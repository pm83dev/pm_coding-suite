using System.Runtime.InteropServices;

/// <summary>
/// Garantisce una finestra console anche quando il processo parte senza terminale
/// (debugger VS Code in modalità internalConsole, doppio-click, Task Scheduler, ecc.).
/// Funziona solo su Windows; su altri OS è no-op.
/// </summary>
internal static class ConsoleHelper
{
    [DllImport("kernel32.dll")]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    private const int ATTACH_PARENT_PROCESS = -1;

    public static void EnsureConsole()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        // Prova ad agganciarsi al terminale del processo padre (es. cmd, PowerShell).
        // Se fallisce (nessun padre con console), alloca una nuova finestra console.
        if (!AttachConsole(ATTACH_PARENT_PROCESS))
            AllocConsole();
    }
}
