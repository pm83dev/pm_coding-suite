import { describe, expect, it } from "vitest";

import { makeMessage, type ServerMessage } from "@pm-ui/protocol";

import {
  applyServerMessage,
  initialProtocolState,
  withConnection,
  withUserMessage,
  type ProtocolState,
} from "./state";

/** Costruisce un messaggio server valido per i test. */
function msg(type: string, payload: unknown, sessionId: string | null = null): ServerMessage {
  return makeMessage(type, payload, sessionId) as ServerMessage;
}

describe("applyServerMessage (reducer)", () => {
  it("stato iniziale: connessione closed, nessun dato", () => {
    expect(initialProtocolState.connection).toBe("closed");
    expect(initialProtocolState.sessions).toEqual([]);
    expect(initialProtocolState.currentSessionId).toBeNull();
    expect(initialProtocolState.messages).toEqual([]);
  });

  it("sessions_list aggiorna l'elenco sessioni", () => {
    const sessions = [
      { id: "s1", title: "A", workspace: "/w", updatedAt: "2026-01-01T00:00:00Z", status: "idle" as const },
      { id: "s2", title: "B", workspace: "/w", updatedAt: "2026-01-02T00:00:00Z", status: "running" as const },
    ];
    const next = applyServerMessage(initialProtocolState, msg("sessions_list", { sessions }));
    expect(next.sessions).toEqual(sessions);
  });

  it("session_state imposta sessione corrente, messaggi, modello e permessi", () => {
    const payload = {
      session: { id: "s1", title: "A", workspace: "/w", updatedAt: "2026-01-01T00:00:00Z", status: "idle" as const },
      messages: [{ id: "m1", role: "user" as const, text: "ciao", ts: "2026-01-01T00:00:01Z" }],
      model: "gpt-x",
      permission: "ask" as const,
    };
    const next = applyServerMessage(initialProtocolState, msg("session_state", payload, "s1"));
    expect(next.currentSessionId).toBe("s1");
    expect(next.messages).toEqual(payload.messages);
    expect(next.activeModelId).toBe("gpt-x");
    expect(next.permissionMode).toBe("ask");
  });

  it("session_deleted rimuove la sessione dall'elenco (sezione corrente non toccata)", () => {
    const sessions = [
      { id: "s1", title: "A", workspace: "/w", updatedAt: "2026-01-01T00:00:00Z", status: "idle" as const },
      { id: "s2", title: "B", workspace: "/w", updatedAt: "2026-01-02T00:00:00Z", status: "idle" as const },
    ];
    let s: ProtocolState = { ...initialProtocolState, sessions, currentSessionId: "s2" };
    s = applyServerMessage(s, msg("session_deleted", { sessionId: "s1" }, "s2"));
    expect(s.sessions).toEqual([sessions[1]]);
    // La sessione corrente (s2) non è quella eliminata: resta invariata.
    expect(s.currentSessionId).toBe("s2");
  });

  it("session_deleted azzera la sessione corrente se eliminata", () => {
    const sessions = [
      { id: "s1", title: "A", workspace: "/w", updatedAt: "2026-01-01T00:00:00Z", status: "idle" as const },
    ];
    let s: ProtocolState = {
      ...initialProtocolState,
      sessions,
      currentSessionId: "s1",
      messages: [{ id: "m1", role: "user" as const, text: "ciao", ts: "2026-01-01T00:00:01Z" }],
    };
    s = applyServerMessage(s, msg("session_deleted", { sessionId: "s1" }, "s1"));
    expect(s.sessions).toEqual([]);
    expect(s.currentSessionId).toBeNull();
    expect(s.messages).toEqual([]);
  });

  it("turn_started azzera streaming e tool call", () => {
    let s: ProtocolState = { ...initialProtocolState, streamingText: "vecchio", toolCalls: [{ callId: "c0", name: "x", args: {}, startedAt: "", finished: false }] };
    s = applyServerMessage(s, msg("turn_started", { turnId: "t1" }, "s1"));
    expect(s.currentTurnId).toBe("t1");
    expect(s.streamingText).toBe("");
    expect(s.toolCalls).toEqual([]);
  });

  it("assistant_delta accumula solo il testo in streaming (nessun messaggio finché non c'è assistant_done)", () => {
    let s = applyServerMessage(initialProtocolState, msg("turn_started", { turnId: "t1" }, "s1"));
    s = applyServerMessage(s, msg("assistant_delta", { turnId: "t1", messageId: "m1", text: "Ciao" }, "s1"));
    s = applyServerMessage(s, msg("assistant_delta", { turnId: "t1", messageId: "m1", text: " mondo" }, "s1"));
    expect(s.streamingText).toBe("Ciao mondo");
    // Il messaggio non viene aggiunto durante lo streaming per evitare duplicati
    expect(s.messages).toHaveLength(0);
  });

  it("assistant_done finalizza il messaggio e salva l'usage", () => {
    let s = applyServerMessage(initialProtocolState, msg("turn_started", { turnId: "t1" }, "s1"));
    s = applyServerMessage(s, msg("assistant_delta", { turnId: "t1", messageId: "m1", text: "ok" }, "s1"));
    s = applyServerMessage(s, msg("assistant_done", { turnId: "t1", messageId: "m1", usage: { promptTokens: 10, completionTokens: 5 } }, "s1"));
    expect(s.lastUsage).toEqual({ promptTokens: 10, completionTokens: 5 });
    expect(s.streamingText).toBe("");
    expect(s.streamingMessageId).toBeNull();
    // Il messaggio viene creato una sola volta, al termine
    expect(s.messages).toHaveLength(1);
    expect(s.messages[0]?.text).toBe("ok");
  });

  it("tool_call_started/finished traccia la chiamata", () => {
    let s = applyServerMessage(initialProtocolState, msg("turn_started", { turnId: "t1" }, "s1"));
    s = applyServerMessage(s, msg("tool_call_started", { turnId: "t1", callId: "c1", name: "read_file", args: { path: "a.ts" } }, "s1"));
    expect(s.toolCalls).toHaveLength(1);
    expect(s.toolCalls[0]?.finished).toBe(false);
    s = applyServerMessage(s, msg("tool_call_finished", { turnId: "t1", callId: "c1", ok: true, summary: "ok", durationMs: 42 }, "s1"));
    expect(s.toolCalls[0]?.finished).toBe(true);
    expect(s.toolCalls[0]?.ok).toBe(true);
    expect(s.toolCalls[0]?.durationMs).toBe(42);
  });

  it("approval_request aggiunge un'approvazione pendente", () => {
    const s = applyServerMessage(initialProtocolState, msg("approval_request", {
      turnId: "t1", approvalId: "a1", kind: "write", path: "a.ts", title: "Modifica a.ts",
      unifiedDiff: "--- a\n+++ b", expiresAt: "2026-01-01T01:00:00Z",
    }, "s1"));
    expect(s.approvals).toHaveLength(1);
    expect(s.approvals[0]?.approvalId).toBe("a1");
    expect(s.approvals[0]?.outcome).toBeUndefined();
  });

  it("approval_resolved chiude l'approvazione con l'esito", () => {
    let s = applyServerMessage(initialProtocolState, msg("approval_request", {
      turnId: "t1", approvalId: "a1", kind: "command", title: "cmd", command: "ls", expiresAt: "2026-01-01T01:00:00Z",
    }, "s1"));
    s = applyServerMessage(s, msg("approval_resolved", { approvalId: "a1", outcome: "approved" }, "s1"));
    expect(s.approvals[0]?.outcome).toBe("approved");
  });

  it("turn_finished chiude il turno e salva il motivo", () => {
    let s = applyServerMessage(initialProtocolState, msg("turn_started", { turnId: "t1" }, "s1"));
    s = applyServerMessage(s, msg("turn_finished", { turnId: "t1", reason: "completed" }, "s1"));
    expect(s.currentTurnId).toBeNull();
    expect(s.lastTurnReason).toBe("completed");
  });

  it("status salva il testo di avanzamento (heartbeat)", () => {
    let s = applyServerMessage(initialProtocolState, msg("turn_started", { turnId: "t1" }, "s1"));
    expect(s.statusText).toBeNull();
    s = applyServerMessage(s, msg("status", { text: "Elaborazione in corso..." }, "s1"));
    expect(s.statusText).toBe("Elaborazione in corso...");
    // Il testo non è parte della risposta: lo streaming resta vuoto.
    expect(s.streamingText).toBe("");
  });

  it("turn_started azzera statusText", () => {
    let s = applyServerMessage(initialProtocolState, msg("turn_started", { turnId: "t1" }, "s1"));
    s = applyServerMessage(s, msg("status", { text: "vecchio" }, "s1"));
    s = applyServerMessage(s, msg("turn_finished", { turnId: "t1", reason: "completed" }, "s1"));
    s = applyServerMessage(s, msg("turn_started", { turnId: "t2" }, "s1"));
    expect(s.statusText).toBeNull();
  });

  it("turn_finished azzera statusText", () => {
    let s = applyServerMessage(initialProtocolState, msg("turn_started", { turnId: "t1" }, "s1"));
    s = applyServerMessage(s, msg("status", { text: "Generazione 'read_file' in corso..." }, "s1"));
    expect(s.statusText).toBe("Generazione 'read_file' in corso...");
    s = applyServerMessage(s, msg("turn_finished", { turnId: "t1", reason: "completed" }, "s1"));
    expect(s.statusText).toBeNull();
  });

  it("error imposta il messaggio di errore", () => {
    const s = applyServerMessage(initialProtocolState, msg("error", { code: "bad_request", message: "payload errato" }));
    expect(s.error).toBe("bad_request: payload errato");
  });

  it("models_list e permission_state aggiornano modello e permessi", () => {
    let s = applyServerMessage(initialProtocolState, msg("models_list", {
      models: [{ id: "m1", label: "M1", endpoint: "http://x", contextSize: 8000 }], active: "m1",
    }));
    expect(s.models).toHaveLength(1);
    expect(s.activeModelId).toBe("m1");
    s = applyServerMessage(s, msg("permission_state", { mode: "auto", rules: [] }));
    expect(s.permissionMode).toBe("auto");
  });

  it("withConnection aggiorna solo connessione ed errore", () => {
    const s = withConnection(initialProtocolState, "open");
    expect(s.connection).toBe("open");
    expect(s.error).toBeNull();
  });

  it("withUserMessage aggiunge un messaggio utente locale", () => {
    const s = withUserMessage(initialProtocolState, "ciao");
    expect(s.messages).toHaveLength(1);
    expect(s.messages[0]?.role).toBe("user");
    expect(s.messages[0]?.text).toBe("ciao");
  });
});
