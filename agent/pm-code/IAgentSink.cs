using System;
using System.Threading.Tasks;

namespace LocalCodeAgent.Core
{
    /// <summary>
    /// Unico punto attraverso cui il loop (Program.cs: RunAgentLoopAsync) riporta all'esterno
    /// testo in streaming, tool call e fine turno — sia in modalità REPL sia in --stdin-protocol.
    /// <see cref="ConsoleSink"/> è l'unica implementazione oggi e riproduce ESATTAMENTE l'output
    /// di entrambe le modalità così com'era prima dell'introduzione di questa interfaccia (nessun
    /// cambio di comportamento: refactor puro, in preparazione a un futuro sink per la UI web).
    /// </summary>
    public interface IAgentSink
    {
        void TurnStarted(string turnId, string sessionId);
        void AssistantDelta(string turnId, string text);
        void AssistantDone(string turnId, int promptTokens, int completionTokens);
        void ToolCallStarted(string turnId, string toolName, object? arguments);
        void ToolCallFinished(string turnId, string toolName, bool success, object? result, double durationMs);
        void TurnFinished(string turnId, string reason);
        void Error(string message);
        void Notice(string text);
    }
}
