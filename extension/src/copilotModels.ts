import * as os from 'os';
import * as path from 'path';
import * as vscode from 'vscode';

interface CopilotModelEntry {
  id?: string;
  url?: string;
}

interface CopilotProviderEntry {
  models?: CopilotModelEntry[];
}

async function readOneChatLanguageModelsFile(filePath: string): Promise<Record<string, string>> {
  const endpoints: Record<string, string> = {};
  try {
    const bytes = await vscode.workspace.fs.readFile(vscode.Uri.file(filePath));
    const entries = JSON.parse(Buffer.from(bytes).toString('utf8')) as CopilotProviderEntry[];
    for (const entry of entries) {
      for (const m of entry.models ?? []) {
        if (m.id && m.url) endpoints[m.id] = m.url;
      }
    }
  } catch {
    // File assente o non valido in questa cartella: normale, si continua con le altre.
  }
  return endpoints;
}

/**
 * chatLanguageModels.json (config dei modelli custom/BYOK di Copilot Chat, la stessa
 * che alimenta il picker nativo) è salvato per-profilo VS Code, sotto
 * User/profiles/<profileId>/chatLanguageModels.json — o sotto User/chatLanguageModels.json
 * per il profilo di default.
 *
 * context.globalStorageUri NON è però scoped per il profilo attivo (nonostante la
 * struttura a cartelle profiles/<id>/globalStorage/<ext-id> potrebbe far pensare il
 * contrario): punta sempre a User/globalStorage/<ext-id>, qualunque sia il profilo
 * della finestra corrente. Non esiste un'API pubblica per leggere l'id del profilo
 * attivo da un'estensione. Come unico ripiego praticabile, scansiona il file di
 * default PIÙ tutti i profili in User/profiles/*, e unisce le mappe trovate — se lo
 * stesso id-modello compare in più profili con URL diversi, l'ultimo letto vince
 * (ambiguità intrinseca, irrisolvibile senza sapere il profilo attivo: in quel caso usa
 * pmChat.modelEndpoints per forzare il valore corretto).
 */
/**
 * Cartelle "User" di VS Code in cui cercare chatLanguageModels.json.
 *
 * La prima è ricavata da globalStorageUri, ma non sempre coincide con quella del client:
 * quando l'extension host gira come processo "server" (osservato su VS Code recenti anche
 * in locale) globalStorageUri punta a ~/.vscode-server/data/User/globalStorage/<ext-id>,
 * mentre Copilot salva chatLanguageModels.json nella cartella User del client
 * (%APPDATA%/Code/User su Windows). Per questo si aggiungono anche le cartelle User
 * standard per piattaforma (e quella dell'installazione portable, se presente).
 */
function candidateUserDirs(globalStorageUri: vscode.Uri): string[] {
  const dirs = [path.dirname(path.dirname(globalStorageUri.fsPath))];
  const product = vscode.env.appName.includes('Insiders') ? 'Code - Insiders' : 'Code';

  if (process.env.VSCODE_PORTABLE) dirs.push(path.join(process.env.VSCODE_PORTABLE, 'user-data', 'User'));
  if (process.platform === 'win32' && process.env.APPDATA) {
    dirs.push(path.join(process.env.APPDATA, product, 'User'));
  } else if (process.platform === 'darwin') {
    dirs.push(path.join(os.homedir(), 'Library', 'Application Support', product, 'User'));
  } else {
    dirs.push(path.join(process.env.XDG_CONFIG_HOME || path.join(os.homedir(), '.config'), product, 'User'));
  }

  // Dedup case-insensitive (su Windows c:\ e C:\ indicano la stessa cartella).
  const seen = new Set<string>();
  return dirs.filter(d => {
    const key = path.normalize(d).toLowerCase();
    if (seen.has(key)) return false;
    seen.add(key);
    return true;
  });
}

export async function loadCopilotModelEndpoints(
  globalStorageUri: vscode.Uri,
): Promise<Record<string, string>> {
  const userDirs = candidateUserDirs(globalStorageUri);
  const endpoints: Record<string, string> = {};
  const filesRead: string[] = [];

  for (const userDir of userDirs) {
    const defaultFile = path.join(userDir, 'chatLanguageModels.json');
    const defaultEndpoints = await readOneChatLanguageModelsFile(defaultFile);
    if (Object.keys(defaultEndpoints).length > 0) filesRead.push(defaultFile);
    Object.assign(endpoints, defaultEndpoints);

    try {
      const profilesDir = path.join(userDir, 'profiles');
      const children = await vscode.workspace.fs.readDirectory(vscode.Uri.file(profilesDir));
      for (const [name, type] of children) {
        if (type !== vscode.FileType.Directory) continue;
        const profileFile = path.join(profilesDir, name, 'chatLanguageModels.json');
        const profileEndpoints = await readOneChatLanguageModelsFile(profileFile);
        if (Object.keys(profileEndpoints).length > 0) filesRead.push(profileFile);
        Object.assign(endpoints, profileEndpoints);
      }
    } catch {
      // Cartella profiles/ assente: nessun profilo custom, solo quello di default.
    }
  }

  console.error(
    `[PM Chat] chatLanguageModels.json: ${Object.keys(endpoints).length} modelli mappati da ${filesRead.length} file (${filesRead.join(', ') || 'nessuno trovato'}) — cartelle User cercate: ${userDirs.join(', ')}.`,
  );

  return endpoints;
}
