import { WebSocketServer, WebSocket } from "ws";
import type { AddressInfo } from "node:net";
import { randomUUID } from "node:crypto";
import { makeMessage, parseClientMessage } from "@pm-ui/protocol";
import type { ClientMessage, Envelope, PermissionMode, SessionInfo } from "@pm-ui/protocol";
import { DEFAULT_MODEL, MODELS } from "./types.js";
import type { ActiveTurn, MockOptions, MockSession, PendingApproval } from "./types.js";

const sleep = (ms: number) => new Promise<void>((r) => setTimeout(r, ms));

/**
 * Mock server dell'agente pm-code: implementa il protocollo v1 su WebSocket
 * con scenari scriptati. Un solo client alla volta (sufficiente per la fase 1).
 */
export class MockAgentServer {
  private wss: WebSocketServer | null = null;

  private readonly sessions = new Map<string, MockSession>();
  /** Client attualmente connesso (un solo client alla volta). */
  private currentWs: WebSocket | null = null;
  /** Stato di autenticazione per connessione (chiave: ws). */
  private readonly authed = new WeakSet<WebSocket>();
  private activeTurn: ActiveTurn | null = null;
  private pendingApproval: PendingApproval | null = null;
  /** Risolve la Promise di awaitApproval in corso (una sola alla volta). */
  private settlePending: ((outcome: "approved" | "rejected" | "expired" | "cancelled") => void) | null = null;
  private cancelled = false;

  constructor(private readonly opts: MockOptions) {
    this.seedSessions();
  }

  /** Due sessioni di esempio già presenti all'avvio. */
  private seedSessions(): void {
    const now = new Date().toISOString();
    const s1: MockSession = {
      info: { id: "sess-demo01", title: "Riassunto progetto pm-code", workspace: "c:\\DevAgentPM\\pm_coding-suite", updatedAt: now, status: "idle" },
      messages: [
        { id: "m-s1u1", role: "user", text: "leggi il README e riassumimi il progetto", ts: now },
        { id: "m-s1a1", role: "assistant", text: "pm-code è un agente di sviluppo in C# con strumenti per file system, git e terminal.", ts: now },
      ],
      model: DEFAULT_MODEL,
      permission: "ask",
    };
    const s2: MockSession = {
      info: { id: "sess-demo02", title: "Build e test", workspace: "c:\\DevAgentPM\\pm_coding-suite", updatedAt: now, status: "idle" },
      messages: [
        { id: "m-s2u1", role: "user", text: "esegui la build del core", ts: now },
        { id: "m-s2a1", role: "assistant", text: "Build completata con successo (0 errori).", ts: now },
      ],
      model: MODELS[0]!.id,
      permission: "readonly",
    };
    this.sessions.set(s1.info.id, s1);
    this.sessions.set(s2.info.id, s2);
  }

  /** Avvia il server su host:port e restituisce la porta effettiva. */
  async start(host: string, port: number): Promise<number> {
    this.wss = new WebSocketServer({ host, port });
    this.wss.on("connection", (ws) => this.onConnection(ws));
    await new Promise<void>((resolve, reject) => {
      this.wss!.once("listening", () => resolve());
      this.wss!.once("error", reject);
    });
    return (this.wss.address() as AddressInfo).port;
  }

  async stop(): Promise<void> {
    if (this.pendingApproval?.expiryTimer) clearTimeout(this.pendingApproval.expiryTimer);
    const settle = this.settlePending;
    this.settlePending = null;
    if (settle) settle("cancelled"); // evita turni appesi che trattengono l'event loop
    this.currentWs?.close();
    await new Promise<void>((resolve) => {
      if (!this.wss) return resolve();
      this.wss.close(() => resolve());
    });
  }

  // -------------------------------------------------------------------------
  // Connessione e routing
  // -------------------------------------------------------------------------

