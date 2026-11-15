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

    /// <summary>
    /// Invocata con la lista PRECEDENTE (completa) quando <see cref="AdoptHistory"/> sostituisce
    /// <see cref="History"/> con una lista diversa (tipicamente dopo un rollover del contesto).
    /// Serve ad archiviare lo storico completo prima che il file di sessione venga sovrascritto
    /// con quello compresso. Best-effort: le eccezioni del callback vengono ignorate.
    /// </summary>
    public Action<List<ChatMessage>>? OnHistoryReplaced { get; set; }

    /// <summary>
    /// Allinea <see cref="History"/> alla lista usata dal loop. <c>SessionManager.MaybeRolloverAsync</c>
    /// restituisce una lista NUOVA (non modifica quella passata): senza questo allineamento il loop
    /// lavora su una lista che la sessione non conosce, e <c>EndTurn</c> salva quella vecchia
    /// perdendo tutti i messaggi assistant/tool del turno. No-op se è già la stessa lista.
    /// </summary>
    public void AdoptHistory(List<ChatMessage> loopHistory)
    {
        List<ChatMessage> previous;
        lock (_lock)
        {
            if (ReferenceEquals(loopHistory, History)) return;
            previous = History;
            History = loopHistory;
        }
        try { OnHistoryReplaced?.Invoke(previous); } catch { /* l'archivio non deve rompere il turno */ }
    }

    private readonly object _lock = new();
    public object Lock => _lock;
    public bool TurnInProgress { get; set; }

    /// <summary>
    /// Id del turno attualmente in corso su QUESTA sessione (null se nessun turno
    /// attivo). Impostato quando il loop emette "turn_started", azzerato su
    /// "turn_finished". Serve a instradare correttamente cancel/log per sessione,
    /// al posto di un unico campo globale condiviso tra tutte le sessioni (che
    /// con più sessioni concorrenti avrebbe mescolato i turnId tra l'una e l'altra).
    /// </summary>
    public string? CurrentTurnId { get; set; }

    /// <summary>
    /// Modalità di permesso della sessione: "readonly" (solo lettura), "ask" (chiede
    /// conferma per modifiche/comandi) o "auto" (agisce senza conferma).
    /// </summary>
    public string PermissionMode { get; set; } = "ask";

    // Approvazione pendente: quando il turno si blocca in attesa di conferma, il
    // TaskCompletionSource viene risolto dal messaggio approve/reject del client.
    private TaskCompletionSource<bool>? _pendingApproval;

    /// <summary>
    /// Dettagli dell'approvazione sulla quale il turno è attualmente in attesa
    /// (null se nessuna). Impostata dal backend quando emette approval_request,
    /// azzerata quando l'approvazione viene risolta. Serve a ri-esporre il bottone
    /// di approvazione al (ri)connessione del client: l'evento approval_request è
    /// un broadcast in tempo reale che, se il browser è disconnesso (refresh),
    /// viene perso per sempre e senza questo campo session_state non potrebbe
    /// ripristinarlo.
    /// </summary>
    public object? PendingApprovalInfo { get; set; }

    // Cancel del turno in corso: quando il client invia "cancel", il token viene
    // cancellato e il loop ReAct lo rispetta (stop tra uno step e l'altro, e
    // durante la chiamata LLM).
    private CancellationTokenSource? _turnCts;

    // Ultimo segno di vita del turno (qualsiasi evento emesso dal loop: delta, tool call, heartbeat
    // di stato…). Lo usa il watchdog per distinguere un turno bloccato da uno lungo ma attivo.
    private long _lastActivityTicks = DateTime.UtcNow.Ticks;

    public DateTime LastActivityUtc => new(Interlocked.Read(ref _lastActivityTicks), DateTimeKind.Utc);

    public void TouchActivity() => Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);

    /// <summary>Token di cancellazione del turno in corso (valido solo se TurnInProgress).</summary>
    public CancellationToken TurnCancellationToken => _turnCts?.Token ?? CancellationToken.None;

    /// <summary>Avvia il tracking del turno: crea il CTS e lo associa alla sessione.</summary>
    public void BeginTurn()
    {
        lock (_lock)
        {
            // Non cancellare il CTS precedente: se un Task del turno vecchio è ancora vivo,
            // la cancellazione forzata potrebbe interferire. Il vecchio CTS verrà pulito
            // dal suo finally quando il Task terminerà.
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
            // Non pulire se c'è un nuovo CTS: significa che è già stato avviato un nuovo turno.
            // Il vecchio Task non deve distruggere il CTS del nuovo turno.
            if (_turnCts != null && !_turnCts.IsCancellationRequested)
            {
                _turnCts.Dispose();
                _turnCts = null;
            }
        }
    }

    public bool HasPendingApproval => _pendingApproval != null;

    /// <summary>Registra l'approvazione su cui il turno è in attesa (null per azzerrarla).</summary>
    public void SetPendingApproval(TaskCompletionSource<bool>? tcs) => _pendingApproval = tcs;

    /// <summary>
    /// Restituisce una copia "scattabile" dell'approvazione pendente corrente
    /// (o null). Serve a costruire il payload session_state senza contendere il
    /// lock della sessione (che lo chiama) e senza che la serializzazione veda un
    /// oggetto mutato a metà.
    /// </summary>
    public object? GetPendingApprovalSnapshot()
    {
        lock (_lock)
            return _pendingApproval != null ? PendingApprovalInfo : null;
    }

    /// <summary>
    /// Blocca il turno in attesa della decisione utente (o del timeout). Restituisce true se
    /// approvato, false se rifiutato/scaduto/cancellato.
    /// </summary>
    public async Task<bool> AwaitApprovalAsync(int timeoutSeconds = 300, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        SetPendingApproval(tcs);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            // Cancellazione del turno o timeout: rifiuta l'approvazione (false).
            using (cts.Token.Register(() => tcs.TrySetResult(false)))
            {
                return await tcs.Task;
            }
        }
        finally
        {
            SetPendingApproval(null);
            PendingApprovalInfo = null;
        }
    }

    /// <summary>Risolve l'approvazione pendente (true = approvato, false = rifiutato).</summary>
    public bool TryResolveApproval(bool approved)
    {
        var tcs = _pendingApproval;
        if (tcs == null) return false;
        var ok = tcs.TrySetResult(approved);
        if (ok)
        {
            lock (_lock)
            {
                _pendingApproval = null;
                PendingApprovalInfo = null;
            }
        }
        return ok;
    }
}
