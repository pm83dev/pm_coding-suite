import { test } from "node:test";
import assert from "node:assert/strict";
import { parseClientMessage, parseServerMessage, makeMessage } from "../index.js";
import { loadExample, listExamples } from "./helpers.js";

const CLIENT_TYPES = new Set(["hello", "list_sessions", "new_session", "open_session", "send_message", "cancel", "approve", "reject", "set_model", "set_permission"]);
const SERVER_TYPES = new Set(["hello_ok", "error", "sessions_list", "session_state", "models_list", "permission_state", "turn_started", "assistant_delta", "assistant_done", "tool_call_started", "tool_call_finished", "approval_request", "approval_resolved", "turn_finished"]);

test("ogni esempio JSON valida nella direzione corretta e il type coincide col nome file", () => {
  const names = listExamples();
  assert.ok(names.length >= 24, `attesi almeno 24 esempi, trovati ${names.length}`);

  for (const name of names) {
    const raw = loadExample(name);
    const msg = raw as { type?: string };
    assert.equal(msg.type, name, `esempio ${name}.json: campo 'type' deve coincidere col nome file`);

    if (CLIENT_TYPES.has(name)) {
      const r = parseClientMessage(raw);
      assert.ok(r.ok, `esempio client '${name}' rifiutato: ${r.ok ? "" : r.error}`);
    } else if (SERVER_TYPES.has(name)) {
      const r = parseServerMessage(raw);
      assert.ok(r.ok, `esempio server '${name}' rifiutato: ${r.ok ? "" : r.error}`);
    } else {
      assert.fail(`esempio '${name}' non corrisponde a nessun tipo di messaggio noto`);
    }
  }
});

test("messaggio con v=2 viene rifiutato", () => {
  const raw = loadExample("hello");
  const bad = { ...(raw as object), v: 2 };
  const r = parseClientMessage(bad);
  assert.equal(r.ok, false);
  if (!r.ok) assert.match(r.error, /versione/i);
});

test("messaggio senza type viene rifiutato", () => {
  const raw = loadExample("hello");
  const { type: _t, ...rest } = raw as Record<string, unknown>;
  const r = parseClientMessage(rest);
  assert.equal(r.ok, false);
  if (!r.ok) assert.match(r.error, /type/);
});

test("messaggio con payload errato viene rifiutato", () => {
  const bad = makeMessage("send_message", { text: 42 }, "sess-1");
  const r = parseClientMessage(bad);
  assert.equal(r.ok, false);
  if (!r.ok) assert.match(r.error, /text/);

  const bad2 = makeMessage("set_permission", { mode: "superuser" }, null);
  const r2 = parseClientMessage(bad2);
  assert.equal(r2.ok, false);
});

test("messaggio della direzione sbagliata viene rifiutato", () => {
  const raw = loadExample("hello_ok"); // evento server
  const r = parseClientMessage(raw);
  assert.equal(r.ok, false);
  if (!r.ok) assert.match(r.error, /non atteso/);

  const raw2 = loadExample("hello"); // comando client
  const r2 = parseServerMessage(raw2);
  assert.equal(r2.ok, false);
});

test("approval_request kind write senza unifiedDiff viene rifiutato", () => {
  const bad = makeMessage("approval_request", { turnId: "t1", approvalId: "a1", kind: "write", path: "x.cs", title: "titolo", expiresAt: new Date().toISOString() }, "s1");
  const r = parseServerMessage(bad);
  assert.equal(r.ok, false);
  if (!r.ok) assert.match(r.error, /unifiedDiff/);
});

test("makeMessage produce una busta valida per entrambe le direzioni", () => {
  const c = makeMessage("cancel", {}, "sess-1");
  assert.equal(parseClientMessage(c).ok, true);
  const s = makeMessage("turn_started", { turnId: "t1" }, "sess-1");
  assert.equal(parseServerMessage(s).ok, true);
});
