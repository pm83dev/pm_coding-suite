using System.Text.Json;
using LocalCodeAgent.Core;
using LocalCodeAgent.Models;

namespace LocalCodeAgent.Tools;

/// <summary>
/// Memoria persistente del progetto (.pm-agent/notes.json): fatti che l'agente scopre una volta e
/// che servono in ogni sessione successiva — come avviare l'app, come testarla end-to-end, quale
/// progetto buildare, endpoint, dati di test, insidie note. Le note entrano nel system prompt, così
/// una nuova chat non deve riscoprire tutto (ed è questo che permette di validare da soli il
/// risultato invece di fermarsi alla build).
/// </summary>
public class ProjectNotesTools(WorkspaceContext workspace)
{
    private const int MaxNotes = 40;
    private const int MaxNoteLength = 400;

    private string StateFile => Path.Combine(workspace.Root, AgentStateDir.DirName, "notes.json");

    public List<ToolDefinition> Definitions =>
    [
        new() { Function = new() {
            Name = "project_notes",
            Description = "Memoria permanente del progetto, mostrata in ogni sessione futura. Salva (action=\"add\") ciò che hai " +
                           "verificato e che servirà di nuovo: come avviare l'app, il comando/la procedura di TEST FUNZIONALE " +
                           "end-to-end con il risultato atteso, quale progetto buildare, endpoint, come autenticarsi, dati di test, " +
                           "insidie scoperte. Una nota = un fatto breve e verificato. action=\"remove\" con id per togliere una nota " +
                           "sbagliata o superata, action=\"list\" per rileggerle.",
            Parameters = new { type = "object",
                properties = new {
                    action = new { type = "string", description = "\"add\", \"remove\" o \"list\"" },
                    note   = new { type = "string", description = "Testo della nota (per add), max 400 caratteri" },
                    id     = new { type = "integer", description = "Id della nota (per remove)" }
                },
                required = new[] { "action" } }
        }}
    ];

    public string Execute(string argumentsJson)
    {
        try
        {
            var args = JsonSerializer.Deserialize<JsonElement>(argumentsJson);
            var action = args.TryGetProperty("action", out var a) ? a.GetString() ?? "" : "";
            var notes = Load();

            switch (action.ToLowerInvariant())
            {
                case "add":
                    var text = args.TryGetProperty("note", out var n) ? n.GetString()?.Trim() ?? "" : "";
                    if (text.Length == 0) return "ERRORE: 'note' vuota.";
                    if (text.Length > MaxNoteLength) text = text[..MaxNoteLength];
                    if (notes.Any(x => x.Text.Equals(text, StringComparison.OrdinalIgnoreCase)))
                        return "Nota già presente.";
                    if (notes.Count >= MaxNotes)
                        return $"ERRORE: raggiunto il massimo di {MaxNotes} note — rimuovi quelle superate (action=\"remove\").";
                    var id = notes.Count == 0 ? 1 : notes.Max(x => x.Id) + 1;
                    notes.Add(new Note { Id = id, Text = text });
                    Save(notes);
                    return $"✓ Nota {id} salvata.";

                case "remove":
                    if (!args.TryGetProperty("id", out var idEl) || !idEl.TryGetInt32(out var rid))
                        return "ERRORE: 'id' mancante.";
                    if (notes.RemoveAll(x => x.Id == rid) == 0) return $"Nota {rid} non trovata.";
                    Save(notes);
                    return $"✓ Nota {rid} rimossa.";

                case "list":
                    return notes.Count == 0 ? "Nessuna nota." : string.Join('\n', notes.Select(x => $"[{x.Id}] {x.Text}"));

                default:
                    return $"ERRORE: action '{action}' non valida — usa 'add', 'remove' o 'list'.";
            }
        }
        catch (Exception ex)
        {
            return $"ERRORE: {ex.Message}";
        }
    }

    /// <summary>Testo per il system prompt, o null se non ci sono note.</summary>
    public string? PromptSummary()
    {
        var notes = Load();
        return notes.Count == 0 ? null : string.Join('\n', notes.Select(x => $"  [{x.Id}] {x.Text}"));
    }

    private List<Note> Load()
    {
        try
        {
            return File.Exists(StateFile)
                ? JsonSerializer.Deserialize<List<Note>>(File.ReadAllText(StateFile)) ?? []
                : [];
        }
        catch { return []; }
    }

    private void Save(List<Note> notes)
    {
        AgentStateDir.Ensure(workspace.Root);
        File.WriteAllText(StateFile, JsonSerializer.Serialize(notes, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed class Note
    {
        public int Id { get; set; }
        public string Text { get; set; } = "";
    }
}