  private onConnection(ws: WebSocket): void {
    if (this.currentWs && this.currentWs.readyState !== WebSocket.CLOSED) {
      // La vecchia connessione è ancora aperta ma non più "corrente" (es. il
      // client ha chiuso senza attendere l'handshake di chiusura): la chiudo
      // prima di accettare il nuovo client, altrimenti resterebbe in OPEN e
      // bloccherebbe tutte le connessioni successive.
      this.currentWs.close();
    }
    this.currentWs = ws;
    let closed = false;
    const onClose = () => {
      if (closed) return; // il close può arrivare due volte (client + server)
      closed = true;
      if (this.currentWs === ws) {
        this.currentWs = null;
        // Un client che va via non deve bloccare la connessione successiva:
        // risolve come "cancelled" eventuali approvazioni/turni appesi.
        const settle = this.settlePending;
        this.settlePending = null;
        if (this.pendingApproval?.expiryTimer) clearTimeout(this.pendingApproval.expiryTimer);
        this.pendingApproval = null;
        if (settle) settle("cancelled");
      }
    };
    ws.on("message", (data) => {
      try {
        void this.handleRaw(ws, JSON.parse(String(data)));
      } catch {
        this.send(ws, makeMessage("error", { code: "bad_request", message: "frame non valido: JSON atteso" }, null));
      }
    });
    ws.on("close", onClose);
    ws.on("error", onClose);
  }

  private send(ws: WebSocket, msg: Envelope): void {
    if (ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify(msg));
  }

  /** Invia un evento alla sessione corrente (o al client se sessionId nullo). */
  private emit(type: string, payload: unknown, sessionId: string | null = null): void {
    const ws = this.currentWs;
    if (!ws) return;
    try {
      this.send(ws, makeMessage(type, payload, sessionId));
    } catch (err) {
      console.error(`[mock-server] emit '${type}' fallito:`, err);
    }
  }

  private async toolCall(session: MockSession, turnId: string, name: string, args: Record<string, unknown>, ok: boolean, summary: string): Promise<void> {
    const callId = `call-${randomUUID().slice(0, 8)}`;
    this.emit("tool_call_started", { turnId, callId, name, args }, session.info.id);
    await sleep(120);
    if (this.cancelled) return;
    this.emit("tool_call_finished", { turnId, callId, name, ok, summary, durationMs: 120 }, session.info.id);
  }

  private async handleRaw(ws: WebSocket, raw: unknown): Promise<void> {
    // Versione non supportata: rifiuto prima di tutto (anche senza hello).
    if (raw && typeof raw === "object" && !Array.isArray(raw) && (raw as { v?: unknown }).v !== 1) {
      this.send(ws, makeMessage("error", { code: "unsupported_version", message: "versione protocollo non supportata (atteso v=1)" }, null));
      return;
    }

    const parsed = parseClientMessage(raw);
    if (!parsed.ok) {
      this.send(ws, makeMessage("error", { code: "bad_request", message: parsed.error }, null));
      return;
    }

    // hello deve essere il primo messaggio e con token valido (stato per connessione).
    if (!this.authed.has(ws)) {
      if (parsed.value.type !== "hello") {
        this.send(ws, makeMessage("error", { code: "unauthorized", message: "atteso 'hello' come primo messaggio" }, null));
        ws.close(1008, "unauthorized");
        return;
      }

      const hello = parsed.value as ClientMessage & { type: "hello" };
      if (hello.payload.token !== this.opts.token) {
        this.send(ws, makeMessage("error", { code: "unauthorized", message: "token non valido" }, null));
        ws.close(1008, "unauthorized");
        return;
      }

      this.authed.add(ws);
      this.send(ws, makeMessage("hello_ok", { serverVersion: this.opts.serverVersion, protocol: 1 }, null));
    }

    await this.dispatch(ws, parsed.value);
  }

