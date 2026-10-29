// Client WebSocket di prova per pm-code --serve (protocollo v1, vedi ui/protocol).
// Ogni messaggio ricevuto dal server viene validato con parseServerMessage di @pm-ui/protocol:
// un messaggio che non passa la validazione è un fallimento del test, non solo un avviso.
//
// Prerequisiti: un fake-llm (tools/fake-llm) e un pm-code --serve già avviati — vedi
// docs/serve-mode.md per i comandi esatti. Variabili d'ambiente:
//   SERVE_URL   ws://127.0.0.1:8765/   (default)
//   SERVE_TOKEN deve combaciare con PM_SERVE_TOKEN/ServeSettings:Token del server
//   SERVE_WORKSPACE  path assoluto, deve essere sotto una delle AgentSettings:AllowedWorkspaceRoots
//
// Uso (da dentro ui/, per la risoluzione di ws e @pm-ui/protocol via node_modules):
//   node serve-test-client.mjs
//
// Esegue, in ordine: (a) token errato, (b) turno di solo testo, (c) turno con una tool call,
// (d) send_message durante un turno → busy. Esce con codice 1 se un test fallisce o un
// messaggio non valida contro il protocollo.

import WebSocket from "ws";
import { makeMessage, parseServerMessage } from "@pm-ui/protocol";

const URL = process.env.SERVE_URL ?? "ws://127.0.0.1:8765/";
const TOKEN = process.env.SERVE_TOKEN ?? "testtoken";
const WORKSPACE = process.env.SERVE_WORKSPACE;

if (!WORKSPACE) {
  console.error("SERVE_WORKSPACE non impostata (path assoluto di un workspace consentito).");
  process.exit(1);
}

let failures = 0;
function fail(label, detail) {
  failures++;
  console.error(`✗ ${label}: ${detail}`);
}
function ok(label) {
  console.log(`✓ ${label}`);
}

/** Apre una connessione, invia hello, risolve con { ws, helloOk } (helloOk null se rifiutato). */
function connectAndHello(token) {
  return new Promise((resolve, reject) => {
    const ws = new WebSocket(URL);
    let settled = false;
    ws.on("open", () => {
      ws.send(JSON.stringify(makeMessage("hello", { token, clientName: "serve-test-client", clientVersion: "0.0.1" })));
    });
    ws.on("message", (data) => {
      const raw = JSON.parse(data.toString());
      const r = parseServerMessage(raw);
      if (!r.ok) {
        if (!settled) { settled = true; reject(new Error(`messaggio non valido: ${r.error}`)); }
        return;
      }
      if (r.value.type === "hello_ok" || r.value.type === "error") {
        if (!settled) { settled = true; resolve({ ws, msg: r.value }); }
      }
    });
    ws.on("error", (e) => { if (!settled) { settled = true; reject(e); } });
  });
}

// ── (a) token errato ────────────────────────────────────────────────────────────
async function testBadToken() {
  const { ws, msg } = await connectAndHello("token-sicuramente-sbagliato");
  if (msg.type !== "error" || msg.payload.code !== "unauthorized") {
    fail("(a) token errato", `atteso error/unauthorized, ricevuto ${msg.type} ${JSON.stringify(msg.payload)}`);
  } else {
    ok("(a) token errato → error/unauthorized");
  }
  await new Promise((resolve) => {
    // Il server chiude la connessione dopo l'errore di hello: verifichiamolo (close entro 3s),
    // non solo che il messaggio di errore sia arretrato come testo.
    const timer = setTimeout(() => { fail("(a) chiusura connessione", "il server non ha chiuso il socket dopo l'hello rifiutato"); resolve(); }, 3000);
    ws.on("close", () => { clearTimeout(timer); ok("(a) connessione chiusa dal server dopo il rifiuto"); resolve(); });
    ws.close(); // nel caso il server non chiuda per primo, non lasciamo il processo in attesa
  });
}

/** Connessione autenticata pronta all'uso per i test (b)(c)(d). */
async function connectAuthenticated() {
  const { ws, msg } = await connectAndHello(TOKEN);
  if (msg.type !== "hello_ok") throw new Error(`hello con token corretto rifiutato: ${JSON.stringify(msg.payload)}`);
  if (msg.payload.protocol !== 1) throw new Error(`protocol atteso 1, ricevuto ${msg.payload.protocol}`);
  return ws;
}

