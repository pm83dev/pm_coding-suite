using System;
using System.Text.Json;

namespace LocalCodeAgent.Core
{
    /// <summary>
    /// Implementazione di <see cref="IAgentSink"/> usata oggi in ENTRAMBE le modalità (REPL e
    /// --stdin-protocol). Riproduce esattamente l'output che il loop scriveva prima di questo
    /// refactor (console colorata su stderr/stdout in REPL via le stesse regole di <c>UI</c>
    /// in Program.cs; righe NDJSON su stdout in --stdin-protocol, stesso formato di
    /// <c>EmitEvent</c> in Program.cs) — nessun cambio di comportamento voluto in questa fase.
    /// </summary>
    public class ConsoleSink : IAgentSink
    {
        private readonly bool _isStdinProtocol;
        private readonly JsonSerializerOptions _eventJsonOpts;

        // Stato per riprodurre, in REPL, il prefisso "Agent › " stampato solo sul primo token di
        // ogni risposta e la riga vuota stampata subito dopo l'ultimo token di quella stessa
        // risposta (comportamento originale: variabile locale "streamedContent" dichiarata da
        // capo a ogni tentativo di chiamata LLM in Program.cs). Irrilevante in --stdin-protocol,
        // dove AssistantDelta/AssistantDone non stampano nulla di equivalente.
        private bool _streaming;

        public ConsoleSink(bool isStdinProtocol)
        {
            _isStdinProtocol = isStdinProtocol;
            _eventJsonOpts = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };
        }

        // Nessun output oggi in nessuna modalità per l'inizio/fine turno: il loop non stampava
        // nulla a questo livello di granularità prima del refactor. Hook pronto per un futuro
        // sink (UI web) che vorrà mostrare l'inizio/fine di un turno.
        public void TurnStarted(string turnId, string sessionId) { }

        public void AssistantDelta(string turnId, string text)
        {
            if (_isStdinProtocol)
            {
                EmitEvent(new { type = "token", text });
                return;
            }

            // Prefisso "Agent › " una sola volta, sul primo frammento di questa risposta
            // (stessa regola della vecchia callback inline in Program.cs/RunAgentLoopAsync).
            if (!_streaming)
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.Write("Agent › ");
                Console.ResetColor();
                _streaming = true;
            }
            Console.Write(text);
        }

        public void AssistantDone(string turnId, int promptTokens, int completionTokens)
        {
            if (_isStdinProtocol) return; // nessun evento per-messaggio emesso oggi

            // Il chiamante decide SE chiamare questo metodo (la vecchia condizione "streamedContent"
            // restava nel loop, non qui): una volta chiamato, stampa sempre la riga vuota di
            // chiusura e resetta il prefisso per la prossima risposta di questo turno.
            Console.WriteLine();
            _streaming = false;
        }

        public void ToolCallStarted(string turnId, string toolName, object? arguments)
        {
            var argsText = arguments as string ?? (arguments is null ? "" : JsonSerializer.Serialize(arguments));

            if (_isStdinProtocol)
            {
                object args;
                try { args = JsonSerializer.Deserialize<JsonElement>(argsText); }
                catch { args = argsText; }
                EmitEvent(new { type = "tool_call", tool = toolName, args });
                return;
            }

            // Riproduce esattamente UI.ToolCall(name, args) di Program.cs.
            Console.WriteLine();
            WriteErr("  ⚙ ", ConsoleColor.Yellow, nl: false);
            WriteErr(toolName, ConsoleColor.Cyan, nl: false);
            WriteErr($"({Truncate(argsText.Replace('\n', ' '), 100)})", ConsoleColor.DarkGray);
        }

        public void ToolCallFinished(string turnId, string toolName, bool success, object? result, double durationMs)
        {
            var resultText = result as string ?? (result is null ? "" : JsonSerializer.Serialize(result));

            if (_isStdinProtocol)
            {
                // La parte [PER L'AGENTE] di manage_todo (id, prossimo passo) serve al modello,
                // che la riceve comunque in history: nell'evento mostrato dall'estensione si
                // vede solo il piano (stessa regola della vecchia onToolResult in Program.cs).
                var visible = resultText;
                if (toolName == "manage_todo")
                {
                    var hintAt = visible.IndexOf(LocalCodeAgent.Tools.TodoTools.AgentHintMarker, StringComparison.Ordinal);
                    if (hintAt >= 0) visible = visible[..hintAt];
                }
                EmitEvent(new { type = "tool_result", tool = toolName, result = visible });
                return;
            }

            // Riproduce esattamente UI.ToolResult(result) di Program.cs.
            var lines = resultText.Split('\n');
            var preview = lines.Length <= 3
                ? resultText.Replace('\n', ' ')
                : string.Join(' ', lines[..3]) + $" …(+{lines.Length - 3} righe)";
            WriteErr($"    → {Truncate(preview, 150)}", ConsoleColor.DarkGray);
        }

        public void TurnFinished(string turnId, string reason) { }

        // UI.Error non distingue tra le due modalità (scrive sempre su stderr): stesso
        // comportamento qui, indipendentemente da _isStdinProtocol.
        public void Error(string message) => WriteErr($"✗ {message}", ConsoleColor.Red);

        public void Notice(string text)
        {
            if (_isStdinProtocol)
                EmitEvent(new { type = "notice", text });
            // In REPL non veniva emesso nulla per i notice (vedi la vecchia Notice() locale in
            // Program.cs, che usciva subito se !isStdinProtocol): stesso comportamento qui.
        }

        private void EmitEvent(object evt)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(evt, _eventJsonOpts));
            Console.Out.Flush();
        }

        // Stessa implementazione privata di UI.Write in Program.cs: tutto il logging/UI passa da
        // stderr, mai da stdout (riservato alle righe NDJSON in --stdin-protocol).
        private static void WriteErr(string msg, ConsoleColor color, bool nl = true)
        {
            Console.ForegroundColor = color;
            if (nl) Console.Error.WriteLine(msg); else Console.Error.Write(msg);
            Console.ResetColor();
        }

        private static string Truncate(string s, int max) =>
            s.Length <= max ? s : s[..max] + "…";
    }
}
