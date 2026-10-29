using LocalCodeAgent.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using pm_mcp;

// Server MCP stdio ADDITIVO: espone gli stessi tool di pm-code (FileSystemTools, TerminalTools,
// GitTools, AgentTools, WebTools, TodoTools, ProjectNotesTools) tramite ToolDispatcher — la
// STESSA classe usata da agent/pm-code/Program.cs, non una copia. Il flusso esistente
// estensione VS Code ↔ pm-code (NDJSON su stdio) resta invariato e non viene toccato da questo
// progetto: pm-mcp è un eseguibile separato, pensato per essere registrato come MCP server
// (.mcp.json / ~/.copilot/mcp-config.json), non per sostituire pm-code.exe.
//
// Configurazione via variabili d'ambiente (mai hardcoded):
//   PM_MCP_WORKSPACE     — root sandbox per i tool filesystem/terminal/git (default: cartella corrente)
//   PM_MCP_WRITE_MODE    — readonly|twostep|direct (default: readonly). Vedi WriteGate.cs:
//                          readonly blocca ogni scrittura, twostep propone diff+token da
//                          applicare con apply_edit, direct scrive subito (solo workspace di prova).
//   PM_LLM_BASE_URL      — URL llama-server, usato solo se in futuro un tool MCP dovesse parlare
//                          direttamente con l'LLM (oggi nessun tool qui lo fa: i tool sono azioni
//                          dirette su file/terminale/git, non round-trip verso il modello)
//   PM_CODE_EXE_PATH     — percorso di pm-code.exe (per il tool opzionale pm_run_task)
//   PM_MCP_BRAVE_API_KEY — API key Brave per web_search (facoltativa; NON committare il valore —
//                          vedi README.md per come impostarla fuori dal repo)
//   PM_MCP_ENABLE_RUN_TASK — "true" per registrare anche pm_run_task (default: false)

var workspacePath = Environment.GetEnvironmentVariable("PM_MCP_WORKSPACE")
    ?? Directory.GetCurrentDirectory();
workspacePath = Path.GetFullPath(workspacePath);

var braveApiKey = Environment.GetEnvironmentVariable("PM_MCP_BRAVE_API_KEY");

var workspace = new WorkspaceContext(workspacePath);
var writeGate = new WriteGate(Environment.GetEnvironmentVariable("PM_MCP_WRITE_MODE"));

// In Direct, proposeWrite resta null: comportamento identico a prima (scrittura immediata via
// FileSystemTools, nessun gate). In ReadOnly/TwoStep, pm-code chiama questo delegate DOPO i suoi
// guard esistenti (RequireRead, limiti dimensione, ecc.) e con il contenuto finale già calcolato:
// il WriteGate decide se rifiutare (readonly) o trasformarlo in un token twostep, ma non scrive
// mai lui stesso in quel punto — vedi WriteGate.ProposeWrite.
Func<string, string, bool>? proposeWrite = writeGate.Mode == WriteMode.Direct
    ? null
    : (relPath, content) => writeGate.ProposeWrite(relPath, content, workspace);

var dispatcher = new ToolDispatcher(workspace, braveApiKey, proposeWrite);

var tools = dispatcher.AllDefinitions
    .Select(def => (AIFunction)new DispatcherTool(dispatcher, def, writeGate, workspace))
    .ToList();

if (writeGate.Mode == WriteMode.TwoStep)
{
    tools.Add(new ApplyEditTool(writeGate, dispatcher, workspace));
    tools.Add(new DiscardEditTool(writeGate));
}

var enableRunTask = string.Equals(
    Environment.GetEnvironmentVariable("PM_MCP_ENABLE_RUN_TASK"), "true",
    StringComparison.OrdinalIgnoreCase);

