import type { Envelope } from "./types.js";

/** Risultato di una validazione runtime: valore tipizzato oppure errore leggibile. */
export type ParseResult<T> =
  | { ok: true; value: T }
  | { ok: false; error: string };

// ---------------------------------------------------------------------------
// Helper di validazione (type guard manuali)
// ---------------------------------------------------------------------------

function isRecord(v: unknown): v is Record<string, unknown> {
  return typeof v === "object" && v !== null && !Array.isArray(v);
}

function str(v: unknown): v is string {
  return typeof v === "string";
}

function num(v: unknown): v is number {
  return typeof v === "number" && Number.isFinite(v);
}

function bool(v: unknown): v is boolean {
  return typeof v === "boolean";
}

/** Stringa non vuota. */
function strNonEmpty(v: unknown): v is string {
  return typeof v === "string" && v.length > 0;
}

/** ISO8601 riconoscibile (Date.parse valido). */
function isoDate(v: unknown): v is string {
  return typeof v === "string" && !Number.isNaN(Date.parse(v));
}

function oneOf<T extends string>(v: unknown, allowed: readonly T[]): v is T {
  return typeof v === "string" && (allowed as readonly string[]).includes(v);
}

/** Valida una busta comune; restituisce il payload o un errore leggibile. */
function parseEnvelope(
  raw: unknown,
): ParseResult<{ envelope: Omit<Envelope, "payload">; payload: unknown }> {
  // Se è una stringa, parsala come JSON
  let parsed: unknown;
  if (typeof raw === "string") {
    try {
      parsed = JSON.parse(raw);
    } catch {
      return { ok: false, error: "messaggio non è un oggetto JSON valido" };
    }
  } else {
    parsed = raw;
  }

  if (!isRecord(parsed)) {
    const detail = parsed === null ? "null" : Array.isArray(parsed) ? "array" : typeof parsed;
    return { ok: false, error: `messaggio non è un oggetto JSON (ricevuto: ${detail})` };
  }

  const { v, id, ts, sessionId, type, payload } = parsed;

  if (v !== 1)
    return {
      ok: false,
      error: `versione protocollo non supportata: v=${String(v)} (atteso 1)`,
    };
  if (!strNonEmpty(id))
    return {
      ok: false,
      error: "campo 'id' mancante o non una stringa non vuota",
    };
  if (!isoDate(ts))
    return {
      ok: false,
      error: "campo 'ts' mancante o non una data ISO8601 valida",
    };
  if (sessionId !== null && !strNonEmpty(sessionId))
    return {
      ok: false,
      error: "campo 'sessionId' deve essere stringa non vuota o null",
    };
  if (!strNonEmpty(type))
    return {
      ok: false,
      error: "campo 'type' mancante o non una stringa non vuota",
    };
  if (payload === undefined)
    return { ok: false, error: "campo 'payload' mancante" };
  if (!isRecord(payload))
    return {
      ok: false,
      error: `payload del messaggio '${type}' deve essere un oggetto`,
    };

  return {
    ok: true,
    value: { envelope: { v, id, ts, sessionId, type }, payload },
  };
}

// ---------------------------------------------------------------------------
// Validazione dei payload per tipo di messaggio
// ---------------------------------------------------------------------------

function check(v: ParseResult<never> | null): ParseResult<never> | null {
  if (v === undefined || v === null) return null; // campo assente = ok se opzionale
  return v.ok ? null : v;
}

type PayloadValidator = (
  p: Record<string, unknown>,
) => ParseResult<unknown> | null;

const SESSION_STATUS = ["idle", "running", "waiting_approval"] as const;
const PERMISSION_MODES = ["readonly", "ask", "auto"] as const;
const APPROVAL_KINDS = ["write", "command"] as const;
const APPROVAL_OUTCOMES = [
  "approved",
  "rejected",
  "expired",
  "cancelled",
] as const;
const TURN_FINISH_REASONS = ["completed", "cancelled", "error", "max_steps", "timeout", "aborted"] as const;

function validateSessionInfo(
  p: Record<string, unknown>,
  where: string,
): ParseResult<never> | null {
  if (!isRecord(p))
    return { ok: false, error: `${where} deve essere un oggetto` };
  for (const f of ["id", "title", "workspace", "updatedAt"] as const) {
    if (!strNonEmpty(p[f]))
      return {
        ok: false,
        error: `${where}.${f} mancante o non stringa non vuota`,
      };
  }
  if (!oneOf(p["status"], SESSION_STATUS))
    return {
      ok: false,
      error: `${where}.status deve essere uno di ${SESSION_STATUS.join("|")}`,
    };
  if (!isoDate(p["updatedAt"]))
    return { ok: false, error: `${where}.updatedAt non è una data ISO8601` };
  return null;
}