  private async dispatch(ws: WebSocket, msg: ClientMessage): Promise<void> {
    if (!this.authed.has(ws) && msg.type !== "hello") {
      this.send(ws, makeMessage("error", { code: "unauthorized", message: "atteso 'hello' come primo messaggio" }, null));
      ws.close(1008, "unauthorized");
      return;
    }

    switch (msg.type) {
      case "hello":
        // già gestito in handleRaw (token verificato, hello_ok emesso)
        return;

      case "list_sessions":
        this.emit("sessions_list", { sessions: [...this.sessions.values()].map((s) => s.info) }, null);
        return;

      case "new_session": {
        const workspace = (msg as ClientMessage & { type: "new_session" }).payload.workspace;
        const id = `sess-${randomUUID().slice(0, 8)}`;
        const now = new Date().toISOString();
        const session: MockSession = {
          info: { id, title: "Nuova sessione", workspace, updatedAt: now, status: "idle" },
          messages: [],
          model: DEFAULT_MODEL,
          permission: "ask",
        };
        this.sessions.set(id, session);
        this.emit("session_state", this.statePayload(session), id);
        return;
      }

      case "open_session": {
        const sessionId = (msg as ClientMessage & { type: "open_session" }).payload.sessionId;
        const session = this.sessions.get(sessionId);
        if (!session) {
          this.emit("error", { code: "unknown_session", message: `sessione '${sessionId}' non trovata` }, null);
          return;
        }
        // Riemissione dello storico: session_state + eventi del messaggio.
        this.emit("session_state", this.statePayload(session), sessionId);
        for (const m of session.messages) {
          if (m.role === "user") this.emit("turn_started", { turnId: `hist-${m.id}` }, sessionId);
          this.emit("assistant_delta", { turnId: `hist-${m.id}`, messageId: m.id, text: m.text }, sessionId);
          this.emit("assistant_done", { turnId: `hist-${m.id}`, messageId: m.id, usage: { promptTokens: 0, completionTokens: 0 } }, sessionId);
          this.emit("turn_finished", { turnId: `hist-${m.id}`, reason: "completed" }, sessionId);
        }
        return;
      }

      case "send_message":
        await this.handleSendMessage(msg);
        return;

      case "cancel":
        this.handleCancel();
        return;

      case "approve": {
        const approvalId = (msg as ClientMessage & { type: "approve" }).payload.approvalId;
        if (!this.resolveApproval(approvalId, "approved")) this.emitErrorForUnknownApproval(approvalId);
        return;
      }

      case "reject": {
        const p = (msg as ClientMessage & { type: "reject" }).payload;
        if (!this.resolveApproval(p.approvalId, "rejected")) this.emitErrorForUnknownApproval(p.approvalId);
        return;
      }

      case "set_model": {
        const modelId = (msg as ClientMessage & { type: "set_model" }).payload.modelId;
        if (!MODELS.some((m) => m.id === modelId)) {
          this.emit("error", { code: "bad_request", message: `modello '${modelId}' sconosciuto` }, msg.sessionId);
          return;
        }
        const session = this.currentSession(msg.sessionId);
        if (session) session.model = modelId;
        this.emit("models_list", { models: MODELS, active: modelId }, msg.sessionId);
        return;
      }

      case "set_permission": {
        const mode = (msg as ClientMessage & { type: "set_permission" }).payload.mode;
        const session = this.currentSession(msg.sessionId);
        if (session) session.permission = mode;
        this.emit("permission_state", { mode, rules: [] }, msg.sessionId);
        return;
      }

    }
  }

  private emitErrorForUnknownApproval(approvalId: string): void {
    this.emit("error", { code: "bad_request", message: `approvazione '${approvalId}' non trovata o già risolta` }, null);
  }

  private currentSession(sessionId: string | null): MockSession | undefined {
    if (!sessionId) return undefined;
    return this.sessions.get(sessionId);
  }

  private statePayload(s: MockSession) {
    return { session: s.info, messages: s.messages, model: s.model, permission: s.permission };
  }

  // -------------------------------------------------------------------------
  // Turni e scenari scriptati
  // -------------------------------------------------------------------------

