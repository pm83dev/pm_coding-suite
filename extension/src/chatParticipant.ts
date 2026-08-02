import * as vscode from 'vscode';
import { AgentContextMessage, AgentEvent, AgentProcess, getAgentProcess } from './agentProcess';
import { EditProposalProvider } from './editProposalProvider';
import { applyProposedEdits } from './edits';

/** Rilegge la config e restituisce/spawna il processo agent condiviso per questa sessione. */
function getCurrentAgentProcess(): AgentProcess | undefined {
  const agentPath = vscode.workspace.getConfiguration('pmChat').get<string>('agentPath', '').trim();
  const workspacePath = vscode.workspace.workspaceFolders?.[0]?.uri.fsPath;
  return agentPath && workspacePath ? getAgentProcess(agentPath, workspacePath) : undefined;
}

/**
 * Sanitizza il testo della history rimuovendo pattern di tool-call testuali
 * malformati che potrebbero essere rimasti da versioni precedenti dell'estensione
 * (XML <tool:...>, placeholder #tool:...) — difensivo per sessioni vecchie.
 */
function sanitizeHistoryText(text: string): string {
  return text
    .replace(/#tool:[\w\s]+(—[^\n]*)?/g, '')
    .replace(/<tool:[\w.]+>.*?<\/tool:[\w.]+>/gs, '')
    .trim();
}

/** Ricostruisce il "context" (solo testo user/assistant) da inviare all'agent. */
function buildAgentContext(history: readonly vscode.ChatContext['history'][number][]): AgentContextMessage[] {
  const messages: AgentContextMessage[] = [];
  for (const turn of history) {
    if (turn instanceof vscode.ChatRequestTurn) {
      messages.push({ role: 'user', content: turn.prompt });
    } else if (turn instanceof vscode.ChatResponseTurn) {
      const text = turn.response
        .filter(
          (part): part is vscode.ChatResponseMarkdownPart =>
            part instanceof vscode.ChatResponseMarkdownPart,
        )
        .map((part) => part.value.value)
        .join('');
      const clean = sanitizeHistoryText(text);
      if (clean) messages.push({ role: 'assistant', content: clean });
    }
  }
  return messages;
}

/**
 * Handler principale del Chat Participant: puro frontend verso l'agent .NET
 * (pm-code.exe --stdin-protocol). Tutta l'intelligenza (system prompt, tool
 * calling, ReAct loop) vive nell'agent — l'estensione inoltra prompt+history,
 * mostra lo streaming dei token, e media la conferma delle modifiche ai file.
 */
export async function handleChatRequest(
  request: vscode.ChatRequest,
  context: vscode.ChatContext,
  stream: vscode.ChatResponseStream,
  token: vscode.CancellationToken,
): Promise<vscode.ChatResult | undefined> {
  const config = vscode.workspace.getConfiguration('pmChat');
  const agentPath = config.get<string>('agentPath', '').trim();

  if (!agentPath) {
    stream.markdown(
      '⚠️ Nessun percorso configurato per PM Code Agent. Imposta `pmChat.agentPath` ' +
        'nelle impostazioni di VS Code (percorso di `pm-code.exe`).',
    );
    stream.button({
      command: 'workbench.action.openSettings',
      arguments: ['pmChat.agentPath'],
      title: 'Apri impostazioni',
    });
    return undefined;
  }

  const workspacePath = vscode.workspace.workspaceFolders?.[0]?.uri.fsPath;
  if (!workspacePath) {
    stream.markdown('⚠️ Apri una cartella come workspace prima di usare `@pm`.');
    return undefined;
  }

  const agent = getAgentProcess(agentPath, workspacePath);
  const agentContext = buildAgentContext(context.history);

  stream.progress('Elaborazione richiesta…');

  try {
    await agent.sendRequest(request.prompt, agentContext, (event: Exclude<AgentEvent, { type: 'done' }>) => {
      switch (event.type) {
        case 'token':
          stream.markdown(event.text);
          break;
        case 'tool_call':
          stream.progress(`Tool: ${event.tool}…`);
          break;
        case 'tool_result':
          // manage_todo restituisce già il piano formattato in markdown (checklist
          // ✅/🔄/⬜): è l'unico tool_result che ha senso mostrare all'utente in chat,
          // gli altri sono dettagli interni del ragionamento dell'agent.
          if (event.tool === 'manage_todo') {
            stream.markdown(`\n${event.result}\n`);
          }
          break;
        case 'edit_proposal':
          void handleEditProposal(event.path, event.content, stream);
          break;
      }
    }, token);
  } catch (err) {
    const msg = err instanceof Error ? err.message : String(err);
    stream.markdown(`\n\n⚠️ **Errore comunicando con PM Code Agent:** ${msg}`);
  }

  return undefined;
}

let proposalProvider: EditProposalProvider | undefined;

export function setEditProposalProvider(provider: EditProposalProvider): void {
  proposalProvider = provider;
}

/**
 * Mostra un diff nativo VS Code tra il file reale e la modifica proposta
 * dall'agent, con bottoni Applica/Rifiuta che inviano l'edit_decision.
 * Gli argomenti dei bottoni restano serializzabili (stringhe): l'istanza di
 * AgentProcess non viene mai passata attraverso stream.button, viene ri-risolta
 * dal comando tramite getCurrentAgentProcess() (config → singleton condiviso).
 */
async function handleEditProposal(
  relativePath: string,
  content: string,
  stream: vscode.ChatResponseStream,
): Promise<void> {
  if (!proposalProvider) {
    // Nessun provider registrato (non dovrebbe accadere: registrato in activate()):
    // rifiuta subito per non lasciare l'agent bloccato in attesa.
    getCurrentAgentProcess()?.sendEditDecision(false);
    return;
  }

  const root = vscode.workspace.workspaceFolders?.[0]?.uri;
  if (!root) {
    stream.markdown(`⚠️ Nessun workspace aperto: impossibile applicare la modifica a \`${relativePath}\`.`);
    getCurrentAgentProcess()?.sendEditDecision(false);
    return;
  }

  const proposedUri = proposalProvider.setProposal(relativePath, content);
  const realUri = vscode.Uri.joinPath(root, relativePath);

  // vscode.diff apre anche il lato "reale": se il file non esiste ancora (l'agent sta
  // proponendo di CREARLO, non di modificarlo) quell'apertura fallisce con un errore.
  // In quel caso mostriamo solo un'anteprima del contenuto proposto, niente diff.
  let fileExists = true;
  try {
    await vscode.workspace.fs.stat(realUri);
  } catch {
    fileExists = false;
  }

  stream.markdown(
    fileExists
      ? `\n📝 **Modifica proposta:** \`${relativePath}\`\n`
      : `\n📝 **Nuovo file proposto:** \`${relativePath}\`\n`,
  );

  if (fileExists) {
    stream.button({
      command: 'vscode.diff',
      arguments: [realUri, proposedUri, `${relativePath} (modifica proposta)`],
      title: 'Visualizza diff',
    });
  } else {
    stream.button({
      command: 'vscode.open',
      arguments: [proposedUri],
      title: 'Visualizza anteprima',
    });
  }
  stream.button({
    command: 'pmChat.acceptEditProposal',
    arguments: [relativePath, content],
    title: '✅ Applica',
  });
  stream.button({
    command: 'pmChat.rejectEditProposal',
    arguments: [],
    title: '❌ Rifiuta',
  });
}

/**
 * Registrati in extension.ts: eseguiti dal click sui bottoni sopra.
 * CRITICO: qualunque cosa succeda, dobbiamo SEMPRE inviare un edit_decision.
 * Il processo .NET è bloccato in lettura sincrona su stdin in attesa di questa
 * risposta — se applyProposedEdits lancia un'eccezione (conflitto WorkspaceEdit,
 * file con modifiche non salvate, permessi, ecc.) e non catturiamo l'errore qui,
 * l'agent resta bloccato per sempre (osservato: "ha chiamato un tool ed è rimasto
 * fermo, LLM fermo sul server" — è esattamente questo, non un problema del server).
 */
export async function acceptEditProposal(relativePath: string, content: string): Promise<void> {
  let applied = false;
  try {
    applied = await applyProposedEdits([{ filePath: relativePath, content }]);
  } catch (err) {
    const msg = err instanceof Error ? err.message : String(err);
    vscode.window.showWarningMessage(`PM Chat: impossibile applicare la modifica a ${relativePath}: ${msg}`);
  } finally {
    getCurrentAgentProcess()?.sendEditDecision(applied);
  }
}

export function rejectEditProposal(): void {
  getCurrentAgentProcess()?.sendEditDecision(false);
}