// PM_MCP_ENABLE_RUN_TASK=true + PM_MCP_WRITE_MODE=twostep è una combinazione incoerente: il
// figlio pm-code --stdin-protocol vede ogni edit_proposal come respinta nello stesso turno (il
// suo protocollo è sincrono, senza nozione di "conferma differita" — vedi PmRunTaskTool.cs), quindi
// il token twostep generato non si inserisce in un flusso che il figlio possa poi riprendere.
// Fallire subito, in modo esplicito, è più sicuro che lasciar credere che "twostep" protegga
// davvero le scritture fatte da pm_run_task.
if (enableRunTask && writeGate.Mode == WriteMode.TwoStep)
{
    Console.Error.WriteLine(
        "ERRORE: PM_MCP_ENABLE_RUN_TASK=true insieme a PM_MCP_WRITE_MODE=twostep non è supportato. " +
        "Il protocollo NDJSON di pm-code --stdin-protocol è sincrono per turno: quando pm_run_task delega " +
        "un edit_proposal del figlio a un token twostep, il figlio lo vede comunque come rifiutato e " +
        "prosegue il turno di conseguenza, quindi applicare poi il token con apply_edit non incide più " +
        "sul turno già concluso. Usa PM_MCP_WRITE_MODE=readonly con pm_run_task (il figlio non scrive), " +
        "oppure PM_MCP_WRITE_MODE=direct SOLO in un worktree git usa-e-getta dedicato. Vedi README.md.");
    Environment.Exit(1);
}

if (enableRunTask)
{
    var pmCodeExePath = Environment.GetEnvironmentVariable("PM_CODE_EXE_PATH")
        ?? ResolveDefaultPmCodeExePath();
    tools.Add(new PmRunTaskTool(pmCodeExePath, workspacePath, writeGate, workspace));
}

var builder = Host.CreateApplicationBuilder(args);

// stdout è riservato al protocollo JSON-RPC MCP: ogni log deve andare su stderr, mai su stdout
// (stesso vincolo di pm-code --stdin-protocol, vedi Program.cs di pm-code).
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new() { Name = "pm-mcp", Version = "0.1.0" };
    })
    .WithStdioServerTransport()
    .WithTools(tools.Select(f => (ModelContextProtocol.Server.McpServerTool)
        ModelContextProtocol.Server.McpServerTool.Create(f, ToolAnnotationsFor(f.Name))));

// Annotazioni MCP standard (readOnlyHint/destructiveHint), supportate nativamente da
// McpServerToolCreateOptions nell'SDK ModelContextProtocol 2.3.0 in uso (verificato per
// reflection sul pacchetto: proprietà ReadOnly/Destructive/Idempotent/OpenWorld, tutte
// Nullable<bool>). Qui vengono impostate solo per i tool elencati esplicitamente: gli altri
// (manage_todo, project_notes, lessons, git_add, git_commit, create_directory, set_workspace,
// sol_analyze, stop_background_job, ecc. — ad azione mista o non puramente distruttiva) restano
// senza hint, per non dichiarare una garanzia che pm-mcp non può verificare con certezza.
static ModelContextProtocol.Server.McpServerToolCreateOptions ToolAnnotationsFor(string name)
{
    var options = new ModelContextProtocol.Server.McpServerToolCreateOptions();

    var readOnly = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "read_file", "read_file_range", "glob_files", "search_in_files", "sym_search",
        "list_directory", "get_workspace_tree", "dir_info",
        "git_status", "git_diff", "git_log",
        "web_search", "web_fetch",
        "get_background_output", "list_background_jobs",
    };
    var destructive = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "edit_file", "write_file", "delete_file", "move_file", "apply_edit",
        "run_command", "run_dotnet",
    };

    if (readOnly.Contains(name)) options.ReadOnly = true;
    if (destructive.Contains(name)) options.Destructive = true;
    return options;
}

await builder.Build().RunAsync();

// Default ragionevole: pm-mcp vive in agent/pm-mcp/, pm-code in agent/pm-code/ — la build
// Debug è il caso comune in sviluppo. In produzione si passa PM_CODE_EXE_PATH esplicito
// (es. l'exe pubblicato standalone).
static string ResolveDefaultPmCodeExePath()
{
    var exeName = OperatingSystem.IsWindows() ? "pm-code.exe" : "pm-code";
    var candidate = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "pm-code", "bin", "Debug", "net8.0", exeName);
    return Path.GetFullPath(candidate);
}
