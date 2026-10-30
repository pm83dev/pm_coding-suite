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

// CORS for frontend
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
        policy.WithOrigins(
            "http://localhost:4200",
            "http://localhost:3005",
            "http://localhost:8080",
            "http://localhost",
            "http://100.121.40.27:8080",
            "http://100.121.40.27:3005",
            "http://100.121.40.27:8765",
            "http://192.168.1.178:8080",
            "https://100.121.40.27:8080",
            "https://100.121.40.27:3005",
            "https://100.121.40.27:8765",
            "https://192.168.1.178:8080",
            "http://127.0.0.1:8765",
            "http://localhost:8765"
        )
              .AllowAnyHeader()
              .AllowAnyMethod());
});

var app = builder.Build();

// Middleware pipeline
app.UseCors("AllowFrontend");
app.UseDefaultFiles();
app.UseStaticFiles();

if (!string.IsNullOrEmpty(config.ServeSettings.Token))
{
    app.Use(async (context, next) =>
    {
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
app.UseWebSockets();
app.MapWhen(
    ctx => ctx.Request.Path == "/ws",
    appBuilder => appBuilder.Run(async ctx =>
    {
        var hub = ctx.RequestServices.GetRequiredService<WebSocketHub>();
        await hub.ProcessWebSocketAsync(ctx, () => Task.CompletedTask);
    })
);

// Start the server
var port = config.ServeSettings.Port;
var bindAddress = config.ServeSettings.BindAddress;
app.Urls.Add($"http://{bindAddress}:{port}");

Console.WriteLine($"=== Agent Backend Starting ===");
Console.WriteLine($"Port: {port}");
Console.WriteLine($"Bind: {bindAddress}");
Console.WriteLine($"Auth: {(string.IsNullOrEmpty(config.ServeSettings.Token) ? "disabled" : "enabled")}");
Console.WriteLine($"LLM: {config.LlmSettings.BaseUrl} / {config.LlmSettings.Model}");
Console.WriteLine("WebSocket: /ws");
Console.WriteLine("==============================");

await app.RunAsync();
