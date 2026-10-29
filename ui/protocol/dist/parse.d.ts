/** Risultato di una validazione runtime: valore tipizzato oppure errore leggibile. */
export type ParseResult<T> = {
    ok: true;
    value: T;
} | {
    ok: false;
    error: string;
};
/**
 * Valida a runtime un messaggio ricevuto dal server.
 * Restituisce il messaggio tipizzato oppure un errore leggibile.
 */
export declare function parseServerMessage<T = import("./types.js").ServerMessage>(raw: unknown): ParseResult<T>;
/**
 * Valida a runtime un messaggio da inviare al server.
 * Restituisce il messaggio tipizzato oppure un errore leggibile.
 */
export declare function parseClientMessage<T = import("./types.js").ClientMessage>(raw: unknown): ParseResult<T>;
/** Costruisce un messaggio v1 valido (id/ts generati). Vedi {@link makeMessage}. */
export { makeMessage } from "./make-message.js";
export type { MakeMessageOptions } from "./make-message.js";