  private async handleSendMessage(msg: ClientMessage & { type: "send_message" }): Promise<void> {
    const session = this.currentSession(msg.sessionId);
    if (!session) {
      this.emit("error", { code: "unknown_session", message: `sessione '${msg.sessionId}' non trovata` }, msg.sessionId);
      return;
    }
    if (this.activeTurn) {
      this.emit("error", { code: "bad_request", message: "un turno è già in corso" }, session.info.id);
      return;
    }

    const text = msg.payload.text;
    const turnId = `turn-${randomUUID().slice(0, 8)}`;
    const messageId = `msg-${randomUUID().slice(0, 8)}`;
    this.cancelled = false;
    this.activeTurn = { turnId, messageId };

    session.info.status = "running";
    session.info.updatedAt = new Date().toISOString();
    session.messages.push({ id: `u-${randomUUID().slice(0, 8)}`, role: "user", text, ts: new Date().toISOString() });
    this.emit("turn_started", { turnId }, session.info.id);

    try {
      if (text.includes("errore")) await this.scenarioError(session, turnId);
      else if (text.includes("modifica")) await this.scenarioModify(session, turnId, messageId);
      else if (text.includes("comando")) await this.scenarioCommand(session, turnId, messageId);
      else if (text.includes("leggi")) await this.scenarioRead(session, turnId, messageId);
      else await this.scenarioDefault(session, turnId, messageId);
    } finally {
      this.activeTurn = null;
    }
  }

  private finishTurn(session: MockSession, turnId: string, reason: "completed" | "cancelled" | "error"): void {
    if (this.pendingApproval?.expiryTimer) clearTimeout(this.pendingApproval.expiryTimer);
    this.pendingApproval = null;
    session.info.status = "idle";
    session.info.updatedAt = new Date().toISOString();
    this.emit("turn_finished", { turnId, reason }, session.info.id);
  }

  private async streamText(session: MockSession, turnId: string, messageId: string, fullText: string): Promise<void> {
    for (let i = 0; i < fullText.length; i += 4) {
      if (this.cancelled) return;
      this.emit("assistant_delta", { turnId, messageId, text: fullText.slice(i, i + 4) }, session.info.id);
      await sleep(40);
    }
    if (this.cancelled) return;
    const words = fullText.split(/\s+/).length;
    this.emit("assistant_done", { turnId, messageId, usage: { promptTokens: 120 + words * 3, completionTokens: words * 4 } }, session.info.id);
  }

  // --- scenari -------------------------------------------------------------

  private async scenarioRead(session: MockSession, turnId: string, messageId: string): Promise<void> {
    await this.toolCall(session, turnId, "read_file", { path: "README.md" }, true, "README.md letto (42 righe)");
    if (this.cancelled) return;
    await this.streamText(session, turnId, messageId, "Ho letto README.md: il progetto pm-code è un agente di sviluppo in C# con strumenti per file system, git e terminal.");
    if (!this.cancelled) {
      session.messages.push({ id: messageId, role: "assistant", text: "Ho letto README.md: il progetto pm-code è un agente di sviluppo in C# con strumenti per file system, git e terminal.", ts: new Date().toISOString() });
      this.finishTurn(session, turnId, "completed");
    }
  }

  private async scenarioDefault(session: MockSession, turnId: string, messageId: string): Promise<void> {
    await this.streamText(session, turnId, messageId, `Ricevuto: "${truncate(msgText(session))}". Posso aiutarti con letture, modifiche o comandi.`);
    if (!this.cancelled) {
      session.messages.push({ id: messageId, role: "assistant", text: `Ricevuto: "${truncate(msgText(session))}". Posso aiutarti con letture, modifiche o comandi.`, ts: new Date().toISOString() });
      this.finishTurn(session, turnId, "completed");
    }
  }

  private async scenarioError(session: MockSession, turnId: string): Promise<void> {
    await sleep(150);
    if (this.cancelled) return;
    this.emit("error", { code: "bad_request", message: "simulazione di errore del modello" }, session.info.id);
    this.finishTurn(session, turnId, "error");
  }

