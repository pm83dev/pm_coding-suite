import { Injectable, signal, computed } from "@angular/core";

import { makeMessage, parseServerMessage, type ServerMessage } from "@pm-ui/protocol";

import {
  applyServerMessage,
  initialProtocolState,
  withConnection,
  withUserMessage,
  type ConnectionState,
  type ProtocolState,
} from "./state";
import { loadSettings, saveSettings, type ConnectionSettings } from "./settings";

const MAX_BACKOFF_MS = 15_000;

/**
 * Gestisce la connessione WebSocket al mock server: hello, validazione dei
 * messaggi in ingresso, riconnessione con backoff esponenziale (max 15 s) e
 * riapertura dell'ultima sessione dopo la riconnessione.
 *
 * Lo stato è esposto come signal; gli eventi server vengono applicati con il
 * reducer puro {@link applyServerMessage}.
 */
@Injectable({ providedIn: "root" })
export class ProtocolService {
  private readonly state = signal<ProtocolState>(initialProtocolState);

  /** Stato completo (connessione, sessioni, messaggi, modello, permessi, turno, approvazioni, usage). */
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

  /** Impostazioni di connessione (URL + token), salvate in sessionStorage. */
  readonly settings = signal<ConnectionSettings>(loadSettings());

  private ws: WebSocket | null = null;
  private reconnectAttempts = 0;
  private reconnectTimer: ReturnType<typeof setTimeout> | null = null;
  private disposed = false;
  /** true finché il server non ha confermato hello_ok. */
  private helloDone = false;
  /** true se la chiusura è stata richiesta esplicitamente dall'utente. */
  private userClosed = false;

  constructor() {
    // L'avvio della connessione è esplicito (connect()): così i test unitari
    // partono dallo stato iniziale "closed" senza WebSocket.
  }

  /** Avvia (o riavvia) la connessione al server. */
  connect(): void {
    this.userClosed = false;
    this.reconnectAttempts = 0;
    this.doConnect();
  }

  /** URL del server corrente (solo per display; il token non è mai nell'URL). */
  get serverUrl(): string {
    return this.settings().serverUrl;
  }

  /** Salva le impostazioni e riavvia la connessione. */
  saveSettings(settings: ConnectionSettings): void {
    saveSettings(settings);
    this.settings.set(settings);
    this.connect();
  }

  /** Chiude la connessione senza riconnettersi. */
  disconnect(): void {
    this.userClosed = true;
    this.clearReconnectTimer();
    this.closeSocket();
    this.setState(withConnection(this.state(), "closed"));
  }

  // ---------------------------------------------------------------------------
  // Comandi (client -> server)
  // ---------------------------------------------------------------------------

  sendMessage(text: string): void {
    const trimmed = text.trim();
    if (!trimmed || !this.currentSessionId()) return;
    this.setState(withUserMessage(this.state(), trimmed));
    this.send("send_message", { text: trimmed });
  }

  cancel(): void {
    this.send("cancel", {});
  }

  approve(approvalId: string): void {
    this.send("approve", { approvalId });
  }

  reject(approvalId: string, reason?: string): void {
    this.send("reject", reason ? { approvalId, reason } : { approvalId });
  }

  setModel(modelId: string): void {
    this.send("set_model", { modelId });
  }

  setPermission(mode: "readonly" | "ask" | "auto"): void {
    this.send("set_permission", { mode });
  }

  listSessions(): void {
    this.send("list_sessions", {});
  }

  newSession(workspace: string): void {
    this.send("new_session", { workspace });
  }

  openSession(sessionId: string): void {
    this.send("open_session", { sessionId });
  }

  // ---------------------------------------------------------------------------
  // Connessione
  // ---------------------------------------------------------------------------

