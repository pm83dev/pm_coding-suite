import { TestBed } from "@angular/core/testing";
import { BrowserTestingModule } from "@angular/platform-browser/testing";
import { vi, beforeAll, afterAll } from "vitest";

/* Inizializzazione TestBed (Angular 22) — disabilitata, si usa TestBed.configureTestingModule con BrowserTestingModule */
// TestBed.initTestEnvironment(BrowserTestingModule, undefined, { teardown: { destroyAfterEach: true } });

/**
 * Mock di WebSocket che evita errori `undici`/`jsdom` durante i test.
 */
class MockWebSocket {
  static readonly CONNECTING = 0;
  static readonly OPEN = 1;
  static readonly CLOSING = 2;
  static readonly CLOSED = 3;

  url = "";
  protocol = "";
  extensions = "";
  binaryType: BinaryType = "blob";
  readyState: 0 | 1 | 2 | 3 = MockWebSocket.CLOSED;
  bufferedAmount = 0;
  onopen: ((ev: Event) => void) | null = null;
  onmessage: ((ev: MessageEvent) => void) | null = null;
  onclose: ((ev: CloseEvent) => void) | null = null;
  onerror: ((ev: Event) => void) | null = null;

  constructor(url: string, _protocols?: string | string[]) {
    this.url = url;
  }

  send(_data: string | ArrayBuffer | Blob | ArrayBufferView): void {}

  close(_code?: number, _reason?: string): void {
    this.readyState = MockWebSocket.CLOSED;
    if (this.onclose) {
      this.onclose(new CloseEvent("close"));
    }
  }

  addEventListener<K>(_type: K, _listener: any, _options?: any): void {}
  removeEventListener<K>(_type: K, _listener: any, _options?: any): void {}
  dispatchEvent(_event?: Event): boolean { return false; }
  onopenSet(value: ((ev: Event) => void) | null) { this.onopen = value; }
  onmessageSet(value: ((ev: MessageEvent) => void) | null) { this.onmessage = value; }
  oncloseSet(value: ((ev: CloseEvent) => void) | null) { this.onclose = value; }
  onerrorSet(value: ((ev: Event) => void) | null) { this.onerror = value; }
  closeSet(_code?: number, _reason?: string) {}
}

vi.stubGlobal("WebSocket", MockWebSocket as unknown as typeof WebSocket);

/**
 * Mock di fetch per prevenire chiamate HTTP reali da jsdom/undici
 * che causano "InvalidArgumentError: invalid onError method".
 */
vi.stubGlobal("fetch", vi.fn().mockRejectedValue(new Error("fetch non disponibile nei test")));

/**
 * Mock di XMLHttpRequest per prevenire chiamate HTTP da jsdom.
 */
const origOpen = XMLHttpRequest.prototype.open;
const origSend = XMLHttpRequest.prototype.send;
XMLHttpRequest.prototype.open = function (this: XMLHttpRequest, method: string, url: string | URL | undefined, ...args: any[]) {
  // Segnala che XHR è disabilitato
  (this as any)._xhrDisabled = true;
  return origOpen.apply(this, [method, url, ...args] as any);
} as any;
XMLHttpRequest.prototype.send = function (this: XMLHttpRequest, data?: any) {
  if ((this as any)._xhrDisabled) {
    throw new Error("XMLHttpRequest disabilitato nei test");
  }
  return origSend.apply(this, [data] as any);
} as any;

/**
 * Interception dei rejection `UND_ERR_INVALID_ARG` (undici sotto jsdom).
 *
 * Nota: `off` è una API Node >= 10; in alcuni ambienti di test
 * (jsdom/happy-dom) EventEmitter non la espone, quindi si usa `removeListener`
 * (disponibile in tutti i runtime supportati).
 */
const _emitter = globalThis as unknown as {
  on: (e: string, h: any) => void;
  removeListener: (e: string, h: any) => void;
};

const handler: (e: PromiseRejectionEvent) => void = (e: PromiseRejectionEvent): void => {
  if (
    e.reason &&
    typeof e.reason === "object" &&
    "code" in e.reason &&
    (e.reason as { code?: string }).code === "UND_ERR_INVALID_ARG"
  ) {
    e.preventDefault();
  }
};

beforeAll(() => _emitter.on("unhandledRejection", handler));
afterAll(() => _emitter.removeListener("unhandledRejection", handler));
