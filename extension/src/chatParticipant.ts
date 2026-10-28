import * as vscode from 'vscode';
import { ActivityLevel, describeCall, describeOutcome, resultPreview } from './activityLog';
import { AgentContextMessage, AgentEvent, AgentProcess, getAgentProcess } from './agentProcess';
import { loadCopilotModelEndpoints } from './copilotModels';
import { EditProposalProvider } from './editProposalProvider';
import { applyProposedEdits } from './edits';

let globalStorageUri: vscode.Uri | undefined;

/** Registrato in extension.ts: serve a risalire al profilo VS Code attivo (vedi copilotModels.ts). */
export function setGlobalStorageUri(uri: vscode.Uri): void {
  globalStorageUri = uri;
}

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

/** Metadata del ChatResult: il solo testo scritto dal modello, senza la cronaca delle azioni. */
interface PmChatMetadata {
  agentText?: string;
}

/**
 * Toglie dal markdown di una risposta la cronaca scritta dall'estensione (righe "- 📖 Legge…",
 * piano ✅/🔄/⬜, anteprime ```text). Serve solo per le risposte salvate prima dei metadata:
 * rimandata all'agent come testo "assistant", il modello la imitava scrivendo azioni finte
 * invece di chiamare i tool.
 */
function stripActivityLog(text: string): string {
  return text
    .replace(/```text\n[\s\S]*?```/g, '')
    .split('\n')
    .filter((line) => !/^- \p{Extended_Pictographic}/u.test(line))
    .filter((line) => !/^\s*(\*\*Piano:\*\*|✅|🔄|⬜)/u.test(line))
    .join('\n')
    .replace(/\n{3,}/g, '\n\n')
    .trim();
}

