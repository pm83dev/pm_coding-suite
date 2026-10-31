using LocalCodeAgent.Core;
using LocalCodeAgent.Models;
using AgentBackend.Models.Dto;

namespace AgentBackend.Services;

public class AgentSession
{
    public string SessionId { get; set; } = "";
    public string Title { get; set; } = "";
    public string WorkspaceRoot { get; set; } = "";
    public string Status { get; set; } = "idle";
    public string Model { get; set; } = "";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public LlamaClient? LlmClient { get; set; }
    public WorkspaceContext? Workspace { get; set; }
    public ToolDispatcher? Dispatcher { get; set; }
    public SessionManager? SessionManager { get; set; }
    public List<ChatMessage> History { get; set; } = [];
    private readonly object _lock = new();
    public object Lock => _lock;
    public bool TurnInProgress { get; set; }

    /// <summary>
    /// Modalità di permesso della sessione: "readonly" (solo lettura), "ask" (chiede
    /// conferma per modifiche/comandi) o "auto" (agisce senza conferma).
    /// </summary>
    public string PermissionMode { get; set; } = "ask";

    // Approvazione pendente: quando il turno si blocca in attesa di conferma, il
    // TaskCompletionSource viene risolto dal messaggio approve/reject del client.
    private TaskCompletionSource<bool>? _pendingApproval;

    // Cancel del turno in corso: quando il client invia "cancel", il token viene
    // cancellato e il loop ReAct lo rispetta (stop tra uno step e l'altro, e
    // durante la chiamata LLM).
    private CancellationTokenSource? _turnCts;

    /// <summary>Token di cancellazione del turno in corso (valido solo se TurnInProgress).</summary>
    public CancellationToken TurnCancellationToken => _turnCts?.Token ?? CancellationToken.None;

    /// <summary>Avvia il tracking del turno: crea il CTS e lo associa alla sessione.</summary>
    public void BeginTurn()
    {
        lock (_lock)
        {
            _turnCts?.Cancel();
            _turnCts?.Dispose();
            _turnCts = new CancellationTokenSource();
        }
    }

    /// <summary>
    /// CANCELLA il turno in corso. Restituisce true se c'era un turno da cancellare.
    /// Il loop ReAct controlla questo token tra uno step e l'altro.
    /// </summary>
    public bool CancelTurn()
    {
        lock (_lock)
        {
            if (_turnCts == null) return false;
            if (!_turnCts.IsCancellationRequested)
                _turnCts.Cancel();
            return true;
        }
    }

    /// <summary>Pulisce il CTS del turno (da chiamare a fine turno, anche in caso di errore).</summary>
    public void EndTurnCts()
    {
        lock (_lock)
        {
            _turnCts?.Dispose();
            _turnCts = null;
        }
    }

    public bool HasPendingApproval => _pendingApproval != null;

    /// <summary>Registra l'approvazione su cui il turno è in attesa (null per azzerrarla).</summary>
    public void SetPendingApproval(TaskCompletionSource<bool>? tcs) => _pendingApproval = tcs;

    /// <summary>
    /// Blocca il turno in attesa della decisione utente (o del timeout). Restituisce true se
    /// approvato, false se rifiutato/scaduto.
    /// </summary>
    public async Task<bool> AwaitApprovalAsync(int timeoutSeconds = 300)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        SetPendingApproval(tcs);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            using (cts.Token.Register(() => tcs.TrySetResult(false)))
            {
                return await tcs.Task;
            }
        }
        finally
        {
            SetPendingApproval(null);
        }
    }

    /// <summary>Risolve l'approvazione pendente (true = approvato, false = rifiutato).</summary>
    public bool TryResolveApproval(bool approved)
    {
        var tcs = _pendingApproval;
        return tcs != null && tcs.TrySetResult(approved);
    }
}
