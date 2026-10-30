import { Injectable, signal, computed, OnDestroy } from "@angular/core";
import { HubConnection, HubConnectionBuilder } from "@microsoft/signalr";

import {
  applyServerMessage,
  initialProtocolState,
  withConnection,
  withUserMessage,
  type ProtocolState,
} from "./state";
import { loadSettings, saveSettings, type ConnectionSettings } from "./settings";
import type {
  ServerMessage,
  SessionInfo,
  ChatMessage,
  UsageInfo,
  TurnFinishReason,
  PermissionMode,
} from "@pm-ui/protocol";

const MAX_BACKOFF_MS = 15_000;

function mapAgentEventToProtocol(
  eventType: string,
  payload: Record<string, unknown>,
): ServerMessage {
  const now = new Date().toISOString();
  const turnId = (payload["turnId"] as string | undefined) ?? null;
  switch (eventType) {
    case "turn_started":
      return { v: 1, id: `turn-${Date.now()}`, ts: now, sessionId: null, type: "turn_started", payload: { turnId: turnId ?? "" } };
    case "assistant_delta":
      return { v: 1, id: `delta-${Date.now()}`, ts: now, sessionId: null, type: "assistant_delta", payload: { turnId: turnId ?? "", messageId: (payload["messageId"] as string) ?? `msg-${Date.now()}`, text: (payload["text"] as string) ?? "" } };
    case "assistant_done":
      return { v: 1, id: `done-${Date.now()}`, ts: now, sessionId: null, type: "assistant_done", payload: { turnId: turnId ?? "", messageId: (payload["messageId"] as string) ?? "", usage: (payload["usage"] as UsageInfo) ?? { promptTokens: 0, completionTokens: 0 } } };
    case "tool_call_started":
      return { v: 1, id: `tcs-${Date.now()}`, ts: now, sessionId: null, type: "tool_call_started", payload: { turnId: turnId ?? "", callId: (payload["callId"] as string) ?? `call-${Date.now()}`, name: (payload["toolName"] as string) ?? (payload["toolCall"] as string) ?? "", args: (payload["args"] as Record<string, unknown>) ?? {} } };
    case "tool_call_finished":
      return { v: 1, id: `tcf-${Date.now()}`, ts: now, sessionId: null, type: "tool_call_finished", payload: { turnId: turnId ?? "", callId: (payload["callId"] as string) ?? "", ok: (payload["success"] as boolean) ?? true, summary: (payload["summary"] as string) ?? "", durationMs: (payload["durationMs"] as number) ?? 0 } };
    case "turn_finished":
      return { v: 1, id: `tf-${Date.now()}`, ts: now, sessionId: null, type: "turn_finished", payload: { turnId: turnId ?? "", reason: (payload["reason"] as TurnFinishReason) ?? "completed" } };
    case "error":
      return { v: 1, id: `err-${Date.now()}`, ts: now, sessionId: null, type: "error", payload: { code: (payload["code"] as "unauthorized" | "unsupported_version" | "unknown_session" | "bad_request") ?? "bad_request", message: (payload["message"] as string) ?? String(payload["text"] ?? "") } };
    default:
      console.warn("[SignalRProtocol] evento sconosciuto:", eventType);
      return { v: 1, id: `unknown-${Date.now()}`, ts: now, sessionId: null, type: "error", payload: { code: "bad_request" as const, message: eventType } };
  }
}

@Injectable({ providedIn: "root" })
export class SignalRProtocolService implements OnDestroy {
  private readonly state = signal<ProtocolState>(initialProtocolState);
  readonly protocolState = this.state.asReadonly();
  readonly connection = computed(() => this.state().connection);
  readonly error = computed(() => this.state().error);
  readonly sessions = computed(() => this.state().sessions);
  readonly currentSessionId = computed(() => this.state().currentSessionId);
  readonly messages = computed(() => this.state().messages);
  readonly models = computed(() => this.state().models);
  readonly activeModelId = computed(() => this.state().activeModelId);
  readonly permissionMode = computed(() => this.state().permissionMode);
  readonly currentTurnId = computed(() => this.state().currentTurnId);
  readonly streamingText = computed(() => this.state().streamingText);
  readonly toolCalls = computed(() => this.state().toolCalls);
  readonly approvals = computed(() => this.state().approvals);
  readonly lastUsage = computed(() => this.state().lastUsage);
  readonly settings = signal<ConnectionSettings>(loadSettings());
  private hubConnection: HubConnection | null = null;
  private reconnectAttempts = 0;
  private reconnectTimer: ReturnType<typeof setTimeout> | null = null;
  private disposed = false;
  private helloDone = false;
  private userClosed = false;

  connect(): void { this.userClosed = false; this.reconnectAttempts = 0; this.doConnect(); }
  get serverUrl(): string { return this.settings().serverUrl; }

  saveSettings(newSettings: ConnectionSettings): void {
    saveSettings(newSettings);
    this.settings.set(newSettings);
    this.connect();
  }

  disconnect(): void {
    this.userClosed = true;
    this.clearReconnectTimer();
    this.closeHub();
    this.setState(withConnection(this.state(), "closed"));
  }

  listSessions(): void {
    if (!this.hubConnection) return;
    this.hubConnection.invoke("listSessions").catch((err) => console.error("[SignalRProtocol] listSessions failed:", err));
  }

