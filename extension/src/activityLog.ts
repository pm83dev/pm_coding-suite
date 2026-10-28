/**
 * Cronaca leggibile delle azioni dell'agent nella chat: una riga per ogni tool call
 * (icona + cosa fa + esito), generata dal codice dell'estensione a partire dagli eventi
 * tool_call/tool_result — nessun token del modello consumato. Prima le tool call erano
 * visibili solo come progress transitorio ("Tool: read_file…") e un turno lungo era
 * indistinguibile da un blocco; per capire cosa stava facendo l'agent bisognava leggere
 * pm-code-trace.log.
 */

export type ActivityLevel = 'off' | 'compact' | 'detailed';

type Args = Record<string, unknown>;

function str(v: unknown): string {
  return typeof v === 'string' ? v : v === undefined || v === null ? '' : String(v);
}

// Inline code senza backtick interni (romperebbero il markdown) e su una sola riga.
function code(s: string, max = 90): string {
  const flat = s.replace(/\s+/g, ' ').replace(/`/g, "'").trim();
  return '`' + (flat.length > max ? flat.slice(0, max) + '…' : flat) + '`';
}

// Path relativo leggibile: solo le ultime due parti, il resto è rumore in chat.
function shortPath(p: string): string {
  const parts = p.replace(/\\/g, '/').split('/').filter(Boolean);
  return parts.slice(-2).join('/') || p;
}

function firstLine(s: string, max = 140): string {
  const line = s.split('\n').map((l) => l.trim()).find((l) => l.length > 0) ?? '';
  return line.length > max ? line.slice(0, max) + '…' : line;
}

function countLines(s: string): number {
  return s.split('\n').filter((l) => l.trim().length > 0).length;
}

/** Descrive la chiamata (senza esito). */
export function describeCall(tool: string, rawArgs: unknown): string {
  const a = (typeof rawArgs === 'object' && rawArgs !== null ? rawArgs : {}) as Args;
  const path = shortPath(str(a.path));
  switch (tool) {
    case 'read_file':
    case 'read_file_range': {
      const range = a.start_line !== undefined ? ` (righe ${str(a.start_line)}-${str(a.end_line)})` : '';
      return `📖 Legge ${code(path)}${range}`;
    }
    case 'search_in_files':
      return `🔍 Cerca ${code(str(a.pattern), 60)}${a.glob ? ` in ${code(str(a.glob), 50)}` : ''}`;
    case 'sym_search':
      return `🔍 Cerca il simbolo ${code(str(a.symbol ?? a.name ?? a.query))}`;
    case 'glob_files':
      return `🗂️ Cerca file ${code(str(a.pattern))}`;
    case 'list_directory':
    case 'get_directory_details':
      return `🗂️ Esplora ${code(path || '.')}`;
    case 'get_workspace_tree':
      return '🗂️ Guarda la struttura del progetto';
    case 'edit_file':
      return `✏️ Modifica ${code(path)}`;
    case 'write_file':
      return `📝 Scrive ${code(path)}`;
    case 'delete_file':
      return `🗑️ Elimina ${code(path)}`;
    case 'move_file':
      return `📦 Sposta ${code(shortPath(str(a.source)))} → ${code(shortPath(str(a.destination)))}`;
    case 'create_directory':
      return `📁 Crea la cartella ${code(path)}`;
    case 'sol_analyze':
      return `🔨 Build${a.project ? ` di ${code(shortPath(str(a.project)))}` : ''}`;
    case 'run_dotnet':
      return `🔨 dotnet ${code(str(a.args))}`;
    case 'run_command':
      return a.background === true
        ? `🚀 Avvia in background ${code(str(a.command))}`
        : `⚙️ Esegue ${code(str(a.command))}`;
    case 'get_background_output':
      return `⏳ Controlla il processo ${str(a.pid)}`;
    case 'stop_background_job':
      return `⏹️ Ferma il processo ${str(a.pid)}`;
    case 'list_background_jobs':
      return '⏳ Elenca i processi in background';
    case 'git_status':
      return '🌿 git status';
    case 'git_diff':
      return `🌿 git diff${a.path ? ` ${code(path)}` : ''}`;
    case 'git_log':
      return '🌿 git log';
    case 'git_add':
    case 'git_commit':
    case 'git_checkout':
      return `🌿 ${tool.replace('_', ' ')} ${code(str(a.path ?? a.message ?? a.branch))}`;
    case 'project_notes':
      return a.action === 'add'
        ? `🧠 Memorizza: ${code(str(a.note), 120)}`
        : `🧠 Note del progetto (${str(a.action)})`;
    case 'web_search':
    case 'web_fetch':
      return `🌐 ${tool === 'web_search' ? 'Cerca sul web' : 'Apre'} ${code(str(a.query ?? a.url))}`;
    default:
      return `🔧 ${tool}`;
  }
}

// Interventi del motore riconoscibili dal prefisso del risultato: mostrati sempre,
// sono esattamente i momenti in cui l'agent viene corretto o fermato.
const GUARD_PATTERNS: [RegExp, string][] = [
  [/^⚠️ LETTURA BLOCCATA/, '🛑 Lettura rifiutata dal motore: troppe letture senza agire'],
  [/^⚠️ VERIFICA FUNZIONALE MANCANTE/, '🛑 Il motore chiede un test reale prima di chiudere'],
  [/^⚠️ VERIFICA MANCANTE/, '🛑 Il motore chiede la build prima di proseguire'],
  [/^\[LOOP-BLOCK\]/, '🛑 Bloccato: stessa chiamata ripetuta con lo stesso risultato'],
];

/** Esito sintetico della chiamata, o stringa vuota se non c'è niente di utile da dire. */
export function describeOutcome(tool: string, result: string): string {
  for (const [rx, label] of GUARD_PATTERNS) {
    if (rx.test(result)) return label;
  }
  const notes: string[] = [];
  if (/\[RIPETIZIONE \d+\/\d+\]/.test(result)) notes.push('⚠️ risultato identico a prima');
  if (/\[STESSO ERRORE \d+ VOLTE\]/.test(result)) notes.push('⚠️ stesso errore ripetuto, il motore chiede di cambiare approccio');
  if (/^\[OUTPUT LUNGO/.test(result)) notes.push('output lungo salvato su file');

  let main = '';
  if (/^ERRORE/i.test(result)) {
    main = `❌ ${firstLine(result.replace(/^ERRORE:?\s*/i, ''), 160)}`;
  } else {
    switch (tool) {
      case 'search_in_files':
      case 'sym_search':
      case 'glob_files':
        main = /^Nessun/i.test(result) ? 'nessun risultato' : `${countLines(result)} risultati`;
        break;
      case 'read_file':
      case 'read_file_range':
        if (/^\[WARNING\]/.test(result)) main = 'file grande: solo anteprima';
        else if (/non trovato/i.test(result)) main = '❌ file non trovato';
        break;
      case 'edit_file':
      case 'write_file': {
        const m = result.match(/riga ~\d+/);
        main = /^✓/.test(result) ? `✅${m ? ' ' + m[0] : ''}` : `❌ ${firstLine(result)}`;
        break;
      }
      case 'delete_file':
        main = /^✓/.test(result) ? '✅ spostato nel cestino recuperabile' : firstLine(result);
        break;
      case 'sol_analyze':
      case 'run_dotnet': {
        const errors = result.match(/error CS\d+/g);
        if (errors) main = `❌ ${new Set(errors).size} tipi di errore (${[...new Set(errors)].slice(0, 3).join(', ')})`;
        else if (/\[NOTA\] Il codice compila/.test(result)) main = '✅ compila (exe in uso da un processo)';
        else if (/Build succeeded|ESITO: SUCCESSO/.test(result)) main = '✅ ok';
        else if (/FALLITO|MSB\d+/.test(result)) main = `❌ ${firstLine(result)}`;
        break;
      }
      case 'run_command': {
        const pid = result.match(/PID (\d+)/);
        if (/avviato in background/.test(result) && pid) { main = `✅ PID ${pid[1]}`; break; }
        // Exit code 0 non basta: uno script con try/catch che stampa "ERR: 401" esce con 0.
        const failed = !/\[ESITO: SUCCESSO\]/.test(result) || /Exception|\bERR(ORE)?\b|\b[45]\d\d\b/.test(result);
        const body = firstLine(result.replace(/\[ESITO:[^\]]*\]/g, '').replace(/^\[OUTPUT LUNGO[^\]]*\]/, ''), 120);
        main = `${failed ? '❌' : '✅'}${body ? ' ' + code(body, 120) : ''}`;
        break;
      }
      case 'get_background_output':
        main = /\[in esecuzione\]/.test(result) ? 'in esecuzione' : firstLine(result, 80);
        break;
      case 'project_notes':
        main = /^✓/.test(result) ? '✅' : '';
        break;
    }
  }
  return [main, ...notes].filter(Boolean).join(' · ');
}

/** Anteprima del risultato per il livello "detailed". */
export function resultPreview(result: string, max = 300): string {
  const flat = result.replace(/\r/g, '').trim();
  return flat.length > max ? flat.slice(0, max) + '…' : flat;
}
