using System.Text.Json;
using System.Text.Json.Serialization;
using LocalCodeAgent.Core;
using LocalCodeAgent.Models;

namespace LocalCodeAgent.Tools;

public enum TodoStatus { Pending, InProgress, Completed }

public class TodoItem
{
    public string Id { get; set; } = "";
    public string Content { get; set; } = "";
    public TodoStatus Status { get; set; } = TodoStatus.Pending;
    // Cosa è stato fatto/scoperto in questo passo (file, righe, esito del test): è la memoria
    // che permette di riprendere il piano in una nuova chat senza rianalizzare tutto da zero.
    public string? Note { get; set; }
}

/// <summary>
/// Tool per gestire un piano/checklist esplicito per task multi-step. Il piano è salvato in
/// .pm-agent/plan.json nel workspace: sopravvive alla nuova chat e al riavvio del processo,
/// così un "prosegui" riparte dal primo passo non completato invece di ricostruire il lavoro
/// leggendo diff e file per decine di step. Si azzera solo con action="clear".
/// </summary>
public class TodoTools(WorkspaceContext workspace)
{
    public const string AgentHintMarker = "\n\n[PER L'AGENTE] ";

    private List<TodoItem> _items = [];
    private int _nextId = 1;

    private static readonly JsonSerializerOptions StateJson = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private string StateFile => Path.Combine(workspace.Root, AgentStateDir.DirName, "plan.json");

    public List<ToolDefinition> Definitions =>
    [
        new() { Function = new() {
            Name = "manage_todo",
            Description = "Piano di lavoro a PICCOLI PASSI, salvato su disco (sopravvive alla nuova chat). " +
                           "Per task con 3+ step: \"create\" PRIMA di iniziare, con passi piccoli (ognuno = UNA modifica " +
                           "verificabile con build/test). Poi per ogni passo: \"update\" a in_progress, esegui, verifica, " +
                           "\"update\" a completed con una note breve (cosa hai cambiato, file:riga, esito test) e passa SUBITO al successivo. " +
                           "\"clear\" cancella il piano quando il lavoro è finito o abbandonato.",
            Parameters = new { type = "object",
                properties = new {
                    action = new { type = "string", description = "\"create\" (sostituisce l'intero piano), \"update\" (aggiorna item esistenti per id) o \"clear\"" },
                    items = new { type = "array", description = "Lista di item del piano (non serve per clear)",
                        items = new { type = "object",
                            properties = new {
                                id      = new { type = "string", description = "Obbligatorio solo per update; ignorato per create (assegnato automaticamente)" },
                                content = new { type = "string", description = "Descrizione breve dello step" },
                                status  = new { type = "string", description = "\"pending\", \"in_progress\" o \"completed\"" },
                                note    = new { type = "string", description = "Esito del passo: cosa hai cambiato/scoperto (file:riga, risultato build/test)" }
                            }
                        }
                    }
                },
                required = new[] { "action" } }
        }}
    ];

    /// <summary>Ricarica il piano salvato per il workspace corrente (nuova chat, /reset, /cd).</summary>
    public void Reload()
    {
        _items = [];
        _nextId = 1;
        try
        {
            if (!File.Exists(StateFile)) return;
            var state = JsonSerializer.Deserialize<PlanState>(File.ReadAllText(StateFile), StateJson);
            if (state == null) return;
            _items = state.Items ?? [];
            _nextId = Math.Max(state.NextId, 1);
        }
        catch { /* file corrotto: si riparte da un piano vuoto */ }
    }

    private void Save()
    {
        try
        {
            if (_items.Count == 0)
            {
                if (File.Exists(StateFile)) File.Delete(StateFile);
                return;
            }
            AgentStateDir.Ensure(workspace.Root);
            File.WriteAllText(StateFile, JsonSerializer.Serialize(new PlanState { Items = _items, NextId = _nextId }, StateJson));
        }
        catch { /* la persistenza è best effort: il piano resta valido in memoria */ }
    }

    /// <summary>
    /// Testo da mettere nel system prompt se c'è un piano non concluso, altrimenti null.
    /// </summary>
    public string? PromptSummary()
    {
        if (!_items.Any(i => i.Status != TodoStatus.Completed)) return null;
        var lines = _items.Select(i =>
        {
            var mark = i.Status switch
            {
                TodoStatus.Completed  => "[completato]",
                TodoStatus.InProgress => "[IN CORSO]",
                _                     => "[da fare]",
            };
            var note = string.IsNullOrWhiteSpace(i.Note) ? "" : $" — nota: {i.Note}";
            return $"  id={i.Id} {mark} {i.Content}{note}";
        });
        return string.Join('\n', lines);
    }

