import type { MockOptions } from "./types.js";
/**
 * Mock server dell'agente pm-code: implementa il protocollo v1 su WebSocket
 * con scenari scriptati. Un solo client alla volta (sufficiente per la fase 1).
 */
export declare class MockAgentServer {
    private readonly opts;
    private wss;
    private readonly sessions;
    /** Client attualmente connesso (un solo client alla volta). */
    private currentWs;
    /** Stato di autenticazione per connessione (chiave: ws). */
    private readonly authed;
    private activeTurn;
    private pendingApproval;
    /** Risolve la Promise di awaitApproval in corso (una sola alla volta). */
    private settlePending;
    private cancelled;
    constructor(opts: MockOptions);
    /** Due sessioni di esempio già presenti all'avvio. */
    private seedSessions;
    /** Avvia il server su host:port e restituisce la porta effettiva. */
    start(host: string, port: number): Promise<number>;
    stop(): Promise<void>;
    private onConnection;
    private send;
    /** Invia un evento alla sessione corrente (o al client se sessionId nullo). */
    private emit;
    private toolCall;
    private handleRaw;
    private dispatch;
    private emitErrorForUnknownApproval;
    private currentSession;
    private statePayload;
    private handleSendMessage;
    private finishTurn;
    private streamText;
    private scenarioRead;
    private scenarioDefault;
    private scenarioError;
    private scenarioModify;
    private scenarioCommand;
    private requestWriteApproval;
    private requestCommandApproval;
    /** In modalità readonly il server non applica mai scritture/comandi. */
    private blockWrite;
    private awaitApproval;
    private resolveApproval;
    private handleCancel;
}