  private async scenarioModify(session: MockSession, turnId: string, messageId: string): Promise<void> {
    const path = "agent/pm-code/Program.cs";
    const title = "Modifica Program.cs: aggiungi gestione flag --verbose";
    const unifiedDiff = [
      "--- a/agent/pm-code/Program.cs",
      "+++ b/agent/pm-code/Program.cs",
      "@@ -10,6 +10,9 @@",
      " static async Task<int> Main(string[] args)",
      " {",
      "+    bool verbose = args.Contains(\"--verbose\");",
      "+    if (verbose) Console.WriteLine(\"[verbose] avvio agente\");",
      "     var agent = new Agent();",
      "     return await agent.RunAsync(args);",
      " }",
      "",
    ].join("\n");

    const outcome = await this.requestWriteApproval(session, turnId, { path, title, unifiedDiff });
    if (outcome === "cancelled") return; // turn_finished già emesso da handleCancel

    if (outcome === "rejected") {
      // La scrittura non viene applicata: nessun tool_call di write_file.
      await this.streamText(session, turnId, messageId, "Modifica rifiutata: non ho applicato nessuna scrittura.");
      if (!this.cancelled) {
        session.messages.push({ id: messageId, role: "assistant", text: "Modifica rifiutata: non ho applicato nessuna scrittura.", ts: new Date().toISOString() });
        this.finishTurn(session, turnId, "completed");
      }
      return;
    }

    if (outcome === "expired") {
      await this.streamText(session, turnId, messageId, "L'approvazione è scaduta prima della conferma: la modifica non è stata applicata.");
      if (!this.cancelled) {
        session.messages.push({ id: messageId, role: "assistant", text: "L'approvazione è scaduta prima della conferma: la modifica non è stata applicata.", ts: new Date().toISOString() });
        this.finishTurn(session, turnId, "completed");
      }
      return;
    }

    // approved (o auto): applica la scrittura
    await this.toolCall(session, turnId, "write_file", { path }, true, `scrittura applicata su ${path}`);
    await this.streamText(session, turnId, messageId, "Fatto: ho aggiunto la gestione del flag --verbose in Program.cs.");
    if (!this.cancelled) {
      session.messages.push({ id: messageId, role: "assistant", text: "Fatto: ho aggiunto la gestione del flag --verbose in Program.cs.", ts: new Date().toISOString() });
      this.finishTurn(session, turnId, "completed");
    }
  }

  private async scenarioCommand(session: MockSession, turnId: string, messageId: string): Promise<void> {
    const command = "dotnet build agent/pm-code";
    const outcome = await this.requestCommandApproval(session, turnId, { title: "Esegui comando di build", command });

    if (outcome === "cancelled") return;

    if (outcome === "rejected" || outcome === "expired") {
      const summary = outcome === "rejected" ? "comando rifiutato dall'utente" : "approvazione scaduta: comando non eseguito";
      await this.toolCall(session, turnId, "run_command", { command }, false, summary);
      await this.streamText(session, turnId, messageId, `Comando non eseguito (${summary}).`);
      if (!this.cancelled) {
        session.messages.push({ id: messageId, role: "assistant", text: `Comando non eseguito (${summary}).`, ts: new Date().toISOString() });
        this.finishTurn(session, turnId, "completed");
      }
      return;
    }

    await this.toolCall(session, turnId, "run_command", { command }, true, "build completata con successo (0 errori)");
    await this.streamText(session, turnId, messageId, "Comando eseguito: la build è andata a buon fine.");
    if (!this.cancelled) {
      session.messages.push({ id: messageId, role: "assistant", text: "Comando eseguito: la build è andata a buon fine.", ts: new Date().toISOString() });
      this.finishTurn(session, turnId, "completed");
    }
  }

  // -------------------------------------------------------------------------
  // Approvazioni (ask / auto / readonly) e cancel
  // -------------------------------------------------------------------------

