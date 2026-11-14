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
        // NON ambiguo: PowerShell scrive il messaggio "non riconosciuto" in output, non è vuoto.
        Assert.DoesNotContain(TerminalTools.AmbiguousExitOneMarker, result);
    }

    [Fact]
    public void FindstrSenzaMatch_RestaFallitoMaSegnalatoComeAmbiguo()
    {
        File.WriteAllText(Path.Combine(_workspaceDir, "haystack.txt"), "hello world");
        var result = RunCommand("findstr /c:\"needle-that-does-not-exist\" haystack.txt");

        Assert.Contains("[ESITO: FALLITO exit code 1]", result);
        Assert.Contains(TerminalTools.AmbiguousExitOneMarker, result);
        Assert.Contains("nessuna corrispondenza/nessuna differenza", result);
    }

    [Fact]
    public void GitDiffQuietConDifferenze_SegnalatoComeAmbiguo()
    {
        RunCommand("git init -q");
        RunCommand("git config user.email test@test.local");
        RunCommand("git config user.name test");
        // autocrlf scrive un warning su stderr ad ogni normalizzazione di fine riga: rumore
        // dell'ambiente di test (Windows), non del comando "git diff --quiet" in sé — va
        // disabilitato qui per isolare il caso realistico "nessun output, solo exit code 1".
        RunCommand("git config core.autocrlf false");
        File.WriteAllText(Path.Combine(_workspaceDir, "file.txt"), "riga 1");
        RunCommand("git add file.txt");
        RunCommand("git commit -q -m init");
        File.AppendAllText(Path.Combine(_workspaceDir, "file.txt"), "\nriga 2");

        var result = RunCommand("git diff --quiet");

        Assert.Contains("[ESITO: FALLITO exit code 1]", result);
        Assert.Contains(TerminalTools.AmbiguousExitOneMarker, result);
    }

    [Fact]
    public void DotnetTestFallito_NonAmbiguo()
    {
        var projDir = Path.Combine(_workspaceDir, "testproj");
        Directory.CreateDirectory(projDir);
        File.WriteAllText(Path.Combine(projDir, "Tests.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
                <IsPackable>false</IsPackable>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
                <PackageReference Include="xunit" Version="2.9.2" />
                <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(projDir, "FailingTest.cs"), """
            using Xunit;
            public class FailingTest
            {
                [Fact]
                public void ThisFails() => Assert.True(false, "intentional");
            }
            """);

        var result = RunCommand("dotnet test testproj\\Tests.csproj");

        Assert.Contains("[ESITO: FALLITO exit code 1]", result);
        Assert.DoesNotContain(TerminalTools.AmbiguousExitOneMarker, result);
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
