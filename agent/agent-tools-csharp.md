# Agent Tools — Implementazione C# per agente locale (llama-server)

Replica degli 8 tool built-in di Claude Managed Agents, adattati per un agente C# che gira contro `llama-server` (llama.cpp HTTP API).

---

## Architettura generale

Ogni tool segue lo stesso contratto:

```csharp
public interface IAgentTool
{
    string Name { get; }
    string Description { get; }
    object InputSchema { get; }               // JSON Schema del parametro
    Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken ct = default);
}

public record ToolResult(bool Success, string Output, string? Error = null);
```

Il loop agente:
1. Invia la conversazione + lista tool a `llama-server`
2. Riceve una `tool_call` (nome + argomenti JSON)
3. Dispatcha al tool corretto → esegue → restituisce il risultato come `tool` message
4. Ripete fino a risposta finale

---

## 1. Bash — Esecuzione comandi shell

**Scopo:** esegue comandi arbitrari in una shell session con CWD persistente tra chiamate.

```csharp
public class BashTool : IAgentTool
{
    public string Name => "bash";
    public string Description =>
        "Esegue un comando bash. Usa per operazioni su file, compilazione, esecuzione di script, " +
        "installazione pacchetti, ecc. La working directory persiste tra chiamate successive.";

    public object InputSchema => new
    {
        type = "object",
        properties = new
        {
            command = new { type = "string", description = "Comando bash da eseguire" },
            timeout_ms = new { type = "integer", description = "Timeout in ms (default 30000)" }
        },
        required = new[] { "command" }
    };

    private string _cwd = Directory.GetCurrentDirectory();

    public async Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken ct = default)
    {
        var command = input.GetProperty("command").GetString()!;
        var timeout = input.TryGetProperty("timeout_ms", out var t) ? t.GetInt32() : 30_000;

        // Estrai eventuale "cd <dir>" e aggiorna _cwd
        if (command.TrimStart().StartsWith("cd "))
        {
            var dir = command.TrimStart()[3..].Trim();
            var newDir = Path.GetFullPath(Path.Combine(_cwd, dir));
            if (Directory.Exists(newDir)) _cwd = newDir;
            return new ToolResult(true, $"cwd: {_cwd}");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        var psi = new ProcessStartInfo
        {
            FileName = "/bin/bash",       // su Windows: "cmd.exe", Arguments = "/c ..."
            Arguments = $"-c \"{command.Replace("\"", "\\\"")}\"",
            WorkingDirectory = _cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var proc = Process.Start(psi)!;
        var stdout = await proc.StandardOutput.ReadToEndAsync(cts.Token);
        var stderr = await proc.StandardError.ReadToEndAsync(cts.Token);
        await proc.WaitForExitAsync(cts.Token);

        var output = string.IsNullOrEmpty(stderr)
            ? stdout
            : $"{stdout}\n[stderr]\n{stderr}";

        return new ToolResult(proc.ExitCode == 0, output.Trim(),
            proc.ExitCode != 0 ? $"Exit code: {proc.ExitCode}" : null);
    }
}
```

**Note:**
- Su Windows sostituisci `FileName = "cmd.exe"`, `Arguments = $"/c {command}"`
- Considera un `SemaphoreSlim` se l'agente è multi-thread

---

## 2. Read — Lettura file

**Scopo:** legge un file dal filesystem. Supporta lettura parziale per file grandi.

