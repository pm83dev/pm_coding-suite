using System.Runtime.InteropServices;

/// <summary>
/// Public ConsoleHelper for pm-code. The version in pm-code-core is internal;
/// this public copy lets Program.cs call ConsoleHelper.EnsureConsole().
/// </summary>
public static class ConsoleHelper
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

        if (!AttachConsole(ATTACH_PARENT_PROCESS))
            AllocConsole();
    }
}
