using System.Text.Json;
using LocalCodeAgent.Core;
using Microsoft.Extensions.AI;

namespace pm_mcp;

/// <summary>Applica una proposta in sospeso creata da edit_file/write_file/delete_file/move_file in modalità twostep.</summary>
public sealed class ApplyEditTool : AIFunction
{
    private readonly WriteGate _gate;
    private readonly ToolDispatcher _dispatcher;
    private readonly WorkspaceContext _workspace;
    private readonly JsonElement _schema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            token = new { type = "string", description = "Token restituito da una proposta in sospeso (edit_file/write_file/delete_file/move_file in modalità twostep)." }
        },
        required = new[] { "token" }
    });

    public ApplyEditTool(WriteGate gate, ToolDispatcher dispatcher, WorkspaceContext workspace)
    {
        _gate = gate;
        _dispatcher = dispatcher;
        _workspace = workspace;
    }

    public override string Name => "apply_edit";

    public override string Description =>
        "Applica una proposta di scrittura/eliminazione/spostamento in sospeso (PM_MCP_WRITE_MODE=twostep), " +
        "identificata dal token restituito da edit_file/write_file/delete_file/move_file. Rifiuta se il file è " +
        "cambiato dopo la proposta o se il token è scaduto (5 minuti).";

    public override JsonElement JsonSchema => _schema;

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var token = ReadToken(arguments);
        return ValueTask.FromResult<object?>(_gate.ApplyEdit(token, _dispatcher, _workspace));
    }

    internal static string ReadToken(AIFunctionArguments arguments) =>
        arguments.TryGetValue("token", out var t)
            ? (t is JsonElement je ? je.GetString() : t?.ToString()) ?? ""
            : "";
}

/// <summary>Scarta (senza applicare) una proposta in sospeso creata in modalità twostep.</summary>
public sealed class DiscardEditTool : AIFunction
{
    private readonly WriteGate _gate;
    private readonly JsonElement _schema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            token = new { type = "string", description = "Token da scartare senza applicarlo." }
        },
        required = new[] { "token" }
    });

    public DiscardEditTool(WriteGate gate)
    {
        _gate = gate;
    }

    public override string Name => "discard_edit";

    public override string Description =>
        "Scarta una proposta di scrittura/eliminazione/spostamento in sospeso (PM_MCP_WRITE_MODE=twostep) senza applicarla.";

    public override JsonElement JsonSchema => _schema;

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var token = ApplyEditTool.ReadToken(arguments);
        return ValueTask.FromResult<object?>(_gate.DiscardEditResult(token));
    }
}