function validateChatMessage(
  p: Record<string, unknown>,
  where: string,
): ParseResult<never> | null {
  if (!isRecord(p))
    return { ok: false, error: `${where} deve essere un oggetto` };
  if (!strNonEmpty(p["id"]))
    return { ok: false, error: `${where}.id mancante o non stringa non vuota` };
  if (p["role"] !== "user" && p["role"] !== "assistant")
    return {
      ok: false,
      error: `${where}.role deve essere 'user' o 'assistant'`,
    };
  if (!str(p["text"]))
    return { ok: false, error: `${where}.text mancante o non stringa` };
  if (!isoDate(p["ts"]))
    return { ok: false, error: `${where}.ts non è una data ISO8601` };
  return null;
}

function validateModelInfo(
  p: Record<string, unknown>,
  where: string,
): ParseResult<never> | null {
  if (!isRecord(p))
    return { ok: false, error: `${where} deve essere un oggetto` };
  for (const f of ["id", "label", "endpoint"] as const) {
    if (!strNonEmpty(p[f]))
      return {
        ok: false,
        error: `${where}.${f} mancante o non stringa non vuota`,
      };
  }
  if (!num(p["contextSize"]) || (p["contextSize"] as number) <= 0)
    return {
      ok: false,
      error: `${where}.contextSize deve essere un numero > 0`,
    };
  return null;
}

function validateArray<T>(
  v: unknown,
  item: (x: unknown, where: string) => ParseResult<never> | null,
  itemName: string,
): ParseResult<T[]> {
  if (!Array.isArray(v))
    return { ok: false, error: `campo deve essere un array di ${itemName}` };
  for (let i = 0; i < v.length; i++) {
    const err = item(v[i], `${itemName}[${i}]`);
    if (err) return err as ParseResult<never>;
  }
  return { ok: true, value: v as T[] };
}

const clientPayloadValidators: Record<string, PayloadValidator> = {
  hello: (p) => {
    if (!strNonEmpty(p["token"]))
      return {
        ok: false,
        error: "hello.payload.token mancante o non stringa non vuota",
      };
    if (!strNonEmpty(p["clientName"]))
      return {
        ok: false,
        error: "hello.payload.clientName mancante o non stringa non vuota",
      };
    if (!strNonEmpty(p["clientVersion"]))
      return {
        ok: false,
        error: "hello.payload.clientVersion mancante o non stringa non vuota",
      };
    return null;
  },
  list_sessions: () => null,
  new_session: (p) => {
    if (!strNonEmpty(p["workspace"]))
      return {
        ok: false,
        error: "new_session.payload.workspace mancante o non stringa non vuota",
      };
    return null;
  },
  open_session: (p) => {
    if (!strNonEmpty(p["sessionId"]))
      return {
        ok: false,
        error:
          "open_session.payload.sessionId mancante o non stringa non vuota",
      };
    return null;
  },
  delete_session: (p) => {
    if (!strNonEmpty(p["sessionId"]))
      return {
        ok: false,
        error:
          "delete_session.payload.sessionId mancante o non stringa non vuota",
      };
    return null;
  },
  send_message: (p) => {
    if (!str(p["text"]))
      return {
        ok: false,
        error: "send_message.payload.text mancante o non stringa",
      };
    return null;
  },
  cancel: () => null,
  approve: (p) => {
    if (!strNonEmpty(p["approvalId"]))
      return {
        ok: false,
        error: "approve.payload.approvalId mancante o non stringa non vuota",
      };
    return null;
  },
  reject: (p) => {
    if (!strNonEmpty(p["approvalId"]))
      return {
        ok: false,
        error: "reject.payload.approvalId mancante o non stringa non vuota",
      };
    if (p["reason"] !== undefined && !str(p["reason"]))
      return {
        ok: false,
        error: "reject.payload.reason deve essere una stringa se presente",
      };
    return null;
  },
  set_model: (p) => {
    if (!strNonEmpty(p["modelId"]))
      return {
        ok: false,
        error: "set_model.payload.modelId mancante o non stringa non vuota",
      };
    return null;
  },
  set_permission: (p) => {
    if (!oneOf(p["mode"], PERMISSION_MODES))
      return {
        ok: false,
        error: `set_permission.payload.mode deve essere uno di ${PERMISSION_MODES.join("|")}`,
      };
    return null;
  },
};

