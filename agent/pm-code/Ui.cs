namespace LocalCodeAgent.Cli;

/// <summary>
/// Output formattato per la modalità REPL di pm-code. Tutto il logging/UI passa da
/// stderr, non da stdout: in modalità --stdin-protocol stdout è riservato
/// esclusivamente alle righe NDJSON di EmitEvent — se un banner o un messaggio di log
/// finisse su stdout romperebbe il parsing lato extension. In modalità REPL interattiva
/// questo non cambia nulla di visibile: il terminale mostra comunque stdout e stderr
/// mescolati insieme.
/// </summary>
public static class UI
{
    public static void Header(string title, string sub)
    {
        Write("╔══════════════════════════════════════════════╗", ConsoleColor.DarkYellow);
        Write($"║  {title,-44}║", ConsoleColor.DarkYellow);
        Write($"║  {sub,-44}║", ConsoleColor.DarkGray);
        Write("╚══════════════════════════════════════════════╝", ConsoleColor.DarkYellow);
    }

    public static void Ok(string msg) => Write($"✓ {msg}", ConsoleColor.Green);
    public static void Error(string msg) => Write($"✗ {msg}", ConsoleColor.Red);
    public static void Info(string msg) => Write($"► {msg}", ConsoleColor.Yellow);
    public static void Dim(string msg) => Write(msg, ConsoleColor.DarkGray);
    public static void Prompt()
    {
        Console.ForegroundColor = ConsoleColor.White;
        Console.Error.Write("You › ");
        Console.ResetColor();
    }

    public static void StepIndicator(int step)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Error.Write($"  [{step}] ");
        Console.ResetColor();
    }

    // ToolCall/ToolResult sono migrati in ConsoleSink (vedi IAgentSink.ToolCallStarted/
    // ToolCallFinished): il loop non li chiama più direttamente.

    private static void Write(string msg, ConsoleColor color, bool nl = true)
    {
        Console.ForegroundColor = color;
        if (nl) Console.Error.WriteLine(msg); else Console.Error.Write(msg);
        Console.ResetColor();
    }
}