  newSession(workspace: string): void {
    this.setState(withUserMessage(this.state(), ""));
    if (!this.hubConnection) return;
    this.hubConnection.invoke("newSession", { workspace }).then(() => this.listSessions()).catch((err) => {
      console.error("[SignalRProtocol] newSession failed:", err);
      this.setState(withConnection(this.state(), "closed", `Errore creazione sessione: ${err}`));
    });
  }

  openSession(sessionId: string): void {
    if (!this.hubConnection) return;
    this.hubConnection.invoke("listSessions").catch((err) => console.error("[SignalRProtocol] openSession failed:", err));
  }

  sendMessage(text: string): void {
    const sid = this.state().currentSessionId;
    if (!this.hubConnection || !sid) return;
    const msg = { id: `local-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`, role: "user" as const, text, ts: new Date().toISOString() };
    this.setState({ ...this.state(), messages: [...this.state().messages, msg] });
    this.hubConnection.invoke("sendMessage", sid, text).catch((err) => console.error("[SignalRProtocol] sendMessage failed:", err));
  }

  cancel(): void {
    const sid = this.state().currentSessionId;
    if (!this.hubConnection || !sid) return;
    this.hubConnection.invoke("cancel", sid).catch((err) => console.error("[SignalRProtocol] cancel failed:", err));
  }

  approve(approvalId: string): void { console.warn("[SignalRProtocol] approve non implementato:", approvalId); }
  reject(approvalId: string): void { console.warn("[SignalRProtocol] reject non implementato:", approvalId); }

  private doConnect(): void {
    this.closeHub();
    const settings = this.settings();
    const baseUrl = settings.serverUrl.replace(/^https:/, "http:");
    const token = settings.token;
    this.setState(withConnection(this.state(), "connecting"));
    const hub = new HubConnectionBuilder()
      .withUrl(`${baseUrl}/agentHub${token ? `?token=${encodeURIComponent(token)}` : ""}`, { skipNegotiation: true, transport: 2 })
      .withAutomaticReconnect()
      .build();
    this.hubConnection = hub;

    hub.on("HelloResponse", (response: { ok: boolean; serverVersion: string; protocol: number }) => {
      if (response.ok) {
        this.helloDone = true;
        this.reconnectAttempts = 0;
        this.listSessions();
        const lastId = this.state().currentSessionId;
        if (lastId) this.openSession(lastId);
      }
    });
    hub.on("SessionsList", (sessions: Array<{ id: string; title: string; workspace: string; updatedAt: string; status: string }>) => {
      this.setState({ ...this.state(), sessions: sessions as SessionInfo[] });
    });
    hub.on("SessionState", (state: { session: { id: string; title: string; workspace: string; updatedAt: string; status: string }; messages: Array<{ id: string; role: string; text: string; ts: string }>; model: string; permission: string }) => {
      this.setState({ ...this.state(), currentSessionId: state.session.id, messages: state.messages as ChatMessage[], activeModelId: state.model, permissionMode: state.permission as PermissionMode | null });
    });
    hub.on("AgentEvent", (event: { type: string; payload: Record<string, unknown> }) => {
      const protocolMsg = mapAgentEventToProtocol(event.type, event.payload);
      this.setState(applyServerMessage(this.state(), protocolMsg));
    });
    hub.on("Error", (error: { code: string; message: string }) => {
      this.setState(withConnection(this.state(), "closed", `${error.code}: ${error.message}`));
    });
    hub.start().then(() => {
      console.log("[SignalRProtocol] Connesso al server");
      this.setState(withConnection(this.state(), "open"));
      hub.invoke("hello", "pm-coding-suite-ui", "0.2.0").catch((err) => console.error("[SignalRProtocol] hello failed:", err));
    }).catch((err) => {
      console.error("[SignalRProtocol] Connessione fallita:", err);
      this.scheduleReconnect(`Connessione fallita: ${err}`);
    });
    hub.onreconnected(() => {
      this.setState(withConnection(this.state(), "open"));
      this.helloDone = true;
      this.listSessions();
    });
    hub.onreconnecting(() => { this.setState(withConnection(this.state(), "connecting")); });
    hub.onclose(() => { if (!this.userClosed && !this.disposed) this.scheduleReconnect("Connessione persa"); });
  }

  private closeHub(): void { const hub = this.hubConnection; this.hubConnection = null; if (hub) hub.stop().catch(() => {}); }
  private scheduleReconnect(error?: string): void {
    if (this.userClosed || this.disposed) return;
    const delay = Math.min(MAX_BACKOFF_MS, 500 * 2 ** this.reconnectAttempts);
    this.reconnectAttempts += 1;
    if (error) this.setState(withConnection(this.state(), "closed", error));
    this.clearReconnectTimer();
    this.reconnectTimer = setTimeout(() => this.doConnect(), delay);
  }
  private clearReconnectTimer(): void { if (this.reconnectTimer !== null) { clearTimeout(this.reconnectTimer); this.reconnectTimer = null; } }
  private setState(next: ProtocolState): void { this.state.set(next); }
  _setStateForTest(st: ProtocolState): void { this.state.set(st); }
  _applyForTest(msg: ServerMessage): void { this.setState(applyServerMessage(this.state(), msg)); }
  _handleRawForTest(raw: string): void {
    try { const parsed = JSON.parse(raw); this.setState(applyServerMessage(this.state(), mapAgentEventToProtocol(parsed.type, parsed))); }
    catch { this.setState({ ...this.state(), error: `Messaggio non valido: ${raw}` }); }
  }
  ngOnDestroy(): void { this.disposed = true; this.clearReconnectTimer(); this.closeHub(); }
}
