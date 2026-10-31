import { computed, Injectable, OnDestroy, Signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { BehaviorSubject, Subject } from 'rxjs';

import {
  makeMessage,
  parseServerMessage,
  type ModelInfo,
  type PermissionMode,
  type ServerMessage,
  type SessionInfo,
  type UsageInfo,
} from '@pm-ui/protocol';
import { loadSettings, saveSettings, type ConnectionSettings } from './settings';
import {
  applyServerMessage,
  initialProtocolState,
  withUserMessage,
  type ApprovalState,
  type ConnectionState,
  type ProtocolState,
  type ToolCallState,
} from './state';

/**
 * Servizio che gestisce la connessione WebSocket al backend pm-code.
 * Supporta URL WebSocket relativo (quando il frontend è servito dallo stesso backend)
 * e URL assoluto (quando il frontend è servito separatamente).
 */
@Injectable({ providedIn: 'root' })
export class WebSocketProtocolService implements OnDestroy {
  // ---------------------------------------------------------------------------
  // Settings
  // ---------------------------------------------------------------------------

  private readonly settingsSubject = new BehaviorSubject<ConnectionSettings>(loadSettings());
  readonly settings: Signal<ConnectionSettings> = toSignal(this.settingsSubject, {
    initialValue: loadSettings(),
  });

  readonly serverUrl = computed(() => this.settings().serverUrl);

  // ---------------------------------------------------------------------------
  // Reactive state (signals)
  // ---------------------------------------------------------------------------

  private readonly stateSubject = new BehaviorSubject<ProtocolState>(initialProtocolState);
  readonly protocolState: Signal<ProtocolState> = toSignal(this.stateSubject, {
    initialValue: initialProtocolState,
  });

  readonly connection: Signal<ConnectionState> = computed(() => this.protocolState().connection);
  readonly error: Signal<string | null> = computed(() => this.protocolState().error);
  readonly sessions: Signal<SessionInfo[]> = computed(() => this.protocolState().sessions);
  readonly currentSessionId: Signal<string | null> = computed(
    () => this.protocolState().currentSessionId,
  );
  readonly messages: Signal<any[]> = computed(() => this.protocolState().messages);
  readonly models: Signal<ModelInfo[]> = computed(() => this.protocolState().models);
  readonly activeModelId: Signal<string | null> = computed(
    () => this.protocolState().activeModelId,
  );
  readonly permissionMode: Signal<PermissionMode | null> = computed(
    () => this.protocolState().permissionMode,
  );
  readonly currentTurnId: Signal<string | null> = computed(
    () => this.protocolState().currentTurnId,
  );
  readonly streamingText: Signal<string> = computed(() => this.protocolState().streamingText);
  readonly streamingMessageId: Signal<string | null> = computed(() => this.protocolState().streamingMessageId);
  readonly statusText: Signal<string | null> = computed(() => this.protocolState().statusText);
  readonly toolCalls: Signal<ToolCallState[]> = computed(() => this.protocolState().toolCalls);
  readonly approvals: Signal<ApprovalState[]> = computed(() => this.protocolState().approvals);
  readonly lastUsage: Signal<UsageInfo | null> = computed(() => this.protocolState().lastUsage);

  // ---------------------------------------------------------------------------
  // Internal
  // ---------------------------------------------------------------------------

  private ws: WebSocket | null = null;
  private reconnectTimer: ReturnType<typeof setTimeout> | null = null;
  private reconnectDelay = 1000;
  private readonly maxReconnectDelay = 30000;
  private readonly destroy$ = new Subject<void>();
  private messageBuffer: string[] = [];
  private pendingHello = false;
  /** Impedisce connessioni multiple in parallelo. */
  private connecting = false;

  // ---------------------------------------------------------------------------
  // Constructor
  // ---------------------------------------------------------------------------

  constructor() {
    // Auto-connect if settings indicate we should
    const s = this.settings();
    const url = this.buildWebSocketUrl(s.serverUrl);
    setTimeout(() => this.doConnect(url), 0);
  }

  // ---------------------------------------------------------------------------
  // Connection management
  // ---------------------------------------------------------------------------

  /**
   * Costruisce l'URL WebSocket a partire dall'URL HTTP del backend.
   * Trasforma sempre http:// -> ws:// e https:// -> wss://, mantenendo host e porta.
   */
  private buildWebSocketUrl(serverUrl: string): string {
    if (!serverUrl) {
      serverUrl = "http://127.0.0.1:8765";
    }
    // URL assoluto: trasforma http:// -> ws://, https:// -> wss://
    const wsProtocol = serverUrl.startsWith('https://') ? 'wss://' : 'ws://';
    const path = serverUrl.replace(/^https?:\/\//, '');
    return `${wsProtocol}${path}/ws`;
  }

  connect(): void {
    if (this.ws?.readyState === WebSocket.OPEN || this.ws?.readyState === WebSocket.CONNECTING) {
      return; // già connesso o in connessione
    }

    const s = this.settings();
    const url = this.buildWebSocketUrl(s.serverUrl);
    this.doConnect(url);
  }

  private doConnect(url: string): void {
    // Evita connessioni multiple in parallelo
    if (this.connecting) {
      return;
    }
    if (this.ws?.readyState === WebSocket.OPEN || this.ws?.readyState === WebSocket.CONNECTING) {
      return;
    }
    this.connecting = true;
    this.setState((prev) => ({ ...prev, connection: 'connecting', error: null }));

    try {
      this.ws = new WebSocket(url);

      this.ws.onopen = () => {
        console.log('[WS] onopen — connection established');
        this.reconnectDelay = 1000; // reset reconnect delay
        this.pendingHello = true;

        // Send hello message with optional token for backend auth
        const s = this.settings();
        const helloPayload: Record<string, unknown> = {
          clientName: 'pm-coding-suite-web',
          clientVersion: '0.1.0',
        };
        if (s.token) {
          helloPayload['token'] = s.token;
        }

        // Invia hello direttamente in onopen (il WebSocket è già OPEN)
        console.log('[WS] sending hello, readyState:', this.ws?.readyState);
        if (this.ws?.readyState === WebSocket.OPEN) {
          this.ws.send(JSON.stringify(makeMessage('hello', helloPayload)));
          console.log('[WS] hello sent');
        }
      };

      this.ws.onmessage = (event: MessageEvent) => {
        console.log('[WS] onmessage:', event.data);
        const raw = typeof event.data === 'string' ? event.data : event.data.text();
        this.handleRaw(raw);
      };

      this.ws.onerror = (event) => {
        console.log('[WS] onerror');
      };

      this.ws.onclose = (event: CloseEvent) => {
        console.log('[WS] onclose:', event.code, event.reason, event.wasClean);
        this.connecting = false;
        this.pendingHello = false;
        this.ws = null;
        this.setState((prev) => ({ ...prev, connection: 'closed' }));

        // Reconnect if not intentionally closed and hello wasn't successful
        if (!event.wasClean && !(event.code === 1000)) {
          this.scheduleReconnect();
        }
      };
    } catch (err) {
      this.connecting = false;
      this.setState((prev) => ({
        ...prev,
        connection: 'closed',
        error: `Connection error: ${err instanceof Error ? err.message : String(err)}`,
      }));
      this.scheduleReconnect();
    }
  }

  private scheduleReconnect(): void {
    if (this.reconnectTimer) return;

    this.reconnectTimer = setTimeout(() => {
      this.reconnectTimer = null;
      const s = this.settings();
      const url = this.buildWebSocketUrl(s.serverUrl);
      this.doConnect(url);
    }, this.reconnectDelay);

    // Exponential backoff
    this.reconnectDelay = Math.min(this.reconnectDelay * 2, this.maxReconnectDelay);
  }

  disconnect(): void {
    if (this.reconnectTimer) {
      clearTimeout(this.reconnectTimer);
      this.reconnectTimer = null;
    }
    this.connecting = false;
    if (this.ws) {
      this.ws.close(1000, 'Client disconnect');
      this.ws = null;
    }
    this.setState((prev) => ({ ...prev, connection: 'closed' }));
  }

  // ---------------------------------------------------------------------------
  // Message handling
  // ---------------------------------------------------------------------------

  handleRaw(raw: string): void {
    try {
      const result = parseServerMessage(raw);
      if (!result.ok) {
        this.setState((prev) => ({
          ...prev,
          error: `Invalid message: ${result.error}`,
        }));
        return;
      }
      this.applyServerMessage(result.value);
    } catch (err) {
      this.setState((prev) => ({
        ...prev,
        error: `Parse error: ${err instanceof Error ? err.message : String(err)}`,
      }));
    }
  }

  private applyServerMessage(msg: ServerMessage): void {
    // Handle hello_ok specially
    if (msg.type === 'hello_ok') {
      this.pendingHello = false;
      this.connecting = false;
      // Imposta la connessione come 'open' — il protocollo è stato completato
      this.setState((prev) => ({ ...prev, connection: 'open', error: null }));
      // Flush buffered messages
      const buffered = this.messageBuffer.splice(0);
      if (buffered.length > 0) {
        buffered.forEach((raw) => this.handleRaw(raw));
      }
      // Dopo l'handshake: carica l'elenco sessioni e riapri l'ultima sessione (se presente)
      this.listSessions();
      const lastId = this.settings().lastSessionId;
      if (lastId) {
        this.openSession(lastId);
      }
      return;
    }

    // Buffer messages until hello_ok is received
    if (this.pendingHello) {
      this.messageBuffer.push(JSON.stringify(msg));
      return;
    }

    this.setState((prev) => applyServerMessage(prev, msg));

    // Persisti l'ID della sessione corrente per riaprirla dopo un refresh
    if (msg.type === 'session_state') {
      const sid = (msg.payload as { session?: { id?: string } })?.session?.id;
      if (sid) {
        this.saveSettings({ lastSessionId: sid });
      }
    }

    // Se la sessione che stavamo riaprendo non esiste più sul backend (riavvio),
    // rimuovi il lastSessionId persistito: altrimenti ad ogni reconnect
    // riproviamo ad aprirla e riceviamo un altro "unknown_session".
    if (msg.type === 'error' && (msg.payload as { code?: string }).code === 'unknown_session') {
      const current = this.settings();
      if (current.lastSessionId) {
        const { lastSessionId: _removed, ...rest } = current;
        this.saveSettings(rest);
      }
    }
  }

  // ---------------------------------------------------------------------------
  // Public API (mimics original ProtocolService)
  // ---------------------------------------------------------------------------

  saveSettings(s: Partial<ConnectionSettings>): void {
    const current = this.settings();
    const updated = { ...current, ...s };
    this.settingsSubject.next(updated);
    saveSettings(updated);

    // Reconnect if serverUrl changed
    if (s.serverUrl && s.serverUrl !== current.serverUrl) {
      this.disconnect();
      setTimeout(() => this.connect(), 100);
    }
  }

  listSessions(): void {
    this.send('list_sessions', {});
  }

  newSession(workspace: string): void {
    this.send('new_session', { workspace });
  }

  openSession(sessionId: string): void {
    this.send('open_session', { sessionId });
  }

  deleteSession(sessionId: string): void {
    this.send('delete_session', { sessionId });
  }

  sendMessage(text: string): void {
    // Aggiungi il messaggio utente locale subito (così si vede in chat)
    this.setState((prev) => withUserMessage(prev, text));
    // Invia al backend
    this.send('send_message', { text });
  }

  cancel(): void {
    this.send('cancel', {});
  }

  approve(approvalId: string): void {
    console.log('[WS] approve() called, approvalId:', approvalId, 'ws readyState:', this.ws?.readyState);
    this.send('approve', { approvalId });
  }

  reject(approvalId: string, reason?: string): void {
    console.log('[WS] reject() called, approvalId:', approvalId, 'ws readyState:', this.ws?.readyState);
    this.send('reject', { approvalId, reason });
  }

  setPermission(mode: 'readonly' | 'ask' | 'auto'): void {
    this.send('set_permission', { mode });
  }

  private send(type: string, payload: Record<string, unknown>, sessionId?: string): void {
    if (this.ws?.readyState !== WebSocket.OPEN) {
      console.log('[WS] send() skipped: not connected, readyState:', this.ws?.readyState);
      this.setState((prev) => ({
        ...prev,
        error: 'Not connected',
      }));
      return;
    }
    const sid = sessionId ?? this.stateSubject.value.currentSessionId ?? null;
    const msg = JSON.stringify(makeMessage(type, payload, sid));
    console.log('[WS] send()', type, 'sessionId:', sid, 'payload:', JSON.stringify(payload));
    this.ws.send(msg);
  }

  // ---------------------------------------------------------------------------
  // Test helpers
  // ---------------------------------------------------------------------------

  _setStateForTest(st: ProtocolState): void {
    this.stateSubject.next(st);
  }

  _applyForTest(msg: ServerMessage): void {
    this.setState((prev) => applyServerMessage(prev, msg));
  }

  _handleRawForTest(raw: string): void {
    this.handleRaw(raw);
  }

  // ---------------------------------------------------------------------------
  // Internal state management
  // ---------------------------------------------------------------------------

  private setState(updater: (prev: ProtocolState) => ProtocolState): void {
    this.stateSubject.next(updater(this.stateSubject.value));
  }

  // ---------------------------------------------------------------------------
  // Lifecycle
  // ---------------------------------------------------------------------------

  ngOnDestroy(): void {
    this.destroy$.next();
    this.destroy$.complete();
    this.disconnect();
  }
}
