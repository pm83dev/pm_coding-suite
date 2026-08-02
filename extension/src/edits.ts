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
 * Applica le modifiche proposte con un unico vscode.WorkspaceEdit
 * (quindi un solo undo, e supporto multi-file nativo).
 */
export async function applyProposedEdits(edits: ProposedEdit[]): Promise<boolean> {
  const root = vscode.workspace.workspaceFolders?.[0]?.uri;
  if (!root) {
    vscode.window.showWarningMessage('PM LLM: nessun workspace aperto, impossibile applicare le modifiche.');
    return false;
  }

  const wsEdit = new vscode.WorkspaceEdit();

  for (const edit of edits) {
    const uri = toUri(edit, root);

    if (edit.range) {
      const r = edit.range;
      wsEdit.replace(
        uri,
        new vscode.Range(r.startLine, r.startChar, r.endLine, r.endChar),
        edit.content,
      );
      continue;
    }

    // Sostituzione intero file: se esiste rimpiazza tutto, altrimenti crealo
    try {
      const doc = await vscode.workspace.openTextDocument(uri);
      const fullRange = new vscode.Range(0, 0, doc.lineCount, 0);
      wsEdit.replace(uri, fullRange, edit.content);
    } catch {
      wsEdit.createFile(uri, { ignoreIfExists: true });
      wsEdit.insert(uri, new vscode.Position(0, 0), edit.content);
    }
  }

  return vscode.workspace.applyEdit(wsEdit);
}
