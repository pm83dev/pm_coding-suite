import { describe, expect, it, vi } from "vitest";

import { makeMessage, parseServerMessage } from "@pm-ui/protocol";

import { ProtocolService } from "./protocol.service";
import { initialProtocolState } from "./state";

describe("ProtocolService", () => {
  function setup() {
    // Il service ha providedIn: "root" e costruttore vuoto — istanziamo direttamente
    // per evitare dipendenze TestBed/Platform in ambiente Vitest.
    return new ProtocolService();
  }

  it("stato iniziale: connessione closed", () => {
    const s = setup();
    expect(s.connection()).toBe("closed");
    expect(s.sessions()).toEqual([]);
  });

  it("applica un messaggio server valido (sessions_list)", () => {
    const s = setup();
    const raw = JSON.stringify(makeMessage("sessions_list", { sessions: [
      { id: "s1", title: "A", workspace: "/w", updatedAt: "2026-01-01T00:00:00Z", status: "idle" },
    ] }));
    s._handleRawForTest(raw);
    expect(s.sessions()).toHaveLength(1);
    expect(s.sessions()[0]?.id).toBe("s1");
  });

  it("rifiuta un messaggio non valido e imposta l'errore", () => {
    const s = setup();
    s._handleRawForTest('{"v":2,"id":"x","ts":"2026-01-01T00:00:00Z","sessionId":null,"type":"sessions_list","payload":{}}');
    expect(s.error()).toContain("non valido");
  });

  it("parseServerMessage valida un hello_ok", () => {
    const raw = JSON.stringify(makeMessage("hello_ok", { serverVersion: "0.1.0", protocol: 1 }));
    const parsed = parseServerMessage(JSON.parse(raw));
    expect(parsed.ok).toBe(true);
    if (parsed.ok) {
      expect(parsed.value.type).toBe("hello_ok");
    }
  });

  it("parseServerMessage rifiuta v=2", () => {
    const raw = JSON.stringify({ v: 2, id: "x", ts: "2026-01-01T00:00:00Z", sessionId: null, type: "hello_ok", payload: { serverVersion: "1", protocol: 1 } });
    const parsed = parseServerMessage(JSON.parse(raw));
    expect(parsed.ok).toBe(false);
  });

  it("saveSettings aggiorna le impostazioni e non mette il token nell'URL", () => {
    const s = setup();
    s.saveSettings({ serverUrl: "ws://127.0.0.1:9999", token: "segreto" });
    expect(s.settings().serverUrl).toBe("ws://127.0.0.1:9999");
    expect(s.settings().token).toBe("segreto");
    // L'URL esposto non contiene il token.
    expect(s.serverUrl).not.toContain("segreto");
  });

  it("disconnect chiude la connessione senza riconnettersi", () => {
    const s = setup();
    s.disconnect();
    expect(s.connection()).toBe("closed");
  });

  it("stato iniziale esposto è immutabile (readonly)", () => {
    const s = setup();
    expect(s.protocolState()).toEqual(initialProtocolState);
  });
});
