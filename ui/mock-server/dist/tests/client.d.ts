import WebSocket from "ws";
/** Client di test minimale per il mock server (protocollo v1). */
export declare class TestClient {
    readonly ws: WebSocket;
    /** Tutti i messaggi ricevuti, in ordine. */
    readonly received: unknown[];
    private waiters;
    constructor(port: number);
    /** Attende l'apertura della connessione. */
    open(): Promise<void>;
    /** Chiude la connessione e risolve quando è effettivamente chiusa. */
    close(): Promise<void>;
    send(type: string, payload: Record<string, unknown>, sessionId?: string | null): void;
    /** Attende il primo messaggio del tipo indicato (o che soddisfa match). */
    waitFor(type?: string, match?: (m: any) => boolean): Promise<any>;
    /** Attende N messaggi di un certo tipo. */
    waitForCount(type: string, count: number): Promise<any[]>;
}
