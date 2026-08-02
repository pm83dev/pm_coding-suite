import * as vscode from 'vscode';

export const PROPOSED_EDIT_SCHEME = 'pm-agent-proposed';

/**
 * TextDocumentContentProvider per lo scheme pm-agent-proposed://<path relativo>.
 * Contiene l'ULTIMO contenuto proposto per ciascun path (evento edit_proposal
 * dell'agent), usato per aprire un vscode.diff tra il file reale e la modifica
 * proposta, prima che l'utente la confermi.
 */
export class EditProposalProvider implements vscode.TextDocumentContentProvider {
  private readonly contents = new Map<string, string>();
  private readonly onDidChangeEmitter = new vscode.EventEmitter<vscode.Uri>();
  readonly onDidChange = this.onDidChangeEmitter.event;

  setProposal(relativePath: string, content: string): vscode.Uri {
    const normalized = relativePath.replace(/\\/g, '/');
    this.contents.set(normalized, content);
    const uri = vscode.Uri.parse(`${PROPOSED_EDIT_SCHEME}:/${normalized}`);
    this.onDidChangeEmitter.fire(uri);
    return uri;
  }

  provideTextDocumentContent(uri: vscode.Uri): string {
    const key = uri.path.replace(/^\//, '');
    return this.contents.get(key) ?? '';
  }
}
