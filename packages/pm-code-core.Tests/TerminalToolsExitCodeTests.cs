using System.Text.Json;
using LocalCodeAgent.Core;
using LocalCodeAgent.Tools;
using Xunit;

namespace LocalCodeAgent.Core.Tests;

/// <summary>
/// Verifica che run_command marchi [ESITO: SUCCESSO]/[ESITO: FALLITO] in base all'exit code
/// REALE del comando interno, non a quello (sempre 0) del wrapper PowerShell/bash che lo esegue
/// — il bug per cui un comando falliva ma veniva marcato come riuscito. Esegue processi reali
/// (powershell.exe/bash), quindi più lento dei test con finto server, ma deterministico.
/// </summary>
public class TerminalToolsExitCodeTests : IDisposable
{
    private readonly string _workspaceDir;
    private readonly TerminalTools _tools;

    public TerminalToolsExitCodeTests()
    {
        _workspaceDir = Path.Combine(Path.GetTempPath(), "pm-code-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspaceDir);
        _tools = new TerminalTools(new WorkspaceContext(_workspaceDir));
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspaceDir, recursive: true); } catch { /* best-effort */ }
    }

    private string RunCommand(string command) =>
        _tools.Execute("run_command", JsonSerializer.Serialize(new { command }));

    [Fact]
    public void ComandoRiuscito_MarcaSuccesso()
    {
        var result = RunCommand("Write-Output 'ok'");
        Assert.Contains("[ESITO: SUCCESSO]", result);
        Assert.DoesNotContain("[ESITO: FALLITO", result);
    }

    [Fact]
    public void ComandoNonEsistente_MarcaFallitoConExitCodeNonZero()
    {
        var result = RunCommand("thiscommanddoesnotexist123abc");
        Assert.Contains("[ESITO: FALLITO", result);
        Assert.DoesNotContain("[ESITO: SUCCESSO]", result);
    }

    [Fact]
    public void ExeConExitCodeNonZero_MarcaFallito()
    {
        // cmd /c exit 7: processo nativo che esce con exit code 7 (non un'eccezione PowerShell,
        // verifica che $LASTEXITCODE diverso da zero venga riconosciuto e non solo le eccezioni).
        var result = RunCommand("cmd /c exit 7");
        Assert.Contains("[ESITO: FALLITO exit code 7]", result);
    }

    [Fact]
    public void ComandoRiuscitoMultiStatement_RestaSuccesso()
    {
        // Regressione: più istruzioni nello stesso comando non devono far scattare un falso FALLITO.
        var result = RunCommand("Write-Output 'a'; Write-Output 'b'");
        Assert.Contains("[ESITO: SUCCESSO]", result);
    }

    [Fact]
    public void BlocklistESicurezza_NonEsegueNulla_RestaComeOggi()
    {
        // Regressione: i comandi bloccati per sicurezza restituiscono un errore testuale
        // SENZA eseguire nulla — non devono passare dal nuovo meccanismo di exit code.
        var result = RunCommand("rm -rf /qualunque");
        Assert.StartsWith("ERRORE: comando bloccato per sicurezza", result);
    }
}
