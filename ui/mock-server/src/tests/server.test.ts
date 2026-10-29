import { test, before, after } from "node:test";
import assert from "node:assert/strict";
import WebSocket from "ws";
import { MockAgentServer } from "../server.js";
import { TestClient } from "./client.js";

const TOKEN = "test-token";

let server: MockAgentServer;
let port: number;

before(async () => {
  // TTL approvazioni molto corto per il test di scadenza.
  server = new MockAgentServer({ token: TOKEN, serverVersion: "0.1.0-test", approvalTtlMs: 300 });
  port = await server.start("127.0.0.1", 0);
});

after(async () => {
  await server.stop();
});

async function connect(): Promise<TestClient> {
  const c = new TestClient(port);
  await c.open();
  return c;
}

test("hello con token errato: error unauthorized e chiusura", async () => {
  const c = await connect();
  c.send("hello", { token: "sbagliato", clientName: "pm-ui", clientVersion: "0.1.0" });
  const err = await c.waitFor("error");
  assert.equal(err.payload.code, "unauthorized");
  await c.close();
});

test("messaggio senza hello prima: error unauthorized e chiusura", async () => {
  const c = await connect();
  c.send("list_sessions", {});
  const err = await c.waitFor("error");
  assert.equal(err.payload.code, "unauthorized");
  await c.close();
});

test("hello valido + list_sessions: hello_ok e 2 sessioni di esempio", async () => {
  const c = await connect();
  c.send("hello", { token: TOKEN, clientName: "pm-ui", clientVersion: "0.1.0" });
  const ok = await c.waitFor("hello_ok");
  assert.equal(ok.payload.protocol, 1);
  c.send("list_sessions", {});
  const list = await c.waitFor("sessions_list");
  assert.ok(list.payload.sessions.length >= 2, "attese almeno le 2 sessioni seed");
  await c.close();
});

test("scenario 'leggi': streaming + tool_call di lettura + turn_finished completed", async () => {
  const c = await connect();
  c.send("hello", { token: TOKEN, clientName: "pm-ui", clientVersion: "0.1.0" });
  await c.waitFor("hello_ok");
  c.send("new_session", { workspace: "c:\\test" });
  const state = await c.waitFor("session_state");
  const sessionId = state.payload.session.id;

  c.send("send_message", { text: "leggi il README" }, sessionId);
  await c.waitFor("turn_started");
  const deltas = await c.waitForCount("assistant_delta", 3);
  assert.ok(deltas.every((d) => d.payload.text.length > 0));
  const started = await c.waitFor("tool_call_started");
  assert.equal(started.payload.name, "read_file");
  const finished = await c.waitFor("tool_call_finished");
  assert.equal(finished.payload.ok, true);
  await c.waitFor("assistant_done");
  const done = await c.waitFor("turn_finished");
  assert.equal(done.payload.reason, "completed");
  await c.close();
});

test("scenario 'modifica' con approve: approval_request write + scrittura applicata", async () => {
  const c = await connect();
  c.send("hello", { token: TOKEN, clientName: "pm-ui", clientVersion: "0.1.0" });
  await c.waitFor("hello_ok");
  c.send("new_session", { workspace: "c:\\test" });
  const state = await c.waitFor("session_state");
  const sessionId = state.payload.session.id;

  c.send("send_message", { text: "modifica Program.cs" }, sessionId);
  await c.waitFor("turn_started");
  const req = await c.waitFor("approval_request");
  assert.equal(req.payload.kind, "write");
  assert.ok(String(req.payload.unifiedDiff).includes("+"), "diff unificato con righe aggiunte");

  c.send("approve", { approvalId: req.payload.approvalId }, sessionId);
  const resolved = await c.waitFor("approval_resolved");
  assert.equal(resolved.payload.outcome, "approved");
  const write = await c.waitFor("tool_call_finished", (m) => m.payload.name === "write_file");
  assert.equal(write.payload.ok, true);
  const done = await c.waitFor("turn_finished");
  assert.equal(done.payload.reason, "completed");
  await c.close();
});