/** Invia new_session e attende session_state; restituisce il sessionId. */
function newSession(ws, workspace) {
  return new Promise((resolve, reject) => {
    const onMsg = (data) => {
      const raw = JSON.parse(data.toString());
      const r = parseServerMessage(raw);
      if (!r.ok) { ws.off("message", onMsg); reject(new Error(`messaggio non valido: ${r.error}`)); return; }
      if (r.value.type === "session_state") { ws.off("message", onMsg); resolve(r.value.payload.session.id); }
      else if (r.value.type === "error") { ws.off("message", onMsg); reject(new Error(`new_session rifiutato: ${JSON.stringify(r.value.payload)}`)); }
    };
    ws.on("message", onMsg);
    ws.send(JSON.stringify(makeMessage("new_session", { workspace }, null)));
  });
}

/**
 * Invia send_message e raccoglie gli eventi del turno finché non arriva turn_finished (o error).
 * Ogni messaggio ricevuto viene validato; una validazione fallita interrompe la raccolta.
 */
function sendMessageAndCollect(ws, sessionId, text) {
  return new Promise((resolve, reject) => {
    const events = [];
    const onMsg = (data) => {
      const raw = JSON.parse(data.toString());
      const r = parseServerMessage(raw);
      if (!r.ok) { ws.off("message", onMsg); reject(new Error(`messaggio non valido: ${r.error}`)); return; }
      events.push(r.value);
      if (r.value.type === "turn_finished" || (r.value.type === "error" && events.length === 1)) {
        ws.off("message", onMsg);
        resolve(events);
      }
    };
    ws.on("message", onMsg);
    ws.send(JSON.stringify(makeMessage("send_message", { text }, sessionId)));
  });
}

// ── (b) turno di solo testo: sequenza di eventi attesa ──────────────────────────
async function testTextOnlyTurn(ws, sessionId) {
  const events = await sendMessageAndCollect(ws, sessionId, "Dimmi che sei pronto, senza usare nessun tool.");
  const types = events.map((e) => e.type);
  const expectedPrefix = ["turn_started"];
  const expectedSuffix = ["assistant_done", "turn_finished"];
  const hasDeltas = types.filter((t) => t === "assistant_delta").length > 0;

  const prefixOk = types[0] === "turn_started";
  const hasAssistantDone = types.includes("assistant_done");
  const last = types[types.length - 1];

  if (prefixOk && hasDeltas && hasAssistantDone && last === "turn_finished") {
    ok(`(b) turno di solo testo → sequenza ${types.join(",")}`);
  } else {
    fail("(b) turno di solo testo", `sequenza inattesa: ${types.join(",")}`);
  }

  const finished = events[events.length - 1];
  if (finished.type === "turn_finished" && finished.payload.reason === "completed") {
    ok("(b) turn_finished.reason === 'completed'");
  } else {
    fail("(b) turn_finished.reason", JSON.stringify(finished.payload));
  }

  // Tutti gli assistant_delta/assistant_done di questo turno devono condividere lo stesso
  // turnId e lo stesso messageId (un solo messaggio assistant, nessuna tool call in mezzo).
  const turnIds = new Set(events.map((e) => e.payload.turnId));
  if (turnIds.size === 1) ok("(b) turnId coerente su tutti gli eventi");
  else fail("(b) turnId", `più turnId nello stesso turno: ${[...turnIds]}`);
}