/** Ricostruisce il "context" (solo testo user/assistant) da inviare all'agent. */
function buildAgentContext(history: readonly vscode.ChatContext['history'][number][]): AgentContextMessage[] {
  const messages: AgentContextMessage[] = [];
  for (const turn of history) {
    if (turn instanceof vscode.ChatRequestTurn) {
      messages.push({ role: 'user', content: turn.prompt });
    } else if (turn instanceof vscode.ChatResponseTurn) {
      const saved = (turn.result.metadata as PmChatMetadata | undefined)?.agentText;
      const text = typeof saved === 'string'
        ? saved
        : stripActivityLog(turn.response
            .filter(
              (part): part is vscode.ChatResponseMarkdownPart =>
                part instanceof vscode.ChatResponseMarkdownPart,
            )
            .map((part) => part.value.value)
            .join(''));
      // Mai saltare il turno: due "user" consecutivi vengono rifiutati dai template tipo Qwen3.
      messages.push({ role: 'assistant', content: sanitizeHistoryText(text) || '(azioni eseguite con i tool, nessun testo)' });
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
        'nelle impostazioni di VS Code (percorso dell\'eseguibile `pm-code`).',
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

  // request.model.id è il modello scelto dall'utente nel picker nativo di VS Code
  // (in alto nella vista Chat) — inoltrato per-turno all'agent .NET, che lo usa al
  // posto del default di appsettings.json se coincide con un nome noto al server LLM.
  //
  // Modelli diversi possono essere serviti da server llama-server DIVERSI (setup
  // multi-macchina/multi-GPU): il solo campo "model" non basta, serve anche l'URL
  // giusto. Letto automaticamente da chatLanguageModels.json (la stessa config che
  // Copilot Chat usa per il picker nativo — vedi copilotModels.ts), con eventuale
  // override manuale da pmChat.modelEndpoints. Se il modello scelto non è in nessuna
  // delle due mappe, l'agent ricade sull'endpoint di default di appsettings.json.
  const autoEndpoints = globalStorageUri ? await loadCopilotModelEndpoints(globalStorageUri) : {};
  const manualEndpoints = config.get<Record<string, string>>('modelEndpoints', {});
  const endpoint = manualEndpoints[request.model.id] ?? autoEndpoints[request.model.id];

  // Auto-approvazione: le modifiche ai file vengono applicate senza richiesta di
  // conferma se l'utente ha scelto "Bypass Approvals"/"Autopilot" nel selettore dei
  // permessi della chat (request.permissionLevel, API proposta chatParticipantPrivate:
  // letta in modo difensivo, può essere undefined se VS Code non la espone a questa
  // estensione) oppure se ha attivato pmChat.autoApprove nelle impostazioni (fallback).
  // Default: false (conferma richiesta).
  const permissionLevel = (request as { permissionLevel?: string }).permissionLevel;
  const autoApprove =
    permissionLevel === 'autoApprove' ||
    permissionLevel === 'autopilot' ||
    config.get<boolean>('autoApprove', false);
  console.log(`[PM Agent] permissionLevel=${permissionLevel ?? 'undefined'} autoApprove=${autoApprove}`);

  // Cronaca delle azioni (vedi activityLog.ts). La riga di una tool call viene scritta
  // quando arriva il suo risultato, così chiamata ed esito stanno sulla stessa riga.
  const activityLevel = config.get<ActivityLevel>('activityLog', 'compact');
  let pendingCall: { tool: string; args: unknown } | undefined;
  let lastWasActivity = false;
  // Testo del modello senza cronaca: finisce nei metadata e torna come context ai turni successivi.
  let agentText = '';
  const writeActivity = (line: string) => {
    // Riga vuota prima del primo elemento: senza, il markdown attaccherebbe l'elenco
    // all'ultima frase del modello.
    stream.markdown(`${lastWasActivity ? '' : '\n\n'}- ${line}\n`);
    lastWasActivity = true;
  };

  try {
    await agent.sendRequest(request.prompt, agentContext, (event: Exclude<AgentEvent, { type: 'done' }>) => {
      switch (event.type) {
        case 'token':
          if (lastWasActivity) {
            stream.markdown('\n');
            lastWasActivity = false;
          }
          stream.markdown(event.text);
          agentText += event.text;
          break;
        case 'tool_call':
          stream.progress(activityLevel === 'off' ? `Tool: ${event.tool}…` : `${describeCall(event.tool, event.args)}…`);
          pendingCall = { tool: event.tool, args: event.args };
          break;
        case 'tool_result': {
          const call = pendingCall?.tool === event.tool ? pendingCall : undefined;
          pendingCall = undefined;
          // manage_todo restituisce già il piano formattato in markdown (checklist
          // ✅/🔄/⬜): lo si mostra per intero in ogni modalità.
          if (event.tool === 'manage_todo') {
            if (lastWasActivity) stream.markdown('\n');
            stream.markdown(`\n${event.result}\n\n`);
            lastWasActivity = false;
            break;
          }
          if (activityLevel === 'off') {
            // Un fallimento (validazione rifiutata, stringa ambigua, loop rilevato, ecc.)
            // va sempre mostrato, anche senza cronaca: altrimenti resta solo nei log.
            if (/^(ERRORE|⚠️|\[LOOP-BLOCK\])/.test(event.result)) {
              stream.markdown(`\n${event.result}\n`);
            }
            break;
          }
          const outcome = describeOutcome(event.tool, event.result);
          writeActivity(`${describeCall(event.tool, call?.args)}${outcome ? ' → ' + outcome : ''}`);
          if (activityLevel === 'detailed' && event.result.trim()) {
            stream.markdown(`\n\`\`\`text\n${resultPreview(event.result).replace(/```/g, "'''")}\n\`\`\`\n`);
            lastWasActivity = false;
          }
          break;
        }
        case 'notice':
          // Interventi automatici del motore (auto-continue del piano, richiesta di test
          // funzionale, verifica non eseguita): sono correzioni di rotta, utili da vedere.
          if (activityLevel !== 'off') writeActivity(`⚙️ Motore: ${event.text}`);
          break;
        case 'edit_proposal':
          void handleEditProposal(event.path, event.content, stream);
          break;
        case 'status':
          // Ping periodico dall'agent (subito dopo un Applica/Rifiuta, e ogni ~3s mentre
          // genera una tool call lunga): senza questo la chat resta silenziosa per l'intera
          // generazione — nessun token visibile finché la tool call non è completa — ed è
          // indistinguibile da un blocco per chi guarda. Vedi LlamaClient.cs (onHeartbeat).
          stream.progress(event.text);
          break;
      }
    }, token, request.model.id, endpoint, autoApprove);
  } catch (err) {
    const msg = err instanceof Error ? err.message : String(err);
    stream.markdown(`\n\n⚠️ **Errore comunicando con PM Code Agent:** ${msg}`);
  }

  const metadata: PmChatMetadata = { agentText };
  return { metadata };
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
