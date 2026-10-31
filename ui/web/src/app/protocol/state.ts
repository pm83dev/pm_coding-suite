import type {
  ApprovalOutcome,
  ApprovalRequestPayload,
  ChatMessage,
  ModelInfo,
  PermissionMode,
  SessionInfo,
  ServerMessage,
  TurnFinishReason,
  UsageInfo,
} from "@pm-ui/protocol";

/** Stato della connessione WebSocket. */
export type ConnectionState = "connecting" | "open" | "closed";

/** Chiamata a uno strumento osservata durante un turno. */
export interface ToolCallState {
  callId: string;
  name: string;
  args: Record<string, unknown>;
  startedAt: string; // ISO8601
  finished: boolean;
  ok?: boolean;
  summary?: string;
  durationMs?: number;
  /** Id del messaggio assistant che ha generato questa tool call (per display inline). */
  messageId?: string;
}

/** Approvazione pendente o risolta. */
export interface ApprovalState {
  approvalId: string;
  turnId: string;
  kind: "write" | "command";
  title: string;
  path?: string;
  unifiedDiff?: string;
  command?: string;
  expiresAt: string; // ISO8601
  outcome?: ApprovalOutcome;
  /** Id del messaggio assistant che ha generato questa approvazione (per display inline). */
  messageId?: string;
}

/** Stato completo dell'app, aggiornato dal reducer. */
export interface ProtocolState {
  connection: ConnectionState;
  /** Ultimo errore di connessione/protocollo da mostrare all'utente. */
  error: string | null;
  sessions: SessionInfo[];
  currentSessionId: string | null;
  messages: ChatMessage[];
  models: ModelInfo[];
  activeModelId: string | null;
  permissionMode: PermissionMode | null;
  /** Turno in corso (null se nessun turno attivo). */
  currentTurnId: string | null;
  /** Testo in streaming del messaggio assistant corrente. */
  streamingMessageId: string | null;
  streamingText: string;
  /**
   * Heartbeat di avanzamento del turno ("Elaborazione in corso…",
   * "Generazione 'tool' in corso…"). Puro feedback di stato: quando non c'è
   * ancora testo in streaming (es. durante una tool call lunga) serve a far
   * capire all'utente che l'agente sta lavorando. Null se assente.
   */
  statusText: string | null;
  toolCalls: ToolCallState[];
  approvals: ApprovalState[];
  /** Usage dell'ultimo turno completato. */
  lastUsage: UsageInfo | null;
  /** Motivo della fine dell'ultimo turno. */
  lastTurnReason: TurnFinishReason | null;
}

export const initialProtocolState: ProtocolState = {
  connection: "closed",
  error: null,
  sessions: [],
  currentSessionId: null,
  messages: [],
  models: [],
  activeModelId: null,
  permissionMode: null,
  currentTurnId: null,
  streamingMessageId: null,
  streamingText: "",
  statusText: null,
  toolCalls: [],
  approvals: [],
  lastUsage: null,
  lastTurnReason: null,
};

/** Aggiunge un messaggio assistant (o lo aggiorna se già presente per id). */
function upsertAssistantMessage(
  messages: ChatMessage[],
  messageId: string,
  text: string,
): ChatMessage[] {
  const idx = messages.findIndex((m) => m.id === messageId);
  if (idx === -1) {
    return [
      ...messages,
      { id: messageId, role: "assistant", text, ts: new Date().toISOString() },
    ];
  }
  const next = messages.slice();
  next[idx] = { ...next[idx]!, text };
  return next;
}

/**
 * Reducer puro: applica un evento server (già validato) allo stato e restituisce
 * il nuovo stato. Non ha effetti collaterali: è testabile in isolamento.
 */
