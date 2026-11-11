using System.Text.Json;
using System.Text.Json.Serialization;
using LocalCodeAgent.Models;

namespace AgentBackend.Services;

/// <summary>
/// Persiste le sessioni su disco (JSON) così sopravvivono ai riavvii del backend.
/// Ogni sessione è un file "sessions/&lt;sessionId&gt;.json" con metadati + storico.
/// Gli oggetti di runtime (LlmClient, Workspace, Dispatcher, SessionManager) NON vengono
/// serializzati: vengono ricreati al caricamento a partire da WorkspaceRoot + Model.
/// Un turno in corso al momento del riavvio è morto: al load si forza sempre
/// TurnInProgress=false e Status="idle".
/// </summary>
public static class SessionPersistence
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Risolve la cartella dove salvare le sessioni (e la crea).</summary>
    public static string ResolveDir(string? configuredDir)
    {
        var dir = !string.IsNullOrWhiteSpace(configuredDir)
            ? configuredDir
            : Path.Combine(AppContext.BaseDirectory, "sessions");
        // Se il percorso è relativo, lo ancoriamo alla cartella del backend.
        if (!Path.IsPathRooted(dir))
            dir = Path.Combine(AppContext.BaseDirectory, dir);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string SessionPath(string dir, string sessionId) =>
        Path.Combine(dir, $"{sessionId}.json");

    /// <summary>
    /// DTO serializzabile: solo i campi che sopravvivono al riavvio. Lo storico va
    /// per intero; Status/TurnInProgress vengono ignorati al load (si riparte idle).
    /// </summary>
    public class SessionFile
    {
        [JsonPropertyName("sessionId")] public string SessionId { get; set; } = "";
        [JsonPropertyName("title")] public string Title { get; set; } = "";
        [JsonPropertyName("workspaceRoot")] public string WorkspaceRoot { get; set; } = "";
        [JsonPropertyName("model")] public string Model { get; set; } = "";
        [JsonPropertyName("permissionMode")] public string PermissionMode { get; set; } = "ask";
        [JsonPropertyName("updatedAt")] public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        [JsonPropertyName("history")] public List<ChatMessage> History { get; set; } = [];
    }

    /// <summary>
    /// Salva una sessione su disco. Best-effort: non deve mai rompere il flusso.
    /// Scrittura atomica (.tmp + File.Move): se la Move fallisce per un lock
    /// transitorio (antivirus/indicizzazione/un lettore esterno che tiene aperto
    /// il file di destinazione — osservato in pratica), ritenta un paio di volte
    /// prima di arrendersi, invece di lasciare silenziosamente .tmp orfano e la
    /// history dell'ultimo turno mai persistita (il turno in sé non è perso: è
    /// già stato consegnato via WebSocket, resta solo non recuperabile a un
    /// riavvio/reconnessione finché non viene ri-salvato).
    /// </summary>
    public static void Save(string dir, AgentSession session)
    {
        var dto = new SessionFile
        {
            SessionId = session.SessionId,
            Title = session.Title,
            WorkspaceRoot = session.WorkspaceRoot,
            Model = session.Model,
            PermissionMode = session.PermissionMode,
            UpdatedAt = session.UpdatedAt,
            History = session.History,
        };
        var path = SessionPath(dir, session.SessionId);
        var tmp = path + ".tmp";
        var json = JsonSerializer.Serialize(dto, JsonOpts);

        const int maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                File.WriteAllText(tmp, json);
                File.Move(tmp, path, overwrite: true);
                return;
            }
            catch when (attempt < maxAttempts)
            {
                Thread.Sleep(50 * attempt);
            }
            catch { /* ultimo tentativo fallito: la persistenza non deve mai rompere il turno */ }
        }
    }

    /// <summary>Elimina il file di una sessione (best-effort).</summary>
    public static void Delete(string dir, string sessionId)
    {
        try
        {
            var path = SessionPath(dir, sessionId);
            if (File.Exists(path)) File.Delete(path);
        }
        catch { /* best effort */ }
    }

    /// <summary>
    /// Carica tutte le sessioni da disco. Restituisce le DTO; chi chiama (AgentSessionService)
    /// ricrea gli oggetti di runtime e le inserisce nel dizionario in memoria.
    /// I file corrotti vengono saltati (e rinominati in .corrupt per non bloccare).
    /// Prima recupera eventuali ".json.tmp" orfani (una Save interrotta prima della Move,
    /// es. per un lock transitorio sul file di destinazione): se il contenuto è JSON valido
    /// lo promuove a ".json", altrimenti lo scarta — non deve mai bloccare l'avvio.
    /// </summary>
    public static List<SessionFile> LoadAll(string dir)
    {
        var result = new List<SessionFile>();
        if (!Directory.Exists(dir)) return result;

        foreach (var tmpFile in Directory.GetFiles(dir, "*.json.tmp"))
        {
            var finalPath = tmpFile[..^".tmp".Length];

            // Validazione JSON separata dalla Move: solo un .tmp illeggibile va scartato.
            // Se invece è valido ma la Move fallisce ancora (stesso lock transitorio di
            // Save), il .tmp resta lì intatto per il prossimo avvio — non va MAI cancellato
            // in quel caso, sarebbe perdita silenziosa dell'ultimo turno della sessione.
            bool validJson;
            try { JsonSerializer.Deserialize<SessionFile>(File.ReadAllText(tmpFile), JsonOpts); validJson = true; }
            catch { validJson = false; }

            if (!validJson) { try { File.Delete(tmpFile); } catch { } continue; }

            const int maxAttempts = 3;
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try { File.Move(tmpFile, finalPath, overwrite: true); break; }
                catch when (attempt < maxAttempts) { Thread.Sleep(50 * attempt); }
                catch { /* resta orfano: ritentato al prossimo avvio, non lo elimino */ }
            }
        }

        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            try
            {
                var dto = JsonSerializer.Deserialize<SessionFile>(File.ReadAllText(file), JsonOpts);
                if (dto != null && !string.IsNullOrEmpty(dto.SessionId))
                    result.Add(dto);
            }
            catch
            {
                // File corrotto: lo rinomino per non riprovare ad ogni avvio.
                try { File.Move(file, file + ".corrupt", overwrite: true); } catch { }
            }
        }
        return result;
    }
}