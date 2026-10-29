/** uuid v4 portabile (browser/node): usa crypto.randomUUID se disponibile. */
function newId() {
    const c = globalThis.crypto;
    if (c && typeof c.randomUUID === "function")
        return c.randomUUID();
    // Fallback: uuid v4 da Math.random (solo se crypto.randomUUID non esiste).
    const bytes = new Uint8Array(16);
    for (let i = 0; i < 16; i++)
        bytes[i] = Math.floor(Math.random() * 256);
    bytes[6] = ((bytes[6] ?? 0) & 0x0f) | 0x40;
    bytes[8] = ((bytes[8] ?? 0) & 0x3f) | 0x80;
    const hex = Array.from(bytes, (b) => b.toString(16).padStart(2, "0")).join("");
    return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}
/**
 * Costruisce un messaggio della busta v1 con `id`/`ts` generati automaticamente.
 * Il payload non viene validato qui: usa {@link parseClientMessage}/{@link parseServerMessage}
 * per la validazione runtime.
 */
export function makeMessage(type, payload, sessionId = null, opts) {
    return {
        v: 1,
        id: opts?.id ?? newId(),
        ts: opts?.ts ?? new Date().toISOString(),
        sessionId,
        type,
        payload,
    };
}
//# sourceMappingURL=make-message.js.map