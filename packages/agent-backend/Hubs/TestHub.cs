using Microsoft.AspNetCore.SignalR;

namespace AgentBackend.Hubs;

public class TestHub : Hub
{
    public async Task<string> Ping()
    {
        return "pong";
    }

    public async Task<string> Hello(string name)
    {
        return $"Hello, {name}!";
    }
}