// ── (c) turno con una tool call (lettura file) ───────────────────────────────────
async function testToolCallTurn(ws, sessionId) {
  const events = await sendMessageAndCollect(ws, sessionId, "Leggi hello.txt e dimmi cosa contiene.");
  const types = events.map((e) => e.type);

  const startedIdx = types.indexOf("tool_call_started");
  const finishedIdx = types.indexOf("tool_call_finished");
  if (startedIdx === -1 || finishedIdx === -1 || finishedIdx <= startedIdx) {
    fail("(c) tool_call_started/finished", `sequenza: ${types.join(",")}`);
  } else {
    ok(`(c) tool_call_started → tool_call_finished presenti nell'ordine giusto (sequenza: ${types.join(",")})`);
  }

  const started = events[startedIdx];
  const finished = events[finishedIdx];
  if (started?.payload.name === "read_file" && started.payload.callId === finished?.payload.callId) {
    ok("(c) callId coerente tra tool_call_started e tool_call_finished, name='read_file'");
  } else {
    fail("(c) callId/name", `started=${JSON.stringify(started?.payload)} finished=${JSON.stringify(finished?.payload)}`);
  }

  if (finished?.payload.ok === true) ok("(c) tool_call_finished.ok === true (lettura riuscita)");
  else fail("(c) tool_call_finished.ok", JSON.stringify(finished?.payload));

  if (typeof started?.payload.args === "object" && started.payload.args !== null) {
    ok("(c) tool_call_started.payload.args è un oggetto (non una stringa)");
  } else {
    fail("(c) tool_call_started.payload.args", JSON.stringify(started?.payload.args));
  }

  const last = events[events.length - 1];
  if (last.type === "turn_finished" && last.payload.reason === "completed") {
    ok("(c) turn_finished.reason === 'completed'");
  } else {
    fail("(c) turn_finished finale", JSON.stringify(last.payload));
  }
}

// ── (d) send_message durante un turno → busy ─────────────────────────────────────
async function testBusy(ws, sessionId) {
  // Avvia un turno (non aspettiamo che finisca: la Promise si risolve solo a turn_finished) e,
  // SUBITO dopo averlo inviato, manda un secondo send_message sulla STESSA connessione: deve
  // arrivare "busy" ben prima che il primo turno finisca.
  const firstTurnPromise = sendMessageAndCollect(ws, sessionId, "Quarto turno, quello lungo.");

  const busyResult = await new Promise((resolve, reject) => {
    const onMsg = (data) => {
      const raw = JSON.parse(data.toString());
      const r = parseServerMessage(raw);
      if (!r.ok) { ws.off("message", onMsg); reject(new Error(`messaggio non valido: ${r.error}`)); return; }
      // Il secondo send_message deve ricevere un "error" PRIMA di qualunque evento del primo
      // turno che abbia già consumato un evento (turn_started incluso: è legittimo che arrivi
      // per il primo turno mentre aspettiamo la risposta al secondo send_message, quindi
      // guardiamo solo i messaggi di tipo "error" con sessionId coerente).
      if (r.value.type === "error") { ws.off("message", onMsg); resolve(r.value); }
    };
    ws.on("message", onMsg);
    ws.send(JSON.stringify(makeMessage("send_message", { text: "Secondo messaggio mentre il primo è in corso." }, sessionId)));
    setTimeout(() => { ws.off("message", onMsg); reject(new Error("nessun error/busy ricevuto entro 3s")); }, 3000);
  });

  if (busyResult.payload.message === "busy") {
    ok(`(d) secondo send_message durante un turno → error "busy" (code=${busyResult.payload.code})`);
  } else {
    fail("(d) busy", `messaggio inatteso: ${JSON.stringify(busyResult.payload)}`);
  }

  // Lascia finire il primo turno prima di chiudere, altrimenti il processo pm-code resta con
  // un turno "in corso" quando la connessione si chiude (non e' uno stato di errore, ma sporca
  // il log per chi guarda i test subito dopo).
  await firstTurnPromise;
  ok("(d) il primo turno (quello non rifiutato) è arrivato comunque a turn_finished");
}

async function main() {
  await testBadToken();

  const ws = await connectAuthenticated();
  ok("hello con token corretto → hello_ok");
  const sessionId = await newSession(ws, WORKSPACE);
  ok(`new_session → session_state (sessionId=${sessionId})`);

  await testTextOnlyTurn(ws, sessionId);
  await testToolCallTurn(ws, sessionId);
  await testBusy(ws, sessionId);

  ws.close();

  console.log(failures === 0 ? "\nTutti i test sono passati." : `\n${failures} test falliti.`);
  process.exit(failures === 0 ? 0 : 1);
}

main().catch((err) => {
  console.error("Errore fatale nel test client:", err);
  process.exit(1);
});
