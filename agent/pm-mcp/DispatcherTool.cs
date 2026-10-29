using System.Text.Json;
using LocalCodeAgent.Core;
using LocalCodeAgent.Models;
using Microsoft.Extensions.AI;

namespace pm_mcp;

/// <summary>
/// Adatta un <see cref="ToolDefinition"/> + <see cref="ToolDispatcher"/> esistenti (da pm-code,
/// invariati) a un tool MCP, senza duplicare nome/descrizione/schema: sono gli stessi oggetti
/// che pm-code passa già all'LLM locale via /v1/chat/completions.
/// </summary>
public sealed class DispatcherTool : AIFunction
{
    private static readonly HashSet<string> WriteTools = new(StringComparer.OrdinalIgnoreCase)
        { "edit_file", "write_file", "delete_file", "move_file" };

    private readonly ToolDispatcher _dispatcher;
    private readonly ToolDefinition _definition;
    private readonly JsonElement _schema;
    private readonly WriteGate? _gate;
    private readonly WorkspaceContext? _workspace;

    public DispatcherTool(ToolDispatcher dispatcher, ToolDefinition definition, WriteGate? gate = null, WorkspaceContext? workspace = null)
    {
        _dispatcher = dispatcher;
        _definition = definition;
        _schema = JsonSerializer.SerializeToElement(definition.Function.Parameters);
        _gate = gate;
        _workspace = workspace;
    }

    public override string Name => _definition.Function.Name;

    public override string Description => _definition.Function.Description;

    public override JsonElement JsonSchema => _schema;

    protected override ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        // Le chiavi arrivano già come JsonElement (deserializzate dalla richiesta JSON-RPC
        // tools/call): serializzare il dizionario produce lo stesso JSON che ToolDispatcher.Execute
        // si aspetta come 'argumentsJson', identico a quanto pm-code riceve da LlamaClient.
        var argsJson = JsonSerializer.Serialize(arguments);
        var name = _definition.Function.Name;

        // I 4 tool di scrittura passano dal WriteGate (PM_MCP_WRITE_MODE); tutti gli altri
        // vanno diretti al dispatcher, invariati.
        var result = (_gate != null && _workspace != null && WriteTools.Contains(name))
            ? _gate.Guard(name, argsJson, _dispatcher, _workspace)
            : _dispatcher.Execute(name, argsJson);
        return ValueTask.FromResult<object?>(result);
    }
}
