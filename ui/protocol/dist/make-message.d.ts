import type { Envelope } from "./types.js";
export interface MakeMessageOptions {
    /** Id del messaggio (default: uuid v4). */
    id?: string;
    /** Timestamp ISO8601 (default: adesso). */
    ts?: string;
}
/**
 * Costruisce un messaggio della busta v1 con `id`/`ts` generati automaticamente.
 * Il payload non viene validato qui: usa {@link parseClientMessage}/{@link parseServerMessage}
 * per la validazione runtime.
 */
export declare function makeMessage<P>(type: string, payload: P, sessionId?: string | null, opts?: MakeMessageOptions): Envelope<P>;
