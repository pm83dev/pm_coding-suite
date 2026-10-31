namespace LocalCodeAgent.Cli;

using LocalCodeAgent.Core;
using LocalCodeAgent.Models;

/// <summary>
/// Modalità REPL interattiva: loop di lettura da console, parsing comandi speciali,
/// history navigation. Costruisce il sink e chiama RunAgentLoopAsync.
/// </summary>
public static class ReplMode
{
    public static async Task RunAsync(CliContext ctx, Func<CancellationToken, Task<bool>> runTurn)
    {
        UI.Dim("Task (es: 'Crea una console app Hello World in C#')");
        UI.Dim("Comandi: /help  /init  /reset  /workspace  /tools  /paste  /cd <path>  quit\n");

        while (true)
        {
            UI.Prompt();
            var userInput = ConsoleInput.ReadLine();

            if (userInput == null) { Console.WriteLine(); continue; } // Ctrl+C
            userInput = userInput.Trim();

            if (string.IsNullOrEmpty(userInput)) continue;
            if (userInput.Equals("quit", StringComparison.OrdinalIgnoreCase) ||
                userInput.Equals("/quit", StringComparison.OrdinalIgnoreCase)) break;
            if (await HandleSpecialCommandAsync(ctx, userInput, runTurn)) { Console.WriteLine(); continue; }

            ctx.History.Add(ChatMessage.User(userInput));
            Console.WriteLine();
            await runTurn(CancellationToken.None);
        }

        UI.Dim("\nArrivederci!");
    }

    private static async Task<bool> HandleSpecialCommandAsync(CliContext ctx, string input, Func<CancellationToken, Task<bool>> runTurn)
    {
        if (!input.Trim().StartsWith("/"))
            return false;

        var lower = input.ToLower().Trim();

        switch (lower)
        {
            case "/reset":
                ctx.Dispatcher.ResetTodo();
                ctx.ResetHistory();
                ctx.Dispatcher.ClearCache();
                ctx.Dispatcher.ResetCwd();
                UI.Ok("Conversazione resettata.");
                return true;

            case "/auto-approve":
                ctx.AutoApprove = true;
                ctx.SaveAutoApproveState(true);
                UI.Ok("Auto-approvazione attivata: write_file/edit_file verranno applicati senza richiesta di conferma.");
                return true;

            case "/no-auto-approve":
                ctx.AutoApprove = false;
                ctx.SaveAutoApproveState(false);
                UI.Ok("Auto-approvazione disattivata: write_file/edit_file richiederanno conferma.");
                return true;

            case "/workspace":
                UI.Dim($"Workspace: {ctx.Workspace.Root}");
                UI.Dim(ctx.Workspace.GetTreeSnapshot());
                return true;

            case "/init":
                UI.Info("Analisi del progetto in corso...");
                ctx.Dispatcher.ClearCache();
                var agentsMdPath = Path.Combine(ctx.Workspace.Root, "AGENTS.md");
                var agentsMdNote = File.Exists(agentsMdPath)
                    ? "AGENTS.md esiste già — aggiornalo, non riscriverlo da zero."
                    : "AGENTS.md non esiste — crealo con write_file.";

                ctx.History.Add(ChatMessage.User($"""
                    Esegui questi passi IN ORDINE STRETTO:
                    1. Chiama get_workspace_tree (una sola volta).
                    2. Usa glob_files per trovare i file di configurazione chiave (.csproj, package.json, ecc.).
                    3. Leggi AL MASSIMO 3 file usando i path ESATTI dal tree. NON inventare path.
                       NON leggere .sln/.slnx. Leggi il .csproj per la versione .NET.
                    4. Chiama OBBLIGATORIAMENTE write_file per scrivere AGENTS.md (max 200 parole).
                       NON includere JSON o il contenuto nel testo — usa il tool write_file.

                    Se read_file restituisce [WARNING], saltalo e passa al prossimo.

                    AGENTS.md deve contenere:
                    - Scopo del progetto (1 riga)
                    - Stack e versioni reali
                    - Comandi build/run con path esatti
                    - Struttura cartelle principale (3-4 righe)
                    - Note operative per agente AI (2-3 righe)

                    {agentsMdNote}
                    """));

                Console.WriteLine();
                bool initOk = await runTurn(CancellationToken.None);

                if (initOk && !File.Exists(agentsMdPath))
                {
                    UI.Info("write_file non eseguito — forzo secondo tentativo...");
                    ctx.History.Add(ChatMessage.User(
                        "ERRORE: hai incluso il JSON nel testo invece di chiamare write_file come tool. " +
                        "Chiama ADESSO write_file come tool call con il contenuto di AGENTS.md."));
                    await runTurn(CancellationToken.None);
                }

                if (File.Exists(agentsMdPath))
                    UI.Ok("AGENTS.md creato/aggiornato.");
                else
                    UI.Error("AGENTS.md non creato — riprova con /init.");
                return true;

            case "/help":
                UI.Dim("Comandi:");
                UI.Dim("  /init        — Analizza il progetto e genera AGENTS.md");
                UI.Dim("  /reset       — Nuova conversazione (ricarica AGENTS.md)");
                UI.Dim("  /auto-approve — Attiva auto-approvazione write_file/edit_file (persistita)");
                UI.Dim("  /no-auto-approve — Disattiva auto-approvazione (persistita)");
                UI.Dim("  /workspace   — Mostra struttura workspace");
                UI.Dim("  /tools       — Elenca tutti i tool disponibili");
                UI.Dim("  /cd <path>   — Cambia workspace");
                UI.Dim("  /clear       — Pulisce lo schermo");
                UI.Dim("  /paste       — Modalità input multiriga (termina con '---')");
                UI.Dim("  ↑ / ↓        — Naviga cronologia comandi");
                UI.Dim("  quit / /quit — Esci");
                return true;

            case "/clear":
                try { Console.Clear(); } catch { }
                return true;

            case "/paste":
                UI.Dim("Inserisci il testo (termina con '---' su una riga a sé):");
                var sb = new System.Text.StringBuilder();
                string? ln;
                while ((ln = Console.ReadLine()) != null && ln.Trim() != "---")
                    sb.AppendLine(ln);
                var pastedText = sb.ToString().TrimEnd();
                if (!string.IsNullOrEmpty(pastedText))
                {
                    ctx.History.Add(ChatMessage.User(pastedText));
                    Console.WriteLine();
                    await runTurn(CancellationToken.None);
                }
                return true;

            default:
                if (input.StartsWith("/cd ", StringComparison.OrdinalIgnoreCase))
                {
                    var newPath = input[4..].Trim();
                    try
                    {
                        ctx.ChangeWorkspace(newPath);
                        UI.Ok($"Workspace: {ctx.Workspace.Root}");
                        var hasAgentsMd = File.Exists(Path.Combine(ctx.Workspace.Root, "AGENTS.md"));
                        UI.Dim(hasAgentsMd ? "AGENTS.md trovato." : "Nessun AGENTS.md — usa /init.");
                    }
                    catch (Exception ex) { UI.Error(ex.Message); }
                    return true;
                }
                return false;
        }
    }
}