  private doConnect(): void {
    if (this.disposed) return;
    this.closeSocket();
    this.clearReconnectTimer();

    const url = this.settings().serverUrl;
    if (!url) {
      this.setState(withConnection(this.state(), "closed", "Nessun URL server configurato"));
      return;
    }

    this.setState(withConnection(this.state(), "connecting"));
    let ws: WebSocket;
    try {
      ws = new WebSocket(url);
    } catch (e) {
      this.scheduleReconnect(`URL non valido: ${String(e)}`);
      return;
    }
    this.ws = ws;

    ws.onopen = () => {
      this.reconnectAttempts = 0;
      this.helloDone = false;
      this.setState(withConnection(this.state(), "open"));
      this.sendHello();
    };

    ws.onmessage = (ev: MessageEvent) => {
      this.handleRawMessage(typeof ev.data === "string" ? ev.data : "");
    };

    ws.onclose = () => {
      this.setState(withConnection(this.state(), "closed"));
      if (!this.userClosed && !this.disposed) {
        this.scheduleReconnect("Connessione persa");
      }
    };

    ws.onerror = () => {
      // onclose seguirà; qui solo per log.
    };
  }

  private sendHello(): void {
    const { token } = this.settings();
    this.send("hello", { token, clientName: "pm-ui", clientVersion: "0.1.0" });
  }

  private handleRawMessage(raw: string): void {
    let obj: unknown;
    try {
      obj = JSON.parse(raw);
    } catch (e) {
      this.setState({ ...this.state(), error: `Messaggio non valido dal server: JSON non valido (${String(e)})` });
      return;
    }
    const parsed = parseServerMessage(obj);
    if (!parsed.ok) {
      this.setState({ ...this.state(), error: `Messaggio non valido dal server: ${parsed.error}` });
      return;
    }
    const msg = parsed.value;

    if (msg.type === "hello_ok") {
      this.helloDone = true;
      // Dopo hello_ok: elenca le sessioni e, se c'era un'ultima sessione, riaprila.
      this.listSessions();
      const lastId = this.state().currentSessionId;
      if (lastId) {
        this.openSession(lastId);
      }
      return;
    }

    if (msg.type === "error" && msg.payload.code === "unauthorized") {
      // Token errato: il server chiude. Non riconnettersi in loop.
      this.setState(withConnection(this.state(), "closed", `Autenticazione rifiutata: ${msg.payload.message}`));
      this.userClosed = true;
      return;
    }

    this.setState(applyServerMessage(this.state(), msg));
  }

  private send(type: string, payload: unknown): void {
    const ws = this.ws;
    if (!ws || ws.readyState !== WebSocket.OPEN) return;
    // makeMessage aggiunge la busta (v, id, ts, sessionId) al tipo+payload.
    const envelope = makeMessage(type, payload, this.currentSessionId());
    ws.send(JSON.stringify(envelope));
  }

  private scheduleReconnect(error?: string): void {
    if (this.userClosed || this.disposed) return;
    const delay = Math.min(MAX_BACKOFF_MS, 500 * 2 ** this.reconnectAttempts);
    this.reconnectAttempts += 1;
    if (error) {
      this.setState(withConnection(this.state(), "closed", error));
    }
    this.clearReconnectTimer();
    this.reconnectTimer = setTimeout(() => {
      // La sessione corrente viene riaperta in onopen via hello_ok.
      this.doConnect();
    }, delay);
  }

  private clearReconnectTimer(): void {
    if (this.reconnectTimer !== null) {
      clearTimeout(this.reconnectTimer);
      this.reconnectTimer = null;
    }
  }

  private closeSocket(): void {
    const ws = this.ws;
    this.ws = null;
    if (ws) {
      ws.onopen = null;
      ws.onmessage = null;
      ws.onclose = null;
      ws.onerror = null;
      try {
        ws.close();
      } catch {
        // già chiuso
      }
    }
  }

  private setState(next: ProtocolState): void {
    this.state.set(next);
  }

  /** Per test: forza lo stato (es. simulare una sessione corrente). */
  _setStateForTest(state: ProtocolState): void {
    this.state.set(state);
  }

  /** Per test: applica un messaggio server già validato. */
  _applyForTest(msg: ServerMessage): void {
    this.setState(applyServerMessage(this.state(), msg));
  }

  /** Per test: simula la ricezione di un messaggio grezzo dal server. */
  _handleRawForTest(raw: string): void {
    this.handleRawMessage(raw);
  }

  ngOnDestroy(): void {
    this.disposed = true;
    this.clearReconnectTimer();
    this.closeSocket();
  }
}
