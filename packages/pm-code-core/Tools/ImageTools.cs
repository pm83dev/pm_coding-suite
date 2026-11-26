using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LocalCodeAgent.Core;
using LocalCodeAgent.Models;

namespace LocalCodeAgent.Tools;

/// <summary>
/// Tool "analyze_image": il modello di testo dell'agente non vede le immagini, quindi la
/// lettura è delegata a un endpoint multimodale OpenAI-compatibile (VisionSettings, es.
/// llama-server con --mmproj). All'agente torna solo la descrizione testuale: l'immagine non
/// entra mai nella sua history. Se VisionSettings non è configurato il tool NON è esposto
/// (Definitions vuoto) — così il blocco tool, e la prompt cache, restano stabili.
/// </summary>
public class ImageTools(WorkspaceContext workspace, VisionSettings? settings)
{
    public const string ToolName = "analyze_image";

    private static readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private static readonly Dictionary<string, string> MimeByExt = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".bmp"] = "image/bmp",
    };

    private bool Enabled => settings is { IsConfigured: true };

    public List<ToolDefinition> Definitions => !Enabled ? [] :
    [
        new() { Function = new() {
            Name = ToolName,
            Description = "Analizza un'immagine (screenshot, mockup, diagramma, foto) tramite un modello con visione e " +
                          "restituisce una descrizione testuale. Tu non vedi le immagini: usa SEMPRE questo tool quando " +
                          "devi consultare uno screen come riferimento. Formati: png, jpg, gif, webp, bmp. " +
                          "Fai una domanda precisa (es. \"elenca i campi del form e le etichette\", \"trascrivi i testi\", " +
                          "\"descrivi layout e colori\"): la risposta è testo, l'immagine non resta nel tuo contesto.",
            Parameters = new { type = "object",
                properties = new {
                    path     = new { type = "string", description = "Percorso dell'immagine (relativo al workspace o assoluto)" },
                    question = new { type = "string", description = "Cosa vuoi sapere dell'immagine (default: descrizione dettagliata di UI, testi e layout)" }
                },
                required = new[] { "path" } }
        }}
    ];

    public string Execute(string toolName, string argumentsJson, CancellationToken ct = default)
    {
        if (toolName != ToolName) return $"Tool '{toolName}' non trovato.";
        if (!Enabled)
            return "ERRORE: analisi immagini non configurata (VisionSettings:BaseUrl/Model in appsettings.json).";
        try
        {
            return AnalyzeAsync(argumentsJson, ct).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            return $"ERRORE: il modello di visione non ha risposto entro {settings!.TimeoutSeconds}s.";
        }
        catch (Exception ex) { return $"ERRORE: {ex.Message}"; }
    }

    private async Task<string> AnalyzeAsync(string argumentsJson, CancellationToken ct)
    {
        var el = JsonDocument.Parse(argumentsJson).RootElement;
        var path = el.TryGetProperty("path", out var p) ? p.GetString()?.Trim() ?? "" : "";
        var question = el.TryGetProperty("question", out var q) ? q.GetString()?.Trim() ?? "" : "";
        if (path.Length == 0) return "ERRORE: 'path' vuoto.";
        if (question.Length == 0) question = settings!.DefaultQuestion;

        // Percorso assoluto consentito solo se la cartella è in VisionSettings.AllowedDirs
        // (es. la cartella degli screenshot): il resto resta confinato nel workspace.
        string full;
        if (Path.IsPathRooted(path))
        {
            full = Path.GetFullPath(path);
            var inWorkspace = full.StartsWith(workspace.Root, StringComparison.OrdinalIgnoreCase);
            var allowed = settings!.AllowedDirs.Any(d =>
                full.StartsWith(Path.GetFullPath(d), StringComparison.OrdinalIgnoreCase));
            if (!inWorkspace && !allowed)
                return $"ERRORE: percorso fuori dal workspace e non in VisionSettings:AllowedDirs: {path}";
        }
        else full = workspace.Resolve(path);

        if (!File.Exists(full)) return $"ERRORE: file non trovato: {path}";
        var ext = Path.GetExtension(full);
        if (!MimeByExt.TryGetValue(ext, out var mime))
            return $"ERRORE: formato '{ext}' non supportato (ammessi: {string.Join(", ", MimeByExt.Keys)}).";
        var size = new FileInfo(full).Length;
        if (size > settings!.MaxImageBytes)
            return $"ERRORE: immagine troppo grande ({size / 1024} KB, massimo {settings.MaxImageBytes / 1024} KB). Ridimensionala.";

        var dataUrl = $"data:{mime};base64,{Convert.ToBase64String(await File.ReadAllBytesAsync(full, ct))}";
        var body = JsonSerializer.Serialize(new
        {
            model = settings.Model,
            max_tokens = settings.MaxTokens,
            temperature = settings.Temperature,
            messages = new object[]
            {
                new { role = "system", content = settings.SystemPrompt },
                new { role = "user", content = new object[]
                {
                    new { type = "text", text = question },
                    new { type = "image_url", image_url = new { url = dataUrl } },
                } },
            },
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{settings.BaseUrl.TrimEnd('/')}/v1/chat/completions")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrWhiteSpace(settings.ApiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, settings.TimeoutSeconds)));
        using var resp = await _http.SendAsync(req, cts.Token);
        var text = await resp.Content.ReadAsStringAsync(cts.Token);
        if (!resp.IsSuccessStatusCode)
            return $"ERRORE: il server di visione ha risposto HTTP {(int)resp.StatusCode}: " +
                   (text.Length > 300 ? text[..300] + "…" : text) +
                   " (il modello è multimodale? llama-server richiede --mmproj).";

        using var doc = JsonDocument.Parse(text);
        var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message")
            .TryGetProperty("content", out var c) ? c.GetString() : null;
        if (string.IsNullOrWhiteSpace(content)) return "ERRORE: il modello di visione non ha prodotto alcuna descrizione.";
        if (content.Length > settings.MaxAnswerChars)
            content = content[..settings.MaxAnswerChars] + "…(troncato)";
        return $"[IMMAGINE {Path.GetFileName(full)}]\n{content.Trim()}";
    }
}