  private async requestWriteApproval(
    session: MockSession,
    turnId: string,
    req: { path: string; title: string; unifiedDiff: string },
  ): Promise<"approved" | "rejected" | "expired" | "cancelled"> {
    if (session.permission === "readonly") return this.blockWrite(session, turnId, req.path);
    if (session.permission === "auto") {
      await sleep(50);
      return this.cancelled ? "cancelled" : "approved";
    }
    return this.awaitApproval(session, turnId, { kind: "write", ...req });
  }

  private async requestCommandApproval(
    session: MockSession,
    turnId: string,
    req: { title: string; command: string },
  ): Promise<"approved" | "rejected" | "expired" | "cancelled"> {
    if (session.permission === "readonly") return this.blockWrite(session, turnId, "<comando>");
    if (session.permission === "auto") {
      await sleep(50);
      return this.cancelled ? "cancelled" : "approved";
    }
    return this.awaitApproval(session, turnId, { kind: "command", ...req });
  }

  /** In modalità readonly il server non applica mai scritture/comandi. */
  private async blockWrite(session: MockSession, turnId: string, path: string): Promise<"rejected"> {
    await this.toolCall(session, turnId, "write_file", { path }, false, "scrittura bloccata (modalità readonly)");
    return "rejected";
  }

  private awaitApproval(
    session: MockSession,
    turnId: string,
    req: { kind: "write" | "command"; title: string; path?: string; unifiedDiff?: string; command?: string },
  ): Promise<"approved" | "rejected" | "expired" | "cancelled"> {
    const approvalId = `appr-${randomUUID().slice(0, 8)}`;
    const expiresAt = Date.now() + this.opts.approvalTtlMs;

    return new Promise((resolve) => {
      let settled = false;
      const settle = (outcome: "approved" | "rejected" | "expired" | "cancelled") => {
        if (settled) return;
        settled = true;
        this.settlePending = null;
        if (this.pendingApproval?.expiryTimer && this.pendingApproval.approvalId === approvalId) {
          clearTimeout(this.pendingApproval.expiryTimer);
        }
        this.pendingApproval = null;
        session.info.status = "idle";
        this.emit("approval_resolved", { approvalId, outcome }, session.info.id);
        resolve(outcome);
      };

      const pending: PendingApproval = { approvalId, turnId, kind: req.kind, path: req.path, title: req.title, unifiedDiff: req.unifiedDiff, command: req.command, expiresAt };
      this.pendingApproval = pending;
      this.settlePending = settle;
      session.info.status = "waiting_approval";
      session.info.updatedAt = new Date().toISOString();

      this.emit(
        "approval_request",
        { turnId, approvalId, kind: req.kind, path: req.path, title: req.title, unifiedDiff: req.unifiedDiff, command: req.command, expiresAt: new Date(expiresAt).toISOString() },
        session.info.id,
      );

      pending.expiryTimer = setTimeout(() => settle("expired"), this.opts.approvalTtlMs);
    });
  }

  private resolveApproval(approvalId: string, outcome: "approved" | "rejected"): boolean {
    const pending = this.pendingApproval;
    if (!pending || pending.approvalId !== approvalId) return false;
    // Risolve la Promise di awaitApproval, che a sua volta emette approval_resolved.
    const settle = this.settlePending;
    this.settlePending = null;
    if (settle) settle(outcome);
    return true;
  }

  private handleCancel(): void {
    const turn = this.activeTurn;
    if (!turn) return;
    this.cancelled = true;

    // Approvazioni pendenti: risolvile come "cancelled" (emette approval_resolved).
    if (this.pendingApproval) {
      const settle = this.settlePending;
      this.settlePending = null;
      if (settle) settle("cancelled");
    }

    // Il turno in corso termina con reason "cancelled".
    const session = [...this.sessions.values()].find((s) => s.info.status === "running" || s.info.status === "waiting_approval");
    if (session) {
      this.finishTurn(session, turn.turnId, "cancelled");
    }
  }
}

function truncate(s: string): string {
  return s.length > 60 ? `${s.slice(0, 57)}...` : s;
}

function msgText(session: MockSession): string {
  const last = [...session.messages].reverse().find((m) => m.role === "user");
  return last?.text ?? "";
}
