using LocalCodeAgent.Models;

namespace LocalCodeAgent.Tools;

/// <summary>
/// Tool "di uscita": il modello tende a preferire sempre una tool call piuttosto che scrivere
/// direttamente una risposta in testo libero, anche quando il guardrail anti-loop lo invita a
/// farlo. Dargli un'azione esplicita per "fermarsi e parlare" (domanda all'utente o risposta
/// finale su un task di sola analisi) riduce i loop di lettura che altrimenti si concludono con
/// l'interruzione forzata del turno senza nessun riscontro per l'utente.
/// L'esecuzione reale (chiusura immediata del turno) è gestita in AgentLoop, non qui.
/// </summary>
public class AskQuestionTool
{
    public const string ToolName = "ask_question";

    public List<ToolDefinition> Definitions =>
    [
        new() { Function = new() {
            Name = ToolName,
            Description = "Ferma SUBITO il turno e parla con l'utente, invece di continuare a leggere/cercare. Usalo: " +
                           "(1) per fare una domanda precisa quando ti manca un'informazione o una decisione che solo " +
                           "l'utente può dare; (2) per consegnare la risposta finale quando la richiesta era una domanda " +
                           "o una verifica e hai già raccolto abbastanza informazioni con le letture fatte. " +
                           "Non chiamare altri tool dopo questo: il testo in 'message' viene mostrato all'utente e il turno finisce qui.",
            Parameters = new { type = "object",
                properties = new {
                    message = new { type = "string", description = "Testo da mostrare all'utente: la domanda precisa oppure la risposta/analisi finale, con quello che hai scoperto." }
                },
                required = new[] { "message" } }
        }}
    ];

    public string Execute(string argumentsJson) => argumentsJson;
}