```csharp
public class ReadTool : IAgentTool
{
    public string Name => "read";
    public string Description =>
        "Legge il contenuto di un file. Specifica offset e limit per file grandi. " +
        "Restituisce il testo con numerazione di riga.";

    public object InputSchema => new
    {
        type = "object",
        properties = new
        {
            path    = new { type = "string",  description = "Percorso assoluto o relativo del file" },
            offset  = new { type = "integer", description = "Riga di inizio (1-based, default 1)" },
            limit   = new { type = "integer", description = "Numero max di righe da leggere (default tutte)" }
        },
        required = new[] { "path" }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken ct = default)
    {
        var path   = input.GetProperty("path").GetString()!;
        var offset = input.TryGetProperty("offset", out var o) ? o.GetInt32() - 1 : 0;
        var limit  = input.TryGetProperty("limit",  out var l) ? l.GetInt32()     : int.MaxValue;

        if (!File.Exists(path))
            return new ToolResult(false, "", $"File non trovato: {path}");

        var lines = await File.ReadAllLinesAsync(path, ct);
        var slice = lines.Skip(offset).Take(limit).ToArray();

        var sb = new StringBuilder();
        for (int i = 0; i < slice.Length; i++)
            sb.AppendLine($"{offset + i + 1}\t{slice[i]}");

        return new ToolResult(true, sb.ToString());
    }
}
```

---

## 3. Write — Scrittura file

**Scopo:** crea o sovrascrive un file. Crea automaticamente le directory mancanti.

```csharp
public class WriteTool : IAgentTool
{
    public string Name => "write";
    public string Description =>
        "Scrive contenuto in un file, creando directory intermedie se necessario. " +
        "Sovrascrive se il file esiste già. Usa Edit per modifiche parziali.";

    public object InputSchema => new
    {
        type = "object",
        properties = new
        {
            path    = new { type = "string", description = "Percorso del file da scrivere" },
            content = new { type = "string", description = "Contenuto da scrivere" }
        },
        required = new[] { "path", "content" }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken ct = default)
    {
        var path    = input.GetProperty("path").GetString()!;
        var content = input.GetProperty("content").GetString()!;

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(path, content, ct);
        return new ToolResult(true, $"Scritto: {path} ({content.Length} caratteri)");
    }
}
```

---

## 4. Edit — Sostituzione stringa in file

**Scopo:** sostituisce una stringa esatta (old_str → new_str). Fallisce se old_str non è univoca o non trovata.

```csharp
public class EditTool : IAgentTool
{
    public string Name => "edit";
    public string Description =>
        "Sostituisce old_str con new_str in un file. old_str deve apparire esattamente una volta. " +
        "Per inserire testo, includi contesto sufficiente in old_str per renderlo univoco.";

    public object InputSchema => new
    {
        type = "object",
        properties = new
        {
            path    = new { type = "string", description = "Percorso del file" },
            old_str = new { type = "string", description = "Stringa da sostituire (deve essere univoca nel file)" },
            new_str = new { type = "string", description = "Stringa sostitutiva" }
        },
        required = new[] { "path", "old_str", "new_str" }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken ct = default)
    {
        var path   = input.GetProperty("path").GetString()!;
        var oldStr = input.GetProperty("old_str").GetString()!;
        var newStr = input.GetProperty("new_str").GetString()!;

        if (!File.Exists(path))
            return new ToolResult(false, "", $"File non trovato: {path}");

        var content = await File.ReadAllTextAsync(path, ct);
        var count   = CountOccurrences(content, oldStr);

        if (count == 0)
            return new ToolResult(false, "", "old_str non trovato nel file");
        if (count > 1)
            return new ToolResult(false, "", $"old_str trovato {count} volte — deve essere univoco");

        var updated = content.Replace(oldStr, newStr, StringComparison.Ordinal);
        await File.WriteAllTextAsync(path, updated, ct);
        return new ToolResult(true, $"Sostituzione eseguita in {path}");
    }

    private static int CountOccurrences(string text, string pattern)
    {
        int count = 0, index = 0;
        while ((index = text.IndexOf(pattern, index, StringComparison.Ordinal)) >= 0)
        { count++; index += pattern.Length; }
        return count;
    }
}
```

---

## 5. Glob — Pattern matching sui file

**Scopo:** trova file che corrispondono a un pattern glob (es. `**/*.cs`).

