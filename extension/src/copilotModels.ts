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
export async function loadCopilotModelEndpoints(
  globalStorageUri: vscode.Uri,
): Promise<Record<string, string>> {
  const userDir = path.dirname(path.dirname(globalStorageUri.fsPath));
  const endpoints: Record<string, string> = {};
  const filesRead: string[] = [];

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

  console.error(
    `[PM Chat] chatLanguageModels.json: ${Object.keys(endpoints).length} modelli mappati da ${filesRead.length} file (${filesRead.join(', ') || 'nessuno trovato'}).`,
  );

  return endpoints;
}
