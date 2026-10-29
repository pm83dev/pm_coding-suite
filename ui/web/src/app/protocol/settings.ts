/** Impostazioni di connessione, salvate in sessionStorage (mai nell'URL). */
export interface ConnectionSettings {
  /** URL del server WebSocket, es. ws://127.0.0.1:8765 */
  serverUrl: string;
  /** Token di autenticazione inviato nel messaggio hello. */
  token: string;
  /** Workspace di default per le nuove sessioni. */
  workspace?: string;
}

const SETTINGS_KEY = "pm-ui.settings";

export const DEFAULT_SETTINGS: ConnectionSettings = {
  serverUrl: "ws://127.0.0.1:8765",
  token: "",
};

/** Legge le impostazioni da sessionStorage; se assenti restituisce i default. */
export function loadSettings(): ConnectionSettings {
  try {
    const raw = sessionStorage.getItem(SETTINGS_KEY);
    if (!raw) return { ...DEFAULT_SETTINGS };
    const parsed = JSON.parse(raw) as Partial<ConnectionSettings>;
    return {
      serverUrl: typeof parsed.serverUrl === "string" && parsed.serverUrl.length > 0 ? parsed.serverUrl : DEFAULT_SETTINGS.serverUrl,
      token: typeof parsed.token === "string" ? parsed.token : "",
      workspace: typeof parsed.workspace === "string" && parsed.workspace.length > 0 ? parsed.workspace : undefined,
    };
  } catch {
    return { ...DEFAULT_SETTINGS };
  }
}

/** Salva le impostazioni in sessionStorage. */
export function saveSettings(settings: ConnectionSettings): void {
  try {
    sessionStorage.setItem(SETTINGS_KEY, JSON.stringify(settings));
  } catch {
    // sessionStorage non disponibile (es. test): ignora.
  }
}
