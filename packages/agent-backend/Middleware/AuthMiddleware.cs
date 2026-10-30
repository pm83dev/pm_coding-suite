using Microsoft.AspNetCore.Http;

namespace AgentBackend.Middleware;

/// <summary>
/// Middleware che valida il token JWT/Bearer prima di passare alla pipeline.
/// Se il token è configurato e non corrisponde, restituisce 401.
/// </summary>
public class AuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string _expectedToken;

    public AuthMiddleware(RequestDelegate next, string expectedToken)
    {
        _next = next;
        _expectedToken = expectedToken;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (string.IsNullOrEmpty(_expectedToken))
        {
            await _next(context);
            return;
        }

        var authHeader = context.Request.Headers["Authorization"].ToString();
        var token = authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authHeader.Substring(7)
            : context.Request.Query["token"].ToString();

        if (token != _expectedToken)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Unauthorized" });
            return;
        }

        await _next(context);
    }
}

public static class AuthMiddlewareExtensions
{
    public static IApplicationBuilder UseAuthMiddleware(this IApplicationBuilder app, string token)
    {
        return app.UseMiddleware<AuthMiddleware>(token);
    }
}
