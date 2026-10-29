import WebSocket from "ws";
import { randomUUID } from "node:crypto";

/** Client di test minimale per il mock server (protocollo v1). */
export class TestClient {
  readonly ws: WebSocket;
  /** Tutti i messaggi ricevuti, in ordine. */
  readonly received: unknown[] = [];
  private waiters: Array<{ match: (m: any) => boolean; resolve: (m: any) => void }> = [];

  constructor(port: number) {
    this.ws = new WebSocket(`ws://127.0.0.1:${port}`);
    this.ws.on("message", (data) => {
      const msg = JSON.parse(String(data));
      this.received.push(msg);
      // Risolve TUTTI i waiter soddisfatti: waitForCount e più attese dello
      // stesso tipo richiedono un waiter per messaggio, non uno solo.
      for (let i = this.waiters.length - 1; i >= 0; i--) {
        if (this.waiters[i]!.match(msg)) {
          const [w] = this.waiters.splice(i, 1);
          w!.resolve(msg);
        }
      }
    });
  }

  /** Attende l'apertura della connessione. */
  open(): Promise<void> {
    return new Promise((resolve, reject) => {
      this.ws.once("open", () => resolve());
      this.ws.once("error", reject);
    });
  }

  /** Chiude la connessione e risolve quando è effettivamente chiusa. */
  close(): Promise<void> {
    return new Promise((resolve) => {
      if (this.ws.readyState === WebSocket.CLOSED) return resolve();
      // Timeout di sicurezza: se il server non completa l'handshake di chiusura,
      // il test non deve restare appeso per sempre.
      const timer = setTimeout(() => resolve(), 2000);
      this.ws.once("close", () => {
        clearTimeout(timer);
        resolve();
      });
      this.ws.close();
    });
  }

  send(type: string, payload: Record<string, unknown>, sessionId: string | null = null): void {
    const msg = { v: 1, id: randomUUID(), ts: new Date().toISOString(), sessionId, type, payload };
    this.ws.send(JSON.stringify(msg));
  }

  /** Attende il primo messaggio del tipo indicato (o che soddisfa match). */
  waitFor(type?: string, match?: (m: any) => boolean): Promise<any> {
    const predicate = (m: any) => (!type || m.type === type) && (!match || match(m));














    // Controllo prima i messaggi già ricevuti. Gli errori di autenticazione
    // vengono scartati SOLO quando non sono l'obiettivo dell'attesa stessa:
    // i test che li verificano esplicitamente (token errato, messaggio senza
    // hello) aspettano proprio un error unauthorized e non devono timeoutare.
    const existing = this.received.find((m: any) => {
      if (!predicate(m)) return false;
      if (m.type === "error" && m.payload?.code === "unauthorized") {
        return type === "error" && (match ? match(m) : true);
      }
      return true;
    });
    if (existing) return Promise.resolve(existing);
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        const idx = this.waiters.findIndex((w) => w.resolve === resolve);
        if (idx >= 0) this.waiters.splice(idx, 1);
        reject(new Error(`timeout in attesa di '${type ?? "match"}'`));
      }, 5000);
      this.waiters.push({
        match: predicate,
        resolve: (m) => {
          clearTimeout(timer);
          resolve(m);
        },
      });
    });
  }

  /** Attende N messaggi di un certo tipo. */
  async waitForCount(type: string, count: number): Promise<any[]> {
    const out: any[] = [];
    for (let i = 0; i < count; i++) out.push(await this.waitFor(type));
    return out;
  }


}