export function applyServerMessage(
  state: ProtocolState,
  msg: ServerMessage,
): ProtocolState {
  switch (msg.type) {
    case "hello_ok":
      // Handshake completato (anche dopo un reconnect): azzera lo stato di turno
      // eventuale lasciato da una connessione precedente. Se il backend è stato
      // riavviato, le sessioni in memoria sono andate perse e un currentTurnId
      // residuo bloccherebbe l'UI con "Turn already in progress". L'errore viene
      // azzerato dal service (applyServerMessage) qui accanto.
      return {
        ...state,
        currentTurnId: null,
        streamingMessageId: null,
        streamingText: "",
        toolCalls: [],
      };

    case "error": {
      // unknown_session: la sessione che l'UI ha aperto non esiste più sul backend
      // (es. riavvio). Azzera lo stato locale per non restare bloccato: torna a
      // "nessuna sessione" e l'utente può crearne una nuova.
      const isUnknownSession = msg.payload.code === "unknown_session";
      const base =
        isUnknownSession
          ? {
              ...state,
              currentSessionId: null,
              messages: [],
              currentTurnId: null,
              streamingMessageId: null,
              streamingText: "",
              toolCalls: [],
            }
          : state;
      return { ...base, error: `${msg.payload.code}: ${msg.payload.message}` };
    }

    case "sessions_list":
      return { ...state, sessions: msg.payload.sessions };

    case "session_state": {
      const p = msg.payload;
      return {
        ...state,
        currentSessionId: p.session.id,
        messages: p.messages,
        activeModelId: p.model,
        permissionMode: p.permission,
        // Aprire una sessione azzera lo stato di turno in corso.
        currentTurnId: null,
        streamingMessageId: null,
        streamingText: "",
        toolCalls: [],
      };
    }

    case "session_deleted": {
      const id = msg.payload.sessionId;
      const sessions = state.sessions.filter((s) => s.id !== id);
      return {
        ...state,
        sessions,
        // Se la sessione aperta viene eliminata, torna a "nessuna sessione".
        currentSessionId:
          state.currentSessionId === id ? null : state.currentSessionId,
        messages: state.currentSessionId === id ? [] : state.messages,
      };
    }

    case "models_list":
      return { ...state, models: msg.payload.models, activeModelId: msg.payload.active };

    case "permission_state":
      return { ...state, permissionMode: msg.payload.mode };

    case "turn_started":
      return {
        ...state,
        currentTurnId: msg.payload.turnId,
        streamingMessageId: null,
        streamingText: "",
        statusText: null,
        toolCalls: [],
        approvals: [],
        lastTurnReason: null,
      };

    case "status":
      // Heartbeat di avanzamento ("Elaborazione in corso…"): salva solo il
      // testo di feedback; non tocca lo streaming (il testo non è parte della
      // risposta). Lo si mostra finché non arriva testo o il turno termina.
      return { ...state, statusText: msg.payload.text };

    case "assistant_delta": {
      const p = msg.payload;
      const text = state.streamingText + p.text;
      return {
        ...state,
        streamingMessageId: p.messageId,
        streamingText: text,
      };
    }

    case "assistant_done": {
      const p = msg.payload;
      const finalText = state.streamingText;
      return {
        ...state,
        streamingMessageId: null,
        streamingText: "",
        lastUsage: p.usage,
        messages: upsertAssistantMessage(state.messages, p.messageId, finalText),
      };
    }

    case "tool_call_started": {
      const p = msg.payload;
      return {
        ...state,
        toolCalls: [
          ...state.toolCalls,
          {
            callId: p.callId,
            name: p.name,
            args: p.args,
            startedAt: msg.ts,
            finished: false,
            messageId: state.streamingMessageId ?? undefined,
          },
        ],
      };
    }

    case "tool_call_finished": {
      const p = msg.payload;
      return {
        ...state,
        toolCalls: state.toolCalls.map((tc) =>
          tc.callId === p.callId
            ? { ...tc, finished: true, ok: p.ok, summary: p.summary, durationMs: p.durationMs }
            : tc,
        ),
      };
    }

    case "approval_request": {
      const p = msg.payload;
      return {
        ...state,
        approvals: [
          ...state.approvals,
          {
            approvalId: p.approvalId,
            turnId: p.turnId,
            kind: p.kind,
            title: p.title,
            path: p.path,
            unifiedDiff: p.unifiedDiff,
            command: p.command,
            expiresAt: p.expiresAt,
            messageId: state.streamingMessageId ?? undefined,
          },
        ],
      };
    }

    case "approval_resolved": {
      const p = msg.payload;
      return {
        ...state,
        approvals: state.approvals.map((a) =>
          a.approvalId === p.approvalId ? { ...a, outcome: p.outcome } : a,
        ),
      };
    }

    case "turn_finished": {
      const p = msg.payload;
      return {
        ...state,
        currentTurnId: null,
        streamingMessageId: null,
        streamingText: "",
        statusText: null,
        lastTurnReason: p.reason,
      };
    }

    default: {
      // Tipo non riconosciuto: non si tocca lo stato.
      const _exhaustive: never = msg;
      return _exhaustive;
    }
  }
}

/** Aggiorna solo lo stato della connessione (gestito dal service, non dal reducer). */
export function withConnection(
  state: ProtocolState,
  connection: ConnectionState,
  error: string | null = null,
): ProtocolState {
  return { ...state, connection, error };
}

/** Aggiunge un messaggio utente locale (prima dell'invio al server). */
export function withUserMessage(state: ProtocolState, text: string): ProtocolState {
  const msg: ChatMessage = {
    id: `local-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`,
    role: "user",
    text,
    ts: new Date().toISOString(),
  };
  return { ...state, messages: [...state.messages, msg] };
}
