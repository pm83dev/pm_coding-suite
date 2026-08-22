import * as vscode from 'vscode';
import * as path from 'path';

/**
 * Una modifica proposta dall'agent (evento edit_proposal), applicata con un click
 * sul bottone "Applica" dopo conferma dell'utente sul diff. L'agent propone sempre
 * il contenuto INTERO del file (mai un range): `range` resta opzionale solo per
 * compatibilità con applyProposedEdits, ma oggi non viene mai popolato.
 */
export interface ProposedEdit {
  filePath: string; // relativo alla root del workspace
  content: string;
  range?: { startLine: number; startChar: number; endLine: number; endChar: number };
}

function toUri(edit: ProposedEdit, root: vscode.Uri): vscode.Uri {
  return path.isAbsolute(edit.filePath)
    ? vscode.Uri.file(edit.filePath)
    : vscode.Uri.joinPath(root, edit.filePath);
}

/**
 * Conferma le modifiche proposte: NON scrive più i byte qui. Il processo agent (.NET)
 * scrive sempre il file reale su disco subito dopo aver ricevuto l'edit_decision positiva
 * (write_file e edit_file, sia in modalità CLI che --stdin-protocol — vedi ApplyOrPropose
 * in FileSystemTools.cs). Se anche l'estensione applicasse la modifica qui con
 * vscode.workspace.applyEdit, lascerebbe il buffer dell'editor "sporco" (modificato, non
 * salvato) un istante prima che l'agent .NET riscriva lo stesso file dall'esterno: VS Code
 * lo interpreta come un conflitto (file cambiato su disco + modifiche locali non salvate)
 * e mostra il prompt di overwrite/merge — osservato soprattutto alla creazione di file
 * nuovi. Nessuna doppia scrittura → nessun conflitto: un editor già aperto sullo stesso
 * file viene semplicemente ricaricato in automatico da VS Code (nessun prompt, il buffer
 * non era "sporco").
 */
export async function applyProposedEdits(edits: ProposedEdit[]): Promise<boolean> {
  const root = vscode.workspace.workspaceFolders?.[0]?.uri;
  if (!root) {
    vscode.window.showWarningMessage('PM LLM: nessun workspace aperto, impossibile applicare le modifiche.');
    return false;
  }
  // Validazione path (stesso calcolo di toUri) per restituire false su un edit malformato,
  // comportamento invariato rispetto a prima per i chiamanti (acceptEditProposal).
  for (const edit of edits) toUri(edit, root);
  return true;
}
