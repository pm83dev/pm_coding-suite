using System.Text.Json;
using LocalCodeAgent.Models;

namespace LocalCodeAgent.Tools;

public enum TodoStatus { Pending, InProgress, Completed }

public class TodoItem
{
    public string Id { get; set; } = "";
    public string Content { get; set; } = "";
    public TodoStatus Status { get; set; } = TodoStatus.Pending;
}

/// <summary>
/// Tool per gestire un piano/checklist esplicito per task multi-step. Lo stato vive
/// nell'istanza (quindi persiste tra i turni della stessa conversazione, coerente con
/// la history persistente del processo) e si azzera solo su /reset o nuova chat.
/// </summary>
public class TodoTools
{
    private List<TodoItem> _items = [];
    private int _nextId = 1;

    public List<ToolDefinition> Definitions =>
    [
        new() { Function = new() {
            Name = "manage_todo",
            Description = "Crea o aggiorna una checklist/piano esplicito per task che richiedono più passaggi " +
                           "(es. refactoring multi-file, migrazioni, debugging con più ipotesi). " +
                           "Usalo quando il task ha 3+ step distinti: crea il piano PRIMA di iniziare, " +
                           "poi aggiorna lo stato di ogni item (in_progress → completed) man mano che procedi. " +
                           "Non serve per task con 1-2 azioni semplici.",
            Parameters = new { type = "object",
                properties = new {
                    action = new { type = "string", description = "\"create\" (sostituisce l'intero piano) o \"update\" (aggiorna item esistenti per id)" },
                    items = new { type = "array", description = "Lista di item del piano",
                        items = new { type = "object",
                            properties = new {
                                id      = new { type = "string", description = "Obbligatorio solo per update; ignorato per create (assegnato automaticamente)" },
                                content = new { type = "string", description = "Descrizione breve dello step" },
                                status  = new { type = "string", description = "\"pending\", \"in_progress\" o \"completed\"" }
                            }
                        }
                    }
                },
                required = new[] { "action", "items" } }
        }}
    ];

    public void Reset()
    {
        _items = [];
        _nextId = 1;
    }

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
                        var id = el.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                        var existing = _items.FirstOrDefault(x => x.Id == id);
                        if (existing == null) { if (!string.IsNullOrEmpty(id)) unknownIds.Add(id); continue; }

                        if (el.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                            existing.Content = c.GetString()!;
                        if (el.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String)
                            existing.Status = ParseStatus(s.GetString());
                    }
                }
            }
            else
            {
                return $"ERRORE: action '{action}' non valida — usa 'create' o 'update'.";
            }

            var markdown = FormatMarkdown();
            return unknownIds.Count > 0
                ? $"{markdown}\n\n(id non trovati, ignorati: {string.Join(", ", unknownIds)})"
                : markdown;
        }
        catch (Exception ex)
        {
            return $"ERRORE: {ex.Message}";
        }
    }

    private TodoItem ParseItem(JsonElement el, bool assignId)
    {
        var id = !assignId && el.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(id)) id = (_nextId++).ToString();
        var content = el.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
        var status = el.TryGetProperty("status", out var s) ? ParseStatus(s.GetString()) : TodoStatus.Pending;
        return new TodoItem { Id = id, Content = content, Status = status };
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
}