const serverPayloadValidators: Record<string, PayloadValidator> = {
  hello_ok: (p) => {
    if (!strNonEmpty(p["serverVersion"]))
      return {
        ok: false,
        error:
          "hello_ok.payload.serverVersion mancante o non stringa non vuota",
      };
    if (p["protocol"] !== 1)
      return { ok: false, error: "hello_ok.payload.protocol deve essere 1" };
    return null;
  },
  error: (p) => {
    if (!strNonEmpty(p["code"]))
      return {
        ok: false,
        error: "error.payload.code mancante o non stringa non vuota",
      };
    if (!str(p["message"]))
      return {
        ok: false,
        error: "error.payload.message mancante o non stringa",
      };
    return null;
  },
  sessions_list: (p) => {
    const r = validateArray(
      p["sessions"],
      (x, w) => validateSessionInfo(x as Record<string, unknown>, w),
      "session",
    );
    if (!r.ok) return r;
    return null;
  },
  session_state: (p) => {
    const e1 = check(
      validateSessionInfo(
        p["session"] as Record<string, unknown>,
        "session_state.payload.session",
      ),
    );
    if (e1) return e1;
    const r = validateArray(
      p["messages"],
      (x, w) => validateChatMessage(x as Record<string, unknown>, w),
      "message",
    );
    if (!r.ok) return r;
    if (!strNonEmpty(p["model"]))
      return {
        ok: false,
        error: "session_state.payload.model mancante o non stringa non vuota",
      };
    if (!oneOf(p["permission"], PERMISSION_MODES))
      return {
        ok: false,
        error: `session_state.payload.permission deve essere uno di ${PERMISSION_MODES.join("|")}`,
      };
    return null;
  },
  session_deleted: (p) => {
    if (!strNonEmpty(p["sessionId"]))
      return {
        ok: false,
        error:
          "session_deleted.payload.sessionId mancante o non stringa non vuota",
      };
    return null;
  },
  models_list: (p) => {
    const r = validateArray(
      p["models"],
      (x, w) => validateModelInfo(x as Record<string, unknown>, w),
      "model",
    );
    if (!r.ok) return r;
    if (!strNonEmpty(p["active"]))
      return {
        ok: false,
        error: "models_list.payload.active mancante o non stringa non vuota",
      };
    return null;
  },
  permission_state: (p) => {
    if (!oneOf(p["mode"], PERMISSION_MODES))
      return {
        ok: false,
        error: `permission_state.payload.mode deve essere uno di ${PERMISSION_MODES.join("|")}`,
      };
    if (!Array.isArray(p["rules"]))
      return {
        ok: false,
        error: "permission_state.payload.rules deve essere un array",
      };
    return null;
  },
  turn_started: (p) => {
    if (!strNonEmpty(p["turnId"]))
      return {
        ok: false,
        error: "turn_started.payload.turnId mancante o non stringa non vuota",
      };
    return null;
  },
  assistant_delta: (p) => {
    for (const f of ["turnId", "messageId"] as const) {
      if (!strNonEmpty(p[f]))
        return {
          ok: false,
          error: `assistant_delta.payload.${f} mancante o non stringa non vuota`,
        };
    }
    if (!str(p["text"]))
      return {
        ok: false,
        error: "assistant_delta.payload.text mancante o non stringa",
      };
    return null;
  },
  assistant_done: (p) => {
    for (const f of ["turnId", "messageId"] as const) {
      if (!strNonEmpty(p[f]))
        return {
          ok: false,
          error: `assistant_done.payload.${f} mancante o non stringa non vuota`,
        };
    }
    const u = p["usage"];
    if (!isRecord(u))
      return {
        ok: false,
        error: "assistant_done.payload.usage deve essere un oggetto",
      };
    if (!num(u["promptTokens"]) || !num(u["completionTokens"])) {
      return {
        ok: false,
        error:
          "assistant_done.payload.usage.promptTokens/completionTokens devono essere numeri",
      };
    }
    return null;
  },
  tool_call_started: (p) => {
    for (const f of ["turnId", "callId", "name"] as const) {
      if (!strNonEmpty(p[f]))
        return {
          ok: false,
          error: `tool_call_started.payload.${f} mancante o non stringa non vuota`,
        };
    }
    if (!isRecord(p["args"]))
      return {
        ok: false,
        error: "tool_call_started.payload.args deve essere un oggetto",
      };
    return null;
  },
  tool_call_finished: (p) => {
    for (const f of ["turnId", "callId"] as const) {
      if (!strNonEmpty(p[f]))
        return {
          ok: false,
          error: `tool_call_finished.payload.${f} mancante o non stringa non vuota`,
        };
    }
    if (!bool(p["ok"]))
      return {
        ok: false,
        error: "tool_call_finished.payload.ok deve essere booleano",
      };
    if (!str(p["summary"]))
      return {
        ok: false,
        error: "tool_call_finished.payload.summary mancante o non stringa",
      };
    if (!num(p["durationMs"]) || (p["durationMs"] as number) < 0)
      return {
        ok: false,
        error:
          "tool_call_finished.payload.durationMs deve essere un numero >= 0",
      };
    return null;
  },
  approval_request: (p) => {
    for (const f of ["turnId", "approvalId", "title"] as const) {
      if (!strNonEmpty(p[f]))
        return {
          ok: false,
          error: `approval_request.payload.${f} mancante o non stringa non vuota`,
        };
    }
    if (!oneOf(p["kind"], APPROVAL_KINDS))
      return {
        ok: false,
        error: `approval_request.payload.kind deve essere uno di ${APPROVAL_KINDS.join("|")}`,
      };
    if (p["kind"] === "write") {
      if (!strNonEmpty(p["path"]))
        return {
          ok: false,
          error: "approval_request.payload.path obbligatorio con kind 'write'",
        };
      if (!str(p["unifiedDiff"]))
        return {
          ok: false,
          error:
            "approval_request.payload.unifiedDiff obbligatorio con kind 'write'",
        };
    } else if (p["kind"] === "command") {
      if (!strNonEmpty(p["command"]))
        return {
          ok: false,
          error:
            "approval_request.payload.command obbligatorio con kind 'command'",
        };
    }
    if (!isoDate(p["expiresAt"]))
      return {
        ok: false,
        error: "approval_request.payload.expiresAt non è una data ISO8601",
      };
    return null;
  },
  approval_resolved: (p) => {
    if (!strNonEmpty(p["approvalId"]))
      return {
        ok: false,
        error:
          "approval_resolved.payload.approvalId mancante o non stringa non vuota",
      };
    if (!oneOf(p["outcome"], APPROVAL_OUTCOMES))
      return {
        ok: false,
        error: `approval_resolved.payload.outcome deve essere uno di ${APPROVAL_OUTCOMES.join("|")}`,
      };
    return null;
  },
  turn_finished: (p) => {
    if (!strNonEmpty(p["turnId"]))
      return {
        ok: false,
        error: "turn_finished.payload.turnId mancante o non stringa non vuota",
      };
    if (!oneOf(p["reason"], TURN_FINISH_REASONS))
      return {
        ok: false,
        error: `turn_finished.payload.reason deve essere uno di ${TURN_FINISH_REASONS.join("|")}`,
      };
    return null;
  },
};

