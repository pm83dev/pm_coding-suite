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

/**
 * Voce della timeline della chat, nell'ORDINE DI ARRIVO degli eventi. La chat disegna
 * questa lista così com'è: testo, tool call e approvazioni restano nel punto in cui sono
 * successi, senza dipendere dal messageId (che il backend riusa tra più step del loop).
 * Tool call e approvazioni sono referenziate per id: il loro stato (finished, outcome)
 * si aggiorna in toolCalls/approvals.
 */
export type TimelineEntry =
  | { kind: "message"; key: string; message: ChatMessage }
  | { kind: "tool"; key: string; callId: string }
  | { kind: "approval"; key: string; approvalId: string };

/** Stato completo dell'app, aggiornato dal reducer. */
export interface ProtocolState {
  connection: ConnectionState;
  /** Ultimo errore di connessione/protocollo da mostrare all'utente. */
  error: string | null;
  sessions: SessionInfo[];
  currentSessionId: string | null;
  messages: ChatMessage[];
  /** Conversazione in ordine cronologico (messaggi + tool call + approvazioni). */
  timeline: TimelineEntry[];
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
  timeline: [],
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

function messageEntry(message: ChatMessage): TimelineEntry {
  return { kind: "message", key: `m:${message.id}`, message };
}

/**
 * Chiude il testo in streaming accumulato finora: lo "committa" come messaggio assistant
 * in coda a messages e alla timeline, e svuota streamingText. No-op se non c'è testo.
 * Il backend riusa lo stesso messageId per più step del loop (si azzera solo su
 * assistant_done): se l'id è già usato da un messaggio committato, ne deriva uno univoco
 * così i segmenti di testo non si sovrascrivono a vicenda.
 */
function commitStreamingText(state: ProtocolState, messageId: string | null): ProtocolState {
  if (!state.streamingText) return state;
  const baseId = messageId ?? state.streamingMessageId ?? "assistant";
  const id = state.messages.some((m) => m.id === baseId)
    ? `${baseId}:${state.timeline.length}`
    : baseId;
  const message: ChatMessage = {
    id,
    role: "assistant",
    text: state.streamingText,
    ts: new Date().toISOString(),
  };
  return {
    ...state,
    streamingText: "",
    messages: [...state.messages, message],
    timeline: [...state.timeline, messageEntry(message)],
  };
}

/**
 * Reducer puro: applica un evento server (già validato) allo stato e restituisce
 * il nuovo stato. Non ha effetti collaterali: è testabile in isolamento.
 */
/**
 * Tipi di evento che riguardano una sessione specifica (non lo stato globale
 * come sessions_list o hello_ok). Il backend li instrada solo ai client che
 * hanno quella sessione aperta, ma con un cambio di sessione in corso un
 * evento "in volo" per la sessione precedente può comunque arrivare dopo che
 * lo stato locale è già passato a un'altra: li scartiamo qui per sicurezza.
 * "session_state" è escluso di proposito: è la risposta diretta a new_session/
 * open_session ed è proprio ciò che aggiorna currentSessionId.
 */
const SESSION_SCOPED_TYPES = new Set<string>([
  "turn_started",
  "status",
  "assistant_delta",
  "assistant_done",
  "tool_call_started",
  "tool_call_finished",
  "approval_request",
  "approval_resolved",
  "turn_finished",
  "permission_state",
]);

export function applyServerMessage(
  state: ProtocolState,
  msg: ServerMessage,
): ProtocolState {
  if (
    SESSION_SCOPED_TYPES.has(msg.type) &&
    msg.sessionId != null &&
    state.currentSessionId != null &&
    msg.sessionId !== state.currentSessionId
  ) {
    return state;
  }

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
              timeline: [],
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
      const pending = (p.pendingApprovals ?? []).map((a) => ({
        approvalId: a.approvalId,
        turnId: a.turnId,
        kind: a.kind,
        title: a.title,
        path: a.path,
        unifiedDiff: a.unifiedDiff,
        command: a.command,
        expiresAt: a.expiresAt,
      }));
      return {
        ...state,
        currentSessionId: p.session.id,
        messages: p.messages,
        // Storico + eventuali approvazioni ancora pendenti (in coda: sono le più recenti).
        timeline: [
          ...p.messages.map(messageEntry),
          ...pending.map((a): TimelineEntry => ({ kind: "approval", key: `a:${a.approvalId}`, approvalId: a.approvalId })),
        ],
        activeModelId: p.model,
        permissionMode: p.permission,
        // Aprire una sessione azzera lo stato di turno in corso.
        currentTurnId: null,
        streamingMessageId: null,
        streamingText: "",
        toolCalls: [],
        // Ri-espone le approvazioni ancora pendenti sul server: l'evento
        // approval_request è un broadcast in tempo reale che, se il browser era
        // disconnesso (refresh/riconnessione), viene perso per sempre. Il campo
        // pendingApprovals in session_state è l'unico modo per ripristinarlo.
        // Se il backend non lo manda (vecchia versione) si azzera a [].
        approvals: pending,
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
        timeline: state.currentSessionId === id ? [] : state.timeline,
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
        // Le tool call dei turni precedenti restano: la timeline le referenzia per id e
        // la chat deve continuare a mostrarle. Le approvazioni ancora pendenti di un
        // turno precedente, invece, non sono più azionabili: si tengono solo le risolte.
        approvals: state.approvals.filter((a) => a.outcome),
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
      // Se streamingText è già vuoto (testo già chiuso da una tool call, oppure un
      // assistant_done arrivato dopo che un turn_finished da "cancel" l'ha già azzerato)
      // non si crea un fumetto assistant vuoto: commitStreamingText è un no-op.
      return {
        ...commitStreamingText(state, p.messageId),
        streamingMessageId: null,
        lastUsage: p.usage,
      };
    }

    case "tool_call_started": {
      const p = msg.payload;
      const messageId = p.messageId ?? state.streamingMessageId ?? undefined;
      // Il testo scritto PRIMA di questa tool call va chiuso qui, così la scheda compare
      // dopo di esso nella timeline e l'eventuale testo successivo dopo la scheda.
      const base = commitStreamingText(state, messageId ?? null);
      return {
        ...base,
        // Se questo tool è il primo evento del turno (nessun assistant_delta ancora),
        // streamingMessageId è ancora null: senza impostarlo qui la card non comparirebbe
        // nella vista "in corso" (filtrata su streamingMessageId), solo dopo che il turno
        // finisce e il messaggio viene "commesso" — restava invisibile per tutta la durata
        // dello streaming.
        streamingMessageId: state.streamingMessageId ?? messageId ?? null,
        toolCalls: [
          ...base.toolCalls,
          {
            callId: p.callId,
            name: p.name,
            args: p.args,
            startedAt: msg.ts,
            finished: false,
            messageId,
          },
        ],
        timeline: [...base.timeline, { kind: "tool", key: `t:${p.callId}`, callId: p.callId }],
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
      const base = commitStreamingText(state, state.streamingMessageId);
      return {
        ...base,
        timeline: [...base.timeline, { kind: "approval", key: `a:${p.approvalId}`, approvalId: p.approvalId }],
        approvals: [
          ...base.approvals,
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
      // Testo rimasto in streaming senza assistant_done (il backend normalmente lo
      // emette, ma non per ogni percorso): lo si chiude in chat invece di perderlo.
      return {
        ...commitStreamingText(state, state.streamingMessageId),
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
  return {
    ...state,
    messages: [...state.messages, msg],
    timeline: [...state.timeline, messageEntry(msg)],
  };
}
