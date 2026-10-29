using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocalCodeAgent.Core;

namespace pm_mcp;

public enum WriteMode { ReadOnly, TwoStep, Direct }

/// <summary>Una proposta di scrittura/eliminazione/spostamento in sospeso, in attesa di apply_edit/discard_edit.</summary>
public sealed record PendingEdit(
    string Token,
    string Kind,              // "write_content" (edit_file/write_file) | "delete_file" | "move_file"
    string RelPath,            // path (write/delete) o source (move)
    string? DestRelPath,       // solo move_file: destination
    string AbsPath,            // path risolto di RelPath
    string? AbsDestPath,       // solo move_file: path risolto di DestRelPath
    string? NewContent,        // solo write_content: contenuto finale già validato dai guard di pm-code
    string ContentHashAtProposal, // hash SHA256 di AbsPath al momento della proposta ("MISSING" se non esisteva)
    DateTimeOffset ExpiresAt);

/// <summary>
/// Gating delle scritture per pm-mcp, PM_MCP_WRITE_MODE=readonly|twostep|direct (default readonly).
/// Si inserisce SOPRA i guard già esistenti in pm-code (sandbox WorkspaceContext, RequireRead,
/// limiti dimensione, blocklist): per edit_file/write_file si aggancia al delegate proposeWrite
/// che pm-code già invoca DOPO aver fatto passare quei guard e calcolato il contenuto finale,
/// quindi non li duplica né li bypassa. move_file/delete_file non hanno un hook equivalente in
/// pm-code (scrivono sempre subito): per questi due pm-mcp intercetta la chiamata prima di
/// invocare ToolDispatcher.Execute, risolve i path con lo stesso WorkspaceContext.Resolve usato
/// dal core (guard di sandbox applicato comunque), e in twostep rimanda l'esecuzione reale
/// (ri-chiamando dispatcher.Execute, stessi guard, cestino incluso) al momento di apply_edit.
/// </summary>
public sealed class WriteGate
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, PendingEdit> _pending = new();

    // Stato dell'ultima chiamata a proposeWrite, letto da Guard() subito dopo dispatcher.Execute.
    // Non pensato per chiamate concorrenti sullo stesso ToolDispatcher — nessun componente del
    // dispatcher esistente lo è (_fileCache, _readPaths sono già stato mutabile non sincronizzato).
    private bool _lastProposeWriteInvoked;
    private string? _lastWriteProposalMessage;

    public WriteMode Mode { get; }

    public WriteGate(string? modeEnv)
    {
        Mode = (modeEnv ?? "readonly").Trim().ToLowerInvariant() switch
        {
            "twostep" => WriteMode.TwoStep,
            "direct" => WriteMode.Direct,
            "readonly" or "" => WriteMode.ReadOnly,
            _ => WriteMode.ReadOnly, // valore non riconosciuto: fallback sicuro, non direct per errore di configurazione
        };
    }

    public static string HashOf(string absPath) =>
        File.Exists(absPath)
            ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(absPath)))
            : "MISSING";

    private void SweepExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var kv in _pending)
            if (kv.Value.ExpiresAt < now) _pending.TryRemove(kv.Key, out _);
    }

    public string RegisterProposal(PendingEdit template)
    {
        SweepExpired();
        var token = Guid.NewGuid().ToString("N");
        _pending[token] = template with { Token = token, ExpiresAt = DateTimeOffset.UtcNow.Add(Ttl) };
        return token;
    }

    private bool Consume(string token, out PendingEdit? pending)
    {
        SweepExpired();
        return _pending.TryRemove(token, out pending);
    }

    public bool Discard(string token) => Consume(token, out _);

    /// <summary>
    /// Diff riga per riga essenziale (prefisso/suffisso comuni tagliati, mostra solo la parte
    /// cambiata). Non è un Myers diff completo, ma per le modifiche localizzate tipiche di
    /// edit_file (e per write_file, dove in pratica tutto il contenuto cambia) è sufficiente
    /// a far capire cosa verrebbe scritto, senza dipendenze NuGet aggiuntive.
    /// </summary>
    public static string UnifiedDiff(string relPath, string oldContent, string newContent)
    {
        var oldLines = oldContent.Length == 0 ? Array.Empty<string>() : oldContent.Replace("\r\n", "\n").Split('\n');
        var newLines = newContent.Length == 0 ? Array.Empty<string>() : newContent.Replace("\r\n", "\n").Split('\n');

        int prefix = 0;
        while (prefix < oldLines.Length && prefix < newLines.Length && oldLines[prefix] == newLines[prefix])
            prefix++;

        int suffix = 0;
        while (suffix < (oldLines.Length - prefix) && suffix < (newLines.Length - prefix)
               && oldLines[^(suffix + 1)] == newLines[^(suffix + 1)])
            suffix++;

        var sb = new StringBuilder();
        sb.AppendLine($"--- {relPath} (attuale)");
        sb.AppendLine($"+++ {relPath} (proposto)");
        if (prefix > 0) sb.AppendLine($"  … {prefix} righe iniziali invariate …");
        for (int i = prefix; i < oldLines.Length - suffix; i++) sb.AppendLine($"- {oldLines[i]}");
        for (int i = prefix; i < newLines.Length - suffix; i++) sb.AppendLine($"+ {newLines[i]}");
        if (suffix > 0) sb.AppendLine($"  … {suffix} righe finali invariate …");
        if (oldLines.Length - prefix - suffix <= 0 && newLines.Length - prefix - suffix <= 0)
            sb.AppendLine("(nessuna differenza di contenuto)");

        return sb.ToString();
    }

    /// <summary>
    /// Delegate da passare come proposeWrite al ToolDispatcher di pm-code. Invocato DOPO i guard
    /// esistenti di FileSystemTools (RequireRead, limiti dimensione, ambiguità old_string, ecc.),
    /// con relPath + contenuto finale già calcolato. Deve SEMPRE ritornare false (mai permettere
    /// a FileSystemTools di scrivere direttamente): in twostep, il token+diff vengono applicati
    /// solo da ApplyEdit; in readonly, il messaggio di errore viene composto da Guard().
    /// </summary>
    public bool ProposeWrite(string relPath, string newContent, WorkspaceContext workspace)
    {
        _lastProposeWriteInvoked = true;
        _lastWriteProposalMessage = null;

        if (Mode == WriteMode.ReadOnly) return false;
        if (Mode == WriteMode.Direct) return true; // non dovrebbe essere agganciato in Direct, ma per sicurezza

        // TwoStep
        var absPath = workspace.Resolve(relPath);
        var oldContent = File.Exists(absPath) ? File.ReadAllText(absPath) : "";
        var hash = HashOf(absPath);
        var diff = UnifiedDiff(relPath, oldContent, newContent);
        var token = RegisterProposal(new PendingEdit("", "write_content", relPath, null, absPath, null, newContent, hash, default));

        _lastWriteProposalMessage =
            $"Proposta di scrittura per {relPath} (NON applicata). Token: {token} (valido 5 minuti)\n\n{diff}\n" +
            $"Usa apply_edit(token=\"{token}\") per applicarla, oppure discard_edit(token=\"{token}\") per scartarla.";

        return false;
    }

    /// <summary>
    /// Instrada edit_file/write_file/delete_file/move_file secondo PM_MCP_WRITE_MODE.
    /// edit_file/write_file passano comunque da dispatcher.Execute (e quindi da proposeWrite,
    /// agganciato sopra, se Mode != Direct); delete_file/move_file sono intercettati PRIMA di
    /// Execute perché pm-code non offre un hook equivalente per loro.
    /// </summary>
    public string Guard(string toolName, string argumentsJson, ToolDispatcher dispatcher, WorkspaceContext workspace)
    {
        if (toolName is "delete_file" or "move_file")
            return Mode == WriteMode.Direct
                ? dispatcher.Execute(toolName, argumentsJson)
                : GuardMoveOrDelete(toolName, argumentsJson, workspace);

        _lastProposeWriteInvoked = false;
        _lastWriteProposalMessage = null;
        var raw = dispatcher.Execute(toolName, argumentsJson);

        if (Mode == WriteMode.Direct) return raw;

        if (!_lastProposeWriteInvoked)
            return raw; // un guard ESISTENTE (RequireRead, dimensione, old_string ambiguo, ecc.) ha già rifiutato prima del gating

        if (Mode == WriteMode.ReadOnly)
            return "ERRORE: scrittura disabilitata (PM_MCP_WRITE_MODE=readonly). " +
                   "Per abilitarla imposta la variabile d'ambiente PM_MCP_WRITE_MODE=twostep (proponi e poi apply_edit) " +
                   "oppure =direct (scrittura immediata, solo su workspace di prova) e riavvia pm-mcp.";

        return _lastWriteProposalMessage ?? raw; // TwoStep
    }

    private string GuardMoveOrDelete(string toolName, string argumentsJson, WorkspaceContext workspace)
    {
        JsonElement el;
        try { el = JsonDocument.Parse(argumentsJson).RootElement; }
        catch (JsonException ex) { return $"ERRORE JSON argomenti: {ex.Message}"; }

        if (toolName == "delete_file")
        {
            var path = el.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";
            string absPath;
            try { absPath = workspace.Resolve(path); }
            catch (UnauthorizedAccessException ex) { return $"ERRORE: {ex.Message}"; }

            if (Mode == WriteMode.ReadOnly)
                return "ERRORE: eliminazione disabilitata (PM_MCP_WRITE_MODE=readonly). " +
                       "Imposta PM_MCP_WRITE_MODE=twostep o =direct e riavvia pm-mcp per abilitarla.";

            if (!File.Exists(absPath) && !Directory.Exists(absPath))
                return $"File non trovato: {path}";

            var hash = HashOf(absPath);
            var token = RegisterProposal(new PendingEdit("", "delete_file", path, null, absPath, null, null, hash, default));
            return $"Proposta di eliminazione per {path} (NON eseguita). Token: {token} (valido 5 minuti)\n" +
                   $"Usa apply_edit(token=\"{token}\") per eseguirla (sposterà il file nel cestino dell'agente, " +
                   $"come fa oggi delete_file), oppure discard_edit(token=\"{token}\") per annullare.";
        }

        // move_file
        var source = el.TryGetProperty("source", out var s) ? s.GetString() ?? "" : "";
        var destination = el.TryGetProperty("destination", out var d) ? d.GetString() ?? "" : "";
        string absSrc, absDst;
        try
        {
            absSrc = workspace.Resolve(source);
            absDst = workspace.Resolve(destination);
        }
        catch (UnauthorizedAccessException ex) { return $"ERRORE: {ex.Message}"; }

        if (Mode == WriteMode.ReadOnly)
            return "ERRORE: spostamento disabilitato (PM_MCP_WRITE_MODE=readonly). " +
                   "Imposta PM_MCP_WRITE_MODE=twostep o =direct e riavvia pm-mcp per abilitarlo.";

        if (!File.Exists(absSrc))
            return $"File sorgente non trovato: {source}";
        if (File.Exists(absDst))
            return $"ERRORE: la destinazione {destination} esiste già.";

        var hashSrc = HashOf(absSrc);
        var token2 = RegisterProposal(new PendingEdit("", "move_file", source, destination, absSrc, absDst, null, hashSrc, default));
        return $"Proposta di spostamento {source} -> {destination} (NON eseguito). Token: {token2} (valido 5 minuti)\n" +
               $"Usa apply_edit(token=\"{token2}\") per eseguirlo, oppure discard_edit(token=\"{token2}\") per annullare.";
    }

    /// <summary>
    /// Applica una proposta in sospeso, solo se il file non è cambiato dal momento della proposta.
    /// Riesegue i guard del dispatcher AL MOMENTO DELL'APPLICAZIONE, non solo a quello della
    /// proposta: per delete_file/move_file passa di nuovo da dispatcher.Execute, che richiama
    /// WorkspaceContext.Resolve e la blocklist di pm-code esattamente come la prima volta (nessuna
    /// strada bypassa quei guard). Per write_content, che scrive direttamente senza passare dal
    /// dispatcher, ri-risolve il path (rileva uno symlink/junction cambiato dopo la proposta) e
    /// ri-verifica il limite di 32000 caratteri prima di scrivere.
    /// </summary>
    public string ApplyEdit(string token, ToolDispatcher dispatcher, WorkspaceContext workspace)
    {
        if (Mode != WriteMode.TwoStep)
            return "ERRORE: apply_edit ha senso solo con PM_MCP_WRITE_MODE=twostep.";

        if (string.IsNullOrWhiteSpace(token) || !Consume(token, out var pending) || pending is null)
            return $"ERRORE: token '{token}' non trovato o scaduto (le proposte durano 5 minuti).";

        switch (pending.Kind)
        {
            case "write_content":
                return ApplyWriteContent(pending, workspace);

            case "delete_file":
            {
                var currentHash = HashOf(pending.AbsPath);
                if (currentHash != pending.ContentHashAtProposal)
                    return HashChangedMessage(pending.RelPath);
                // dispatcher.Execute richiama FileSystemTools.DeleteFile, che risolve di nuovo il
                // path con WorkspaceContext.Resolve: il guard di sandbox è quindi riapplicato qui,
                // non solo alla proposta.
                return dispatcher.Execute("delete_file", JsonSerializer.Serialize(new { path = pending.RelPath }));
            }

            case "move_file":
            {
                var currentHash = HashOf(pending.AbsPath);
                if (currentHash != pending.ContentHashAtProposal)
                    return HashChangedMessage(pending.RelPath);
                if (pending.AbsDestPath != null && File.Exists(pending.AbsDestPath))
                    return $"ERRORE: la destinazione {pending.DestRelPath} esiste già (creata dopo la proposta) — modifica non applicata.";
                // Stesso discorso di delete_file: dispatcher.Execute → MoveFile risolve di nuovo
                // sorgente e destinazione con WorkspaceContext.Resolve.
                return dispatcher.Execute("move_file",
                    JsonSerializer.Serialize(new { source = pending.RelPath, destination = pending.DestRelPath }));
            }

            default:
                return "ERRORE interno: tipo di proposta sconosciuto.";
        }
    }

    private static string HashChangedMessage(string relPath) =>
        $"ERRORE: {relPath} è stato modificato dopo la proposta (hash del contenuto cambiato) — " +
        "la modifica NON è stata applicata per evitare di sovrascrivere cambiamenti nel frattempo. " +
        "Rileggi il file e, se necessario, riproponi la modifica.";

    /// <summary>
    /// Scrive il contenuto proposto solo se il file non è cambiato. A differenza di
    /// delete_file/move_file (che ripassano da dispatcher.Execute), qui non esiste un hook di
    /// pm-code da richiamare: il controllo dell'hash e la scrittura vengono quindi fatti sotto un
    /// unico lock esclusivo (FileShare.None) sullo stesso file, per restringere al minimo la
    /// finestra TOCTOU tra "leggo per calcolare l'hash" e "scrivo" — un altro processo non può
    /// scrivere lo stesso file mentre questo handle è aperto. Non è una garanzia assoluta (un
    /// editor che sovrascrive il file con le proprie semantiche, o un filesystem di rete senza
    /// locking affidabile, può comunque intercalarsi subito prima dell'apertura dell'handle): resta
    /// un rischio residuo, documentato nel README, non eliminabile senza un meccanismo di lock
    /// cross-processo più ampio di quanto serva qui.
    /// </summary>
    private static string ApplyWriteContent(PendingEdit pending, WorkspaceContext workspace)
    {
        string reResolvedPath;
        try { reResolvedPath = workspace.Resolve(pending.RelPath); }
        catch (UnauthorizedAccessException ex) { return $"ERRORE: {ex.Message}"; }

        if (!string.Equals(reResolvedPath, pending.AbsPath, StringComparison.OrdinalIgnoreCase))
            return $"ERRORE: il percorso di {pending.RelPath} risolve ora altrove rispetto alla proposta " +
                   "(possibile symlink/junction cambiato) — modifica non applicata.";

        var content = pending.NewContent ?? "";
        if (content.Length > 32000)
            return $"ERRORE interno: il contenuto proposto per {pending.RelPath} supera il limite di 32000 " +
                   "caratteri — modifica non applicata.";

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(pending.AbsPath)!);

            using var fs = new FileStream(pending.AbsPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var currentBytes = new byte[fs.Length];
            var totalRead = 0;
            while (totalRead < currentBytes.Length)
            {
                var n = fs.Read(currentBytes, totalRead, currentBytes.Length - totalRead);
                if (n == 0) break;
                totalRead += n;
            }
            var currentHash = currentBytes.Length == 0 ? "MISSING" : Convert.ToHexString(SHA256.HashData(currentBytes));
            if (currentHash != pending.ContentHashAtProposal)
                return HashChangedMessage(pending.RelPath);

            var newBytes = new UTF8Encoding(false).GetBytes(content);
            fs.SetLength(0);
            fs.Seek(0, SeekOrigin.Begin);
            fs.Write(newBytes, 0, newBytes.Length);
            fs.Flush();
            return $"✓ Applicato: {pending.RelPath} ({content.Length} caratteri)";
        }
        catch (Exception ex) { return $"ERRORE durante la scrittura di {pending.RelPath}: {ex.Message}"; }
    }

    public string DiscardEditResult(string token)
    {
        if (Mode != WriteMode.TwoStep)
            return "ERRORE: discard_edit ha senso solo con PM_MCP_WRITE_MODE=twostep.";
        return Discard(token)
            ? $"Proposta {token} scartata."
            : $"ERRORE: token '{token}' non trovato o già scaduto/consumato.";
    }
}
