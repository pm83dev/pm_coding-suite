using AgentBackend.Controllers;
using AgentBackend.Hubs;
using AgentBackend.Services;
using LocalCodeAgent.Models;

var builder = WebApplication.CreateBuilder(args);

// Load configuration
var config = new AgentConfig();
builder.Configuration.GetSection("LlmSettings").Bind(config.LlmSettings);
builder.Configuration.GetSection("AgentSettings").Bind(config.AgentSettings);
builder.Configuration.GetSection("WebSearchSettings").Bind(config.WebSearchSettings);
builder.Configuration.GetSection("ServeSettings").Bind(config.ServeSettings);

// Services
builder.Services.AddSingleton(config);
builder.Services.AddSingleton<AgentSessionService>();
builder.Services.AddSingleton<WebSocketHub>();

// Controllers
builder.Services.AddControllers();

// CORS per il frontend in dev (ng serve su :4200) — in produzione il frontend
// è servito dallo stesso backend (same-origin) quindi CORS non è necessario.
// Per WebSocket il browser NON invia l'header Origin, quindi AllowAnyOrigin è
// l'unico modo per non bloccare le connessioni WS cross-origin in dev.
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod());
});

var app = builder.Build();

// Middleware pipeline (ordine importante):
// 1. CORS
// 2. Static files (frontend)
// 3. Auth — blocca /api/* e /ws senza token valido
// 4. Routing + endpoint (API, WebSocket, SPA fallback)
app.UseCors("AllowFrontend");
app.UseDefaultFiles();
app.UseStaticFiles();

if (!string.IsNullOrEmpty(config.ServeSettings.Token))
{
    app.Use(async (context, next) =>
    {
        var path = context.Request.Path.Value ?? "";

        // Esenta gli asset statici (frontend) dall'autenticazione:
        // il browser li carica senza header Authorization.
        if (IsStaticAsset(path))
        {
            await next(context);
            return;
        }

        // /ws: il token è nel payload hello, non nell'header HTTP.
        // L'auth WS è gestita dal WebSocketHub.
        if (path == "/ws")
        {
            await next(context);
            return;
        }

        var authHeader = context.Request.Headers["Authorization"].ToString();
        var token = authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authHeader.Substring(7)
            : context.Request.Query["token"].ToString();
        if (token != config.ServeSettings.Token)
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { error = "Unauthorized" });
            return;
        }
        await next(context);
    });
}

app.UseRouting();
app.MapControllers();

// Endpoint per la selezione workspace dalla UI: restituisce una lista di path candidati
// (recenti usi + driver disponibili + home utente) filtrati per query.
app.MapGet("/api/workspaces", (string? query) =>
{
    var q = (query ?? "").Trim().ToLowerInvariant();
    var ignoreDirs = new[] { ".git", "bin", "obj", "node_modules", ".vs", "__pycache__", ".nuget", "$recycle.bin", "system volume information" };
    var candidates = new List<string>();
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    void Add(string? p)
    {
        if (string.IsNullOrWhiteSpace(p)) return;
        try
        {
            var full = Path.GetFullPath(p);
            if (Directory.Exists(full) && seen.Add(full))
                candidates.Add(full);
        }
        catch { /* ignora path non validi */ }
    }

    // 1) Recenti: .pm-agent/workspaces.json in %USERPROFILE% (se esiste)
    try
    {
        var recentsFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pm-agent", "workspaces.json");
        if (File.Exists(recentsFile))
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(recentsFile));
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    if (el.ValueKind == System.Text.Json.JsonValueKind.String)
                        Add(el.GetString());
                }
            }
        }
    }
    catch { }

    // 2) Workspace correnti: cartelle con .pm-agent nei driver locali (profondità 1-2)
    try
    {
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            try
            {
                var root = drive.RootDirectory;
                foreach (var dir in root.EnumerateDirectories("*", SearchOption.TopDirectoryOnly))
                {
                    if (ignoreDirs.Contains(dir.Name, StringComparer.OrdinalIgnoreCase)) continue;
                    try
                    {
                        if (Directory.Exists(Path.Combine(dir.FullName, ".pm-agent")))
                        {
                            Add(dir.FullName);
                            foreach (var sub in dir.EnumerateDirectories("*", SearchOption.TopDirectoryOnly))
                            {
                                if (ignoreDirs.Contains(sub.Name, StringComparer.OrdinalIgnoreCase)) continue;
                                try
                                {
                                    if (Directory.Exists(Path.Combine(sub.FullName, ".pm-agent")))
                                        Add(sub.FullName);
                                }
                                catch { }
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }
    }
    catch { }

    // 3) Home utente e cartelle comuni
    try
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
        {
            Add(home);
            foreach (var sub in new[] { "Desktop", "Documents", "Projects", "Source", "src", "code", "workspace" })
            {
                try { Add(Path.Combine(home, sub)); } catch { }
            }
        }
    }
    catch { }

    var filtered = string.IsNullOrEmpty(q)
        ? candidates
        : candidates.Where(c => c.ToLowerInvariant().Contains(q)).ToList();

    return Results.Ok(new { workspaces = filtered.Take(50).ToList() });
});

app.UseWebSockets();
app.MapWhen(
    ctx => ctx.Request.Path == "/ws",
    appBuilder => appBuilder.Run(async ctx =>
    {
        var hub = ctx.RequestServices.GetRequiredService<WebSocketHub>();
        await hub.ProcessWebSocketAsync(ctx, () => Task.CompletedTask);
    })
);

// SPA fallback: per ultimo, così le richieste a /api/* e /ws vengono
// gestite dai rispettivi endpoint prima di ricadere su index.html.
app.MapFallbackToFile("index.html");

// Start the server
var port = config.ServeSettings.Port;
var bindAddress = config.ServeSettings.BindAddress;
app.Urls.Add($"http://0.0.0.0:{port}");

Console.WriteLine($"=== Agent Backend Starting ===");
Console.WriteLine($"Port: {port}");
Console.WriteLine($"Bind: {bindAddress}");
Console.WriteLine($"Auth: {(string.IsNullOrEmpty(config.ServeSettings.Token) ? "disabled" : "enabled")}");
Console.WriteLine($"LLM: {config.LlmSettings.BaseUrl} / {config.LlmSettings.Model}");
Console.WriteLine("WebSocket: /ws");
Console.WriteLine("Frontend SPA: / (wwwroot)");
Console.WriteLine("==============================");

await app.RunAsync();

/// <summary>
/// Ritorna true se il path corrisponde a un asset statico del frontend
/// (index.html, JS, CSS, immagini, font, ecc.) — esentato dall'auth.
/// </summary>
static bool IsStaticAsset(string path)
{
    if (string.IsNullOrEmpty(path) || path == "/") return true;
    return path.StartsWith("/main-", StringComparison.Ordinal)
        || path.StartsWith("/styles-", StringComparison.Ordinal)
        || path.StartsWith("/polyfills-", StringComparison.Ordinal)
        || path.StartsWith("/chunk-", StringComparison.Ordinal)
        || path.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".css", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".woff", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".woff2", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".map", StringComparison.OrdinalIgnoreCase);
}