```csharp
public class GlobTool : IAgentTool
{
    public string Name => "glob";
    public string Description =>
        "Cerca file usando pattern glob (es. **/*.cs, src/**/*.json). " +
        "Restituisce i percorsi ordinati per data di modifica (più recenti prima).";

    public object InputSchema => new
    {
        type = "object",
        properties = new
        {
            pattern = new { type = "string",  description = "Pattern glob (es. **/*.cs)" },
            path    = new { type = "string",  description = "Directory di partenza (default: cwd)" },
            limit   = new { type = "integer", description = "Max risultati (default 100)" }
        },
        required = new[] { "pattern" }
    };

    public Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken ct = default)
    {
        var pattern = input.GetProperty("pattern").GetString()!;
        var basePath = input.TryGetProperty("path", out var p) ? p.GetString()! : Directory.GetCurrentDirectory();
        var limit = input.TryGetProperty("limit", out var l) ? l.GetInt32() : 100;

        if (!Directory.Exists(basePath))
            return Task.FromResult(new ToolResult(false, "", $"Directory non trovata: {basePath}"));

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MatchCasing = MatchCasing.CaseInsensitive,
            IgnoreInaccessible = true
        };

        // .NET 7+ supporta glob nativo in GetFiles; per pattern **/ serve libreria o split manuale
        var files = Directory
            .GetFiles(basePath, pattern.Contains('/') ? "*" : pattern, options)
            .Where(f => MatchGlob(f, basePath, pattern))
            .OrderByDescending(f => File.GetLastWriteTime(f))
            .Take(limit)
            .Select(f => Path.GetRelativePath(basePath, f))
            .ToList();

        if (!files.Any())
            return Task.FromResult(new ToolResult(true, "Nessun file trovato"));

        return Task.FromResult(new ToolResult(true, string.Join('\n', files)));
    }

    // Glob minimale: supporta * e ** (usa Minimatch o DotNet.Glob per pattern complessi)
    private static bool MatchGlob(string fullPath, string basePath, string pattern)
    {
        var relative = Path.GetRelativePath(basePath, fullPath).Replace('\\', '/');
        var regex    = "^" + Regex.Escape(pattern).Replace(@"\*\*", ".*").Replace(@"\*", "[^/]*") + "$";
        return Regex.IsMatch(relative, regex, RegexOptions.IgnoreCase);
    }
}
```

