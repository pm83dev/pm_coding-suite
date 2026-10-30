using LocalCodeAgent.Models;
using AgentBackend.Services;
using AgentBackend.Hubs;
using AgentBackend.Controllers;

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

// SignalR
builder.Services.AddSignalR();

// Controllers
builder.Services.AddControllers();

// CORS for frontend
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
        policy.WithOrigins("http://localhost:4200")
              .AllowAnyHeader()
              .AllowAnyMethod());
});

var app = builder.Build();

// Middleware pipeline
app.UseCors("AllowFrontend");

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
app.MapHub<AgentHub>("/agent-hub");

// Start the server
var port = config.ServeSettings.Port;
var bindAddress = config.ServeSettings.BindAddress;
app.Urls.Add($"http://{bindAddress}:{port}");

Console.WriteLine($"=== Agent Backend Starting ===");
Console.WriteLine($"Port: {port}");
Console.WriteLine($"Bind: {bindAddress}");
Console.WriteLine($"Auth: {(string.IsNullOrEmpty(config.ServeSettings.Token) ? "disabled" : "enabled")}");
Console.WriteLine($"LLM: {config.LlmSettings.BaseUrl} / {config.LlmSettings.Model}");
Console.WriteLine("==============================");

await app.RunAsync();