test("scenario 'modifica' con reject: scrittura non applicata", async () => {
  const c = await connect();
  c.send("hello", { token: TOKEN, clientName: "pm-ui", clientVersion: "0.1.0" });
  await c.waitFor("hello_ok");
  c.send("new_session", { workspace: "c:\\test" });
  const state = await c.waitFor("session_state");
  const sessionId = state.payload.session.id;

  c.send("send_message", { text: "modifica Program.cs" }, sessionId);
  await c.waitFor("turn_started");
  const req = await c.waitFor("approval_request");
  c.send("reject", { approvalId: req.payload.approvalId, reason: "no grazie" }, sessionId);
  const resolved = await c.waitFor("approval_resolved");
  assert.equal(resolved.payload.outcome, "rejected");
  const done = await c.waitFor("turn_finished");
  assert.equal(done.payload.reason, "completed");
  // Nessuna scrittura applicata.
  assert.ok(!c.received.some((m: any) => m.type === "tool_call_finished" && m.payload.name === "write_file"));
  await c.close();
});

test("scenario 'modifica' con scadenza: outcome expired", async () => {
  const c = await connect();
  c.send("hello", { token: TOKEN, clientName: "pm-ui", clientVersion: "0.1.0" });
  await c.waitFor("hello_ok");
  c.send("new_session", { workspace: "c:\\test" });
  const state = await c.waitFor("session_state");
  const sessionId = state.payload.session.id;

  c.send("send_message", { text: "modifica Program.cs" }, sessionId);
  await c.waitFor("turn_started");
  const req = await c.waitFor("approval_request");
  // Non approvo: aspetto la scadenza (TTL 300 ms).
  const resolved = await c.waitFor("approval_resolved");
  assert.equal(resolved.payload.outcome, "expired");
  assert.ok(req.payload.approvalId === resolved.payload.approvalId);
  const done = await c.waitFor("turn_finished");
  assert.equal(done.payload.reason, "completed");
  await c.close();
});

test("scenario 'comando' con approve: approval_request command + esecuzione", async () => {
  const c = await connect();
  c.send("hello", { token: TOKEN, clientName: "pm-ui", clientVersion: "0.1.0" });
  await c.waitFor("hello_ok");
  c.send("new_session", { workspace: "c:\\test" });
  const state = await c.waitFor("session_state");
  const sessionId = state.payload.session.id;

  c.send("send_message", { text: "esegui il comando di build" }, sessionId);
  await c.waitFor("turn_started");
  const req = await c.waitFor("approval_request");
  assert.equal(req.payload.kind, "command");
  assert.ok(String(req.payload.command).includes("dotnet"));

  c.send("approve", { approvalId: req.payload.approvalId }, sessionId);
  const resolved = await c.waitFor("approval_resolved");
  assert.equal(resolved.payload.outcome, "approved");
  const run = await c.waitFor("tool_call_finished", (m) => m.payload.name === "run_command");
  assert.equal(run.payload.ok, true);
  const done = await c.waitFor("turn_finished");
  assert.equal(done.payload.reason, "completed");
  await c.close();
});

test("modalità readonly: scrittura bloccata senza approval_request", async () => {
  const c = await connect();
  c.send("hello", { token: TOKEN, clientName: "pm-ui", clientVersion: "0.1.0" });
  await c.waitFor("hello_ok");
  c.send("new_session", { workspace: "c:\\test" });
  const state = await c.waitFor("session_state");
  const sessionId = state.payload.session.id;

  c.send("set_permission", { mode: "readonly" }, sessionId);
  const perm = await c.waitFor("permission_state");
  assert.equal(perm.payload.mode, "readonly");

  c.send("send_message", { text: "modifica Program.cs" }, sessionId);
  await c.waitFor("turn_started");
  const blocked = await c.waitFor("tool_call_finished", (m) => m.payload.name === "write_file");
  assert.equal(blocked.payload.ok, false);
  assert.match(String(blocked.payload.summary), /scrittura bloccata/);
  const done = await c.waitFor("turn_finished");
  assert.equal(done.payload.reason, "completed");
  // Nessun approval_request emesso in readonly.
  assert.ok(!c.received.some((m: any) => m.type === "approval_request"));
  await c.close();
});

