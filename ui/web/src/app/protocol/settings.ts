/** Impostazioni di connessione, salvate in localStorage (persistono dopo la chiusura del browser). */
export interface ConnectionSettings {
  /** URL del server backend (HTTP/HTTPS), es. http://127.0.0.1:8765 */
  serverUrl: string;
  /** Token di autenticazione inviato come query param o header Bearer. */
  token: string;
  /** Workspace di default per le nuove sessioni. */
  workspace?: string;
}

const SETTINGS_KEY = "pm-coding-suite.settings";

export const DEFAULT_SETTINGS: ConnectionSettings = {
  serverUrl: "http://127.0.0.1:8765",
  token: "",
};

/** Legge le impostazioni da localStorage; se assenti restituisce i default. */
export function loadSettings(): ConnectionSettings {
  try {
    const raw = localStorage.getItem(SETTINGS_KEY);
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

/** Salva le impostazioni in localStorage. */
export function saveSettings(settings: ConnectionSettings): void {
  try {
    localStorage.setItem(SETTINGS_KEY, JSON.stringify(settings));
  } catch {
    // localStorage non disponibile (es. test): ignora.
  }
}