> **Dipendenza consigliata:** [DotNet.Glob](https://github.com/dazinator/DotNet.Glob) per pattern avanzati.

---

## 6. Grep — Ricerca testo con regex

**Scopo:** cerca pattern regex in file, con supporto a file multipli tramite glob.

```csharp
public class GrepTool : IAgentTool
{
    public string Name => "grep";
    public string Description =>
        "Cerca testo usando regex in uno o più file. " +
        "Restituisce percorso, numero di riga e riga corrispondente.";

    public object InputSchema => new
    {
        type = "object",
        properties = new
        {
            pattern     = new { type = "string",  description = "Pattern regex da cercare" },
            path        = new { type = "string",  description = "File o directory in cui cercare" },
            glob        = new { type = "string",  description = "Filtro glob per directory (es. *.cs)" },
            ignore_case = new { type = "boolean", description = "Case insensitive (default false)" },
            context     = new { type = "integer", description = "Righe di contesto prima/dopo (default 0)" }
        },
        required = new[] { "pattern", "path" }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken ct = default)
    {
        var pattern    = input.GetProperty("pattern").GetString()!;
        var path       = input.GetProperty("path").GetString()!;
        var glob       = input.TryGetProperty("glob", out var g)  ? g.GetString() : "*";
        var ignoreCase = input.TryGetProperty("ignore_case", out var ic) && ic.GetBoolean();
        var context    = input.TryGetProperty("context", out var c) ? c.GetInt32() : 0;

        var regexOpts  = ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None;
        var regex      = new Regex(pattern, regexOpts);

        IEnumerable<string> files;
        if (File.Exists(path))
            files = new[] { path };
        else if (Directory.Exists(path))
            files = Directory.GetFiles(path, glob ?? "*", new EnumerationOptions { RecurseSubdirectories = true });
        else
            return new ToolResult(false, "", $"Percorso non trovato: {path}");

        var sb = new StringBuilder();
        int totalMatches = 0;

        foreach (var file in files)
        {
            var lines = await File.ReadAllLinesAsync(file, ct);
            for (int i = 0; i < lines.Length; i++)
            {
                if (!regex.IsMatch(lines[i])) continue;
                totalMatches++;
                var start = Math.Max(0, i - context);
                var end   = Math.Min(lines.Length - 1, i + context);
                for (int j = start; j <= end; j++)
                {
                    var sep = j == i ? ":" : "-";
                    sb.AppendLine($"{Path.GetRelativePath(Directory.GetCurrentDirectory(), file)}:{j + 1}{sep}{lines[j]}");
                }
                if (context > 0) sb.AppendLine("--");
            }
        }

        if (totalMatches == 0)
            return new ToolResult(true, "Nessuna corrispondenza trovata");

        return new ToolResult(true, $"[{totalMatches} match]\n{sb}");
    }
}
```

---

## 7. WebFetch — Fetch contenuto da URL

**Scopo:** scarica il contenuto di un URL e lo restituisce come testo (HTML stripped o raw).

```csharp
public class WebFetchTool : IAgentTool
{
    public string Name => "web_fetch";
    public string Description =>
        "Scarica il contenuto di un URL. Restituisce testo pulito per pagine HTML, " +
        "raw text per JSON/XML/plain. Utile per leggere documentazione o API.";

    public object InputSchema => new
    {
        type = "object",
        properties = new
        {
            url         = new { type = "string",  description = "URL da scaricare" },
            raw         = new { type = "boolean", description = "Restituisce HTML raw invece di testo pulito" },
            max_chars   = new { type = "integer", description = "Limite caratteri output (default 20000)" }
        },
        required = new[] { "url" }
    };

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public async Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken ct = default)
    {
        var url      = input.GetProperty("url").GetString()!;
        var raw      = input.TryGetProperty("raw",       out var r) && r.GetBoolean();
        var maxChars = input.TryGetProperty("max_chars", out var m) ? m.GetInt32() : 20_000;

        try
        {
            var response = await _http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync(ct);
            var mime    = response.Content.Headers.ContentType?.MediaType ?? "";

            if (!raw && mime.Contains("html"))
                content = StripHtml(content);

            if (content.Length > maxChars)
                content = content[..maxChars] + $"\n\n[Troncato a {maxChars} caratteri]";

            return new ToolResult(true, content);
        }
        catch (Exception ex)
        {
            return new ToolResult(false, "", ex.Message);
        }
    }

    private static string StripHtml(string html)
    {
        // Rimuovi script/style
        html = Regex.Replace(html, @"<(script|style)[^>]*>.*?</(script|style)>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        // Rimuovi tag
        html = Regex.Replace(html, @"<[^>]+>", " ");
        // Normalizza spazi
        html = Regex.Replace(html, @"\s{2,}", "\n").Trim();
        return html;
    }
}
```

> **Dipendenza consigliata:** [HtmlAgilityPack](https://html-agility-pack.net/) per uno strip HTML più robusto.

---

## 8. WebSearch — Ricerca sul web

**Scopo:** esegue una ricerca e restituisce i risultati (snippet + URL). Richiede un motore di ricerca via API.

```csharp
public class WebSearchTool : IAgentTool
{
    public string Name => "web_search";
    public string Description =>
        "Cerca informazioni sul web e restituisce titolo, URL e snippet dei risultati. " +
        "Usa per informazioni recenti non presenti nel modello.";

    public object InputSchema => new
    {
        type = "object",
        properties = new
        {
            query   = new { type = "string",  description = "Query di ricerca" },
            results = new { type = "integer", description = "Numero risultati (default 5)" }
        },
        required = new[] { "query" }
    };

    // Provider: Brave Search API (gratuito fino a 2000 query/mese)
    // Alternativa: SerpAPI, DuckDuckGo instant answers
    private readonly string _apiKey;
    private static readonly HttpClient _http = new();

    public WebSearchTool(string braveApiKey) => _apiKey = braveApiKey;

    public async Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken ct = default)
    {
        var query   = input.GetProperty("query").GetString()!;
        var count   = input.TryGetProperty("results", out var r) ? r.GetInt32() : 5;

        var url = $"https://api.search.brave.com/res/v1/web/search?q={Uri.EscapeDataString(query)}&count={count}";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Accept", "application/json");
        request.Headers.Add("X-Subscription-Token", _apiKey);

        try
        {
            var response = await _http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);
            var doc  = JsonDocument.Parse(json);

            var sb = new StringBuilder();
            if (doc.RootElement.TryGetProperty("web", out var web) &&
                web.TryGetProperty("results", out var results))
            {
                foreach (var item in results.EnumerateArray().Take(count))
                {
                    var title   = item.TryGetProperty("title",       out var t) ? t.GetString() : "";
                    var itemUrl = item.TryGetProperty("url",         out var u) ? u.GetString() : "";
                    var snippet = item.TryGetProperty("description", out var d) ? d.GetString() : "";
                    sb.AppendLine($"**{title}**\n{itemUrl}\n{snippet}\n");
                }
            }

            return new ToolResult(true, sb.ToString().Trim());
        }
        catch (Exception ex)
        {
            return new ToolResult(false, "", ex.Message);
        }
    }
}
```

> **Alternative gratuite senza API key:**
> - DuckDuckGo: `https://api.duckduckgo.com/?q=QUERY&format=json` (instant answers, non web full)
> - SearXNG self-hosted: ideale per setup completamente locale

---

## Tool Registry e dispatch

```csharp
public class ToolRegistry
{
    private readonly Dictionary<string, IAgentTool> _tools = new();

    public ToolRegistry Register(IAgentTool tool)
    {
        _tools[tool.Name] = tool;
        return this;
    }

    public IReadOnlyDictionary<string, IAgentTool> All => _tools;

    // Schema da inviare a llama-server nel campo "tools"
    public List<object> GetToolSchemas() => _tools.Values.Select(t => (object)new
    {
        type = "function",
        function = new
        {
            name        = t.Name,
            description = t.Description,
            parameters  = t.InputSchema
        }
    }).ToList();

    public async Task<ToolResult> ExecuteAsync(string name, JsonElement args, CancellationToken ct = default)
    {
        if (!_tools.TryGetValue(name, out var tool))
            return new ToolResult(false, "", $"Tool sconosciuto: {name}");

        return await tool.ExecuteAsync(args, ct);
    }
}
```

### Setup e utilizzo

```csharp
var registry = new ToolRegistry()
    .Register(new BashTool())
    .Register(new ReadTool())
    .Register(new WriteTool())
    .Register(new EditTool())
    .Register(new GlobTool())
    .Register(new GrepTool())
    .Register(new WebFetchTool())
    .Register(new WebSearchTool(braveApiKey: "YOUR_KEY"));

// Nel loop agente, quando llama-server risponde con tool_call:
var toolName = toolCall.GetProperty("name").GetString()!;
var toolArgs = toolCall.GetProperty("arguments");  // già JsonElement
var result   = await registry.ExecuteAsync(toolName, toolArgs, ct);

// Aggiungi alla conversazione come tool result e richiama il modello
```

---

## Dipendenze NuGet

| Pacchetto | Uso | Obbligatorio |
|---|---|---|
| `System.Text.Json` | parsing JSON tool calls | ✅ (incluso in .NET) |
| `DotNet.Glob` | pattern glob avanzati in GlobTool | ⚠️ consigliato |
| `HtmlAgilityPack` | strip HTML robusto in WebFetch | ⚠️ consigliato |

---

## Note di sicurezza

- **BashTool:** valuta un allowlist di comandi se l'agente non è trusted-only
- **WriteTool / EditTool:** considera un path sandbox (`/workspace/...`) per prevenire scritture fuori progetto  
- **WebFetch / WebSearch:** aggiungi rate limiting o cache per evitare abusi
- **Tutti i tool:** loggare input/output per audit trail dell'agente

---

## Gestione del contesto e sessioni

### Come funziona il contesto con llama-server

llama-server è **stateless** — non mantiene nulla tra una chiamata e l'altra. Il "contesto" è semplicemente l'array di messaggi che costruisci in C# e mandi ad ogni richiesta. Questo significa:

- allungare il contesto non costa nulla lato server, ma occupa **VRAM** (KV cache)
- su una RTX 3060 12GB con un 7B Q4 (~4-5 GB per il modello) hai circa 6-7 GB liberi per la KV cache, sufficienti per 16-32k token di contesto
- oltre quella soglia devi scaricare sulla RAM di sistema con perdita drastica di velocità

Con un agente che usa tool, il contesto si riempie rapidamente perché accumula tutta la storia delle chiamate (richiesta → tool call → risultato → risposta → ...).

### Strategia: rollover di sessione

Invece di compattare in-place, la soluzione più pulita è generare un riassunto **prima di raggiungere il limite** e aprire una nuova sessione che parte dal riassunto. Il modello non sa che è un riassunto — lo tratta come contesto normale.

```
sessione 1: [msg1 ... msg50]  →  8000 token  →  soglia 70%  →  genera riassunto
sessione 2: [system + riassunto + msg51]      →  500 token   →  riparte da zero
```

```csharp
public class SessionManager
{
    private readonly int _maxTokens;
    private readonly float _compactThreshold = 0.70f;
    private readonly LlamaClient _llm;

    public async Task<AgentSession> ContinueOrRollover(AgentSession current)
    {
        var tokens = EstimateTokens(current.Messages);

        if (tokens < _maxTokens * _compactThreshold)
            return current; // continua normalmente

        // Genera riassunto prima di toccare il limite
        var summary = await GenerateSummary(current.Messages);

        // Salva la sessione corrente (opzionale, per audit/debug)
        await SaveSession(current);

        // Nuova sessione con riassunto come contesto iniziale
        return new AgentSession
        {
            Id = Guid.NewGuid(),
            PreviousSessionId = current.Id,
            Messages = new List<Message>
            {
                new("system", current.SystemPrompt),
                new("system", $"[SESSIONE PRECEDENTE #{current.Id}]\n{summary}")
                // i nuovi messaggi verranno aggiunti qui
            }
        };
    }

    private async Task<string> GenerateSummary(List<Message> messages)
    {
        return await _llm.CompleteAsync(new[]
        {
            new Message("system",
                "Riassumi la sessione di lavoro dell'agente includendo esplicitamente: " +
                "1) obiettivo originale dell'utente " +
                "2) cosa è già stato fatto e risultati ottenuti " +
                "3) cosa resta da fare " +
                "4) eventuali errori incontrati e come sono stati gestiti"),
            new Message("user", Serialize(messages))
        });
    }

    private int EstimateTokens(List<Message> messages)
    {
        // Stima approssimativa: 1 token ≈ 4 caratteri
        return messages.Sum(m => m.Content.Length / 4);
    }
}

public record AgentSession
{
    public Guid Id { get; init; }
    public Guid? PreviousSessionId { get; init; }  // catena navigabile per debug
    public string SystemPrompt { get; init; } = "";
    public List<Message> Messages { get; init; } = new();
}
```

### Come il modello usa il riassunto

Non serve nulla di speciale — basta includere il riassunto come messaggio `system` all'inizio della nuova sessione. Il modello lo legge come parte normale della conversazione e riparte da lì.

La qualità del riassunto è tutto: deve descrivere **stato corrente** e **prossimi passi**, non solo la cronologia. Per questo il prompt di generazione deve essere esplicito sui 4 punti sopra.

### Vantaggi del rollover rispetto alla compaction in-place

| | Rollover | Compaction in-place |
|---|---|---|
| Contesto dopo operazione | pulito (solo riassunto) | parzialmente compresso |
| Navigabilità storia | catena di sessioni via `PreviousSessionId` | non disponibile |
| Complessità implementativa | bassa | media |
| Rischio perdita contesto | basso (riassunto esplicito) | medio (dipende da quali messaggi tagli) |