test("modalità auto: scrittura applicata senza approval_request", async () => {
  const c = await connect();
  c.send("hello", { token: TOKEN, clientName: "pm-ui", clientVersion: "0.1.0" });
  await c.waitFor("hello_ok");
  c.send("new_session", { workspace: "c:\\test" });
  const state = await c.waitFor("session_state");
  const sessionId = state.payload.session.id;

  c.send("set_permission", { mode: "auto" }, sessionId);
  await c.waitFor("permission_state");

  c.send("send_message", { text: "modifica Program.cs" }, sessionId);
  await c.waitFor("turn_started");
  const write = await c.waitFor("tool_call_finished", (m) => m.payload.name === "write_file");
  assert.equal(write.payload.ok, true);
  const done = await c.waitFor("turn_finished");
  assert.equal(done.payload.reason, "completed");
  assert.ok(!c.received.some((m: any) => m.type === "approval_request"));
  await c.close();
});

test("set_model: models_list con active aggiornato; modello sconosciuto rifiutato", async () => {
  const c = await connect();
  c.send("hello", { token: TOKEN, clientName: "pm-ui", clientVersion: "0.1.0" });
  await c.waitFor("hello_ok");
  c.send("new_session", { workspace: "c:\\test" });
  const state = await c.waitFor("session_state");
  const sessionId = state.payload.session.id;

  c.send("set_model", { modelId: "gpt-4o-mini" }, sessionId);
  const models = await c.waitFor("models_list");
  assert.equal(models.payload.active, "gpt-4o-mini");
  assert.ok(models.payload.models.length >= 2);

  c.send("set_model", { modelId: "non-esiste" }, sessionId);
  const err = await c.waitFor("error");
  assert.equal(err.payload.code, "bad_request");
  await c.close();
});

test("cancel a metà streaming: turn_finished cancelled", async () => {
  const c = await connect();
  c.send("hello", { token: TOKEN, clientName: "pm-ui", clientVersion: "0.1.0" });
  await c.waitFor("hello_ok");
  c.send("new_session", { workspace: "c:\\test" });
  const state = await c.waitFor("session_state");
  const sessionId = state.payload.session.id;

  c.send("send_message", { text: "leggi il README" }, sessionId);
  // Aspetto un po' di streaming poi cancello.
  await c.waitForCount("assistant_delta", 2);
  c.send("cancel", {}, sessionId);
  const done = await c.waitFor("turn_finished");
  assert.equal(done.payload.reason, "cancelled");
  await c.close();
});

test("open_session: riemissione session_state + storico", async () => {
  const c = await connect();
  c.send("hello", { token: TOKEN, clientName: "pm-ui", clientVersion: "0.1.0" });
  await c.waitFor("hello_ok");

  // Uso una sessione seed con storico già presente.
  c.send("open_session", { sessionId: "sess-demo01" });
  const state = await c.waitFor("session_state");
  assert.equal(state.payload.session.id, "sess-demo01");
  assert.ok(state.payload.messages.length >= 2);
  // Lo storico viene riemesso come eventi di turno.
  const done = await c.waitFor("turn_finished");
  assert.equal(done.payload.reason, "completed");
  await c.close();
});

test("v=2 rifiutato con unsupported_version", async () => {
  const raw = new WebSocket(`ws://127.0.0.1:${port}`);
  await new Promise<void>((resolve, reject) => {
    raw.once("open", () => resolve());
    raw.once("error", reject);
  });
  const errPromise = new Promise<any>((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error("timeout v=2")), 5000);
    raw.on("message", (data: Buffer) => {
      const m = JSON.parse(String(data));
      if (m.type === "error") {
        clearTimeout(timer);
        resolve(m);
      }
    });
  });
  raw.send(JSON.stringify({ v: 2, id: "x", ts: new Date().toISOString(), sessionId: null, type: "hello", payload: { token: TOKEN, clientName: "pm-ui", clientVersion: "0.1.0" } }));
  const err = await errPromise;
  assert.equal(err.payload.code, "unsupported_version");
  raw.close();
});
