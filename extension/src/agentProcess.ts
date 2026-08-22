import * as vscode from 'vscode';
import * as cp from 'child_process';
import * as fs from 'fs';
import * as readline from 'readline';

/**
 * Un turno della cronologia inviato all'agent nel campo "context" della request
 * — solo testo user/assistant, nessun tool call: l'agent .NET tiene la propria
 * history dei tool call internamente per il turno in corso, non serve trasmetterla.
 */
export interface AgentContextMessage {
  role: 'user' | 'assistant';
  content: string;
}

export type AgentEvent =
  | { type: 'token'; text: string }
  | { type: 'tool_call'; tool: string; args: unknown }
  | { type: 'tool_result'; tool: string; result: string }
  | { type: 'edit_proposal'; path: string; content: string }
  | { type: 'status'; text: string }
  | { type: 'done' };

/**
 * Gestisce il ciclo di vita del processo `pm-code.exe --stdin-protocol`: un solo
 * processo per sessione (lazy, creato al primo messaggio @pm), riusato finché resta
 * vivo. Comunica via NDJSON: una riga JSON per richiesta/evento su stdin/stdout.
 */
export class AgentProcess {
  private proc: cp.ChildProcessWithoutNullStreams | undefined;
  private rl: readline.Interface | undefined;

  constructor(
    private readonly agentPath: string,
    private readonly workspacePath: string,
  ) {}

  private ensureStarted(): cp.ChildProcessWithoutNullStreams {
    if (this.proc && !this.proc.killed) return this.proc;

    // Su Linux/macOS un binario appena copiato/pubblicato spesso non ha il bit eseguibile
    // (dotnet publish non lo imposta): senza questo lo spawn fallisce con EACCES invece
    // di avviare l'agent.
    if (process.platform !== 'win32') {
      try {
        fs.accessSync(this.agentPath, fs.constants.X_OK);
      } catch {
        try {
          fs.chmodSync(this.agentPath, 0o755);
        } catch (err) {
          console.error(`[PM Agent] Impossibile rendere eseguibile ${this.agentPath}:`, err);
        }
      }
    }

    // --workspace: senza questo il processo figlio erediterebbe la cwd
    // dell'Extension Host (es. la cartella di installazione di VS Code) invece
    // della cartella realmente aperta come workspace — l'agent finirebbe a
    // lavorare su una sandbox scollegata dal progetto dell'utente.
    const proc = cp.spawn(this.agentPath, ['--stdin-protocol', '--workspace', this.workspacePath], {
      cwd: this.workspacePath,
      stdio: ['pipe', 'pipe', 'pipe'],
    });
    proc.stderr.setEncoding('utf8');
    proc.stderr.on('data', (chunk: string) => {
      console.error(`[PM Agent] ${chunk}`);
    });
    proc.on('exit', (code) => {
      console.error(`[PM Agent] Processo terminato (code ${code})`);
      if (this.proc === proc) {
        this.proc = undefined;
        this.rl = undefined;
      }
    });
    proc.on('error', (err) => {
      console.error('[PM Agent] Errore di avvio:', err);
    });

    this.proc = proc;
    this.rl = readline.createInterface({ input: proc.stdout });
    return proc;
  }

  /**
   * Invia una richiesta e consuma gli eventi NDJSON finché non arriva "done"
   * (o il processo termina). `onEvent` viene invocato per ogni evento tranne "done".
   */
  async sendRequest(
    prompt: string,
    context: AgentContextMessage[],
    onEvent: (event: Exclude<AgentEvent, { type: 'done' }>) => void,
    token: vscode.CancellationToken,
    model?: string,
    endpoint?: string,
  ): Promise<void> {
    const proc = this.ensureStarted();
    const rl = this.rl!;

    return new Promise<void>((resolve, reject) => {
      let settled = false;

      const cleanup = () => {
        rl.off('line', onLine);
        proc.off('exit', onExit);
        cancelListener.dispose();
      };

      const onLine = (line: string) => {
        if (!line.trim()) return;
        let event: AgentEvent;
        try {
          event = JSON.parse(line) as AgentEvent;
        } catch (e) {
          console.error('[PM Agent] Riga NDJSON non valida, ignorata:', line, e);
          return;
        }
        if (event.type === 'done') {
          if (!settled) { settled = true; cleanup(); resolve(); }
          return;
        }
        onEvent(event);
      };

      const onExit = (code: number | null) => {
        if (!settled) {
          settled = true;
          cleanup();
          reject(new Error(`Il processo dell'agent è terminato inaspettatamente (code ${code}).`));
        }
      };

      const cancelListener = token.onCancellationRequested(() => {
        if (!settled) {
          settled = true;
          cleanup();
          // Hard stop: limitarsi a smettere di ASCOLTARE gli eventi non ferma il processo
          // .NET, che continuerebbe a chiamare l'LLM ed eseguire tool in background ignaro
          // della cancellazione (esattamente il bug "@pm non si ferma mai su Stop"). L'unico
          // modo per interrompere davvero un turno già in corso è terminare il processo —
          // si perde la history accumulata in questo turno, ma è il prezzo di uno stop reale.
          // Il prossimo messaggio @pm farà ripartire un processo pulito (ensureStarted lazy).
          this.dispose();
          resolve();
        }
      });

      rl.on('line', onLine);
      proc.once('exit', onExit);

      const requestLine = JSON.stringify({ type: 'request', prompt, context, model, endpoint });
      proc.stdin.write(requestLine + '\n');
    });
  }

  /** Invia la decisione dell'utente su una edit_proposal in attesa. */
  sendEditDecision(accepted: boolean): void {
    if (!this.proc || this.proc.killed) return;
    this.proc.stdin.write(JSON.stringify({ type: 'edit_decision', accepted }) + '\n');
  }

  dispose(): void {
    this.rl?.close();
    this.proc?.kill();
    this.proc = undefined;
    this.rl = undefined;
  }
}

let sharedAgentProcess: AgentProcess | undefined;
let sharedAgentKey: string | undefined;

/**
 * Istanza modulo-level lazy: un solo processo per l'intera sessione dell'estensione,
 * ma ne viene spawnato uno nuovo se cambia il path dell'agent o la cartella workspace
 * (es. l'utente apre un altro progetto) — la chiave combina entrambi.
 */
export function getAgentProcess(agentPath: string, workspacePath: string): AgentProcess {
  const key = `${agentPath}|${workspacePath}`;
  if (sharedAgentProcess && sharedAgentKey === key) return sharedAgentProcess;
  sharedAgentProcess?.dispose();
  sharedAgentKey = key;
  sharedAgentProcess = new AgentProcess(agentPath, workspacePath);
  return sharedAgentProcess;
}

export function disposeAgentProcess(): void {
  sharedAgentProcess?.dispose();
  sharedAgentProcess = undefined;
  sharedAgentKey = undefined;
}