// ---------------------------------------------------------------------------
// API pubblica
// ---------------------------------------------------------------------------

function parseTyped<T>(
  raw: unknown,
  types: readonly string[],
  validators: Record<string, PayloadValidator>,
): ParseResult<T> {
  const env = parseEnvelope(raw);
  if (!env.ok) return env;

  const { envelope, payload } = env.value;
  if (!(types as readonly string[]).includes(envelope.type)) {
    return {
      ok: false,
      error: `tipo messaggio '${envelope.type}' non atteso in questa direzione (attesi: ${types.join(", ")})`,
    };
  }

  const validator = validators[envelope.type];
  if (!validator)
    return {
      ok: false,
      error: `validatore non trovato per il tipo '${envelope.type}'`,
    };

  const err = validator(payload as Record<string, unknown>);
  if (err && !err.ok) return { ok: false, error: err.error };

  return { ok: true, value: { ...envelope, payload } as unknown as T };
}

/**
 * Valida a runtime un messaggio ricevuto dal server.
 * Restituisce il messaggio tipizzato oppure un errore leggibile.
 */
export function parseServerMessage<T = import("./types.js").ServerMessage>(
  raw: unknown,
): ParseResult<T> {
  return parseTyped<T>(
    raw,
    [
      "hello_ok",
      "error",
      "sessions_list",
      "session_state",
      "session_deleted",
      "models_list",
      "permission_state",
      "turn_started",
      "assistant_delta",
      "assistant_done",
      "tool_call_started",
      "tool_call_finished",
      "approval_request",
      "approval_resolved",
      "turn_finished",
    ],
    serverPayloadValidators,
  );
}

/**
 * Valida a runtime un messaggio da inviare al server.
 * Restituisce il messaggio tipizzato oppure un errore leggibile.
 */
export function parseClientMessage<T = import("./types.js").ClientMessage>(
  raw: unknown,
): ParseResult<T> {
  return parseTyped<T>(
    raw,
    [
      "hello",
      "list_sessions",
      "new_session",
      "open_session",
      "delete_session",
      "send_message",
      "cancel",
      "approve",
      "reject",
      "set_model",
      "set_permission",
    ],
    clientPayloadValidators,
  );
}

/** Costruisce un messaggio v1 valido (id/ts generati). Vedi {@link makeMessage}. */
export { makeMessage } from "./make-message.js";
export type { MakeMessageOptions } from "./make-message.js";