    public bool HasPendingItems => _items.Any(i => i.Status != TodoStatus.Completed);

    public string Execute(string argumentsJson)
    {
        try
        {
            var args = JsonSerializer.Deserialize<JsonElement>(argumentsJson);
            var action = args.TryGetProperty("action", out var a) ? a.GetString() ?? "" : "";
            var itemsEl = args.TryGetProperty("items", out var it) && it.ValueKind == JsonValueKind.Array
                ? it : default;

            var unknownIds = new List<string>();

            if (action.Equals("create", StringComparison.OrdinalIgnoreCase))
            {
                _nextId = 1;
                var newItems = new List<TodoItem>();
                if (itemsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in itemsEl.EnumerateArray())
                        newItems.Add(ParseItem(el, assignId: true));
                }
                _items = newItems;
            }
            else if (action.Equals("update", StringComparison.OrdinalIgnoreCase))
            {
                if (itemsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in itemsEl.EnumerateArray())
                    {
                        var id = el.TryGetProperty("id", out var idEl) ? idEl.ToString() : "";
                        var existing = _items.FirstOrDefault(x => x.Id == id);
                        if (existing == null) { if (!string.IsNullOrEmpty(id)) unknownIds.Add(id); continue; }

                        if (el.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                            existing.Content = c.GetString()!;
                        if (el.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String)
                            existing.Status = ParseStatus(s.GetString());
                        if (el.TryGetProperty("note", out var n) && n.ValueKind == JsonValueKind.String)
                            existing.Note = n.GetString();
                    }
                }
            }
            else if (action.Equals("clear", StringComparison.OrdinalIgnoreCase))
            {
                _items = [];
                _nextId = 1;
                Save();
                return "Piano cancellato.";
            }
            else
            {
                return $"ERRORE: action '{action}' non valida — usa 'create', 'update' o 'clear'.";
            }

            Save();

            var markdown = FormatMarkdown();
            // Gli id sono assegnati dal tool: senza mostrarli il modello inventava id propri
            // ("baseline", "a1"...) e gli update finivano ignorati.
            var ids = string.Join(", ", _items.Select(i => $"{i.Id}={Short(i.Content)}"));
            var next = _items.FirstOrDefault(i => i.Status == TodoStatus.InProgress)
                    ?? _items.FirstOrDefault(i => i.Status == TodoStatus.Pending);
            // Il marcatore AgentHintMarker separa la parte per il modello: l'estensione VS Code
            // mostra in chat solo il piano, tagliando da qui in poi.
            var hint = AgentHintMarker + (next == null
                ? "Tutti i passi sono completati: chiama manage_todo action=\"clear\" e rispondi all'utente con il riepilogo."
                : $"Id da usare negli update: {ids}\nPROSSIMO PASSO: id={next.Id} \"{next.Content}\" — eseguilo ORA (lettura mirata, modifica, verifica).");
            return (unknownIds.Count > 0
                ? $"{markdown}\n\n(id non trovati, ignorati: {string.Join(", ", unknownIds)})"
                : markdown) + hint;
        }
        catch (Exception ex)
        {
            return $"ERRORE: {ex.Message}";
        }
    }

    private static string Short(string s) => s.Length <= 40 ? s : s[..40] + "…";

    private TodoItem ParseItem(JsonElement el, bool assignId)
    {
        var id = !assignId && el.TryGetProperty("id", out var idEl) ? idEl.ToString() : "";
        if (string.IsNullOrEmpty(id)) id = (_nextId++).ToString();
        var content = el.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
        var status = el.TryGetProperty("status", out var s) ? ParseStatus(s.GetString()) : TodoStatus.Pending;
        var note = el.TryGetProperty("note", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
        return new TodoItem { Id = id, Content = content, Status = status, Note = note };
    }

    private static TodoStatus ParseStatus(string? s) => s?.ToLowerInvariant() switch
    {
        "in_progress" or "in-progress" => TodoStatus.InProgress,
        "completed" or "done" => TodoStatus.Completed,
        _ => TodoStatus.Pending,
    };

    private string FormatMarkdown()
    {
        if (_items.Count == 0) return "Piano vuoto.";
        var lines = _items.Select(i => i.Status switch
        {
            TodoStatus.Completed  => $"✅ {i.Content}",
            TodoStatus.InProgress => $"🔄 {i.Content}",
            _                     => $"⬜ {i.Content}",
        });
        return "**Piano:**\n" + string.Join('\n', lines);
    }

    private sealed class PlanState
    {
        public List<TodoItem>? Items { get; set; }
        public int NextId { get; set; } = 1;
    }
}
