using System.Text.Json;

namespace LocalCodeAgent.Core;

/// <summary>
/// Helper per parlare il protocollo v1 di <c>ui/protocol</c> (busta v/id/ts/sessionId/type/payload)
/// dal lato server .NET. Non duplica la validazione completa di ui/protocol/src/parse.ts: valida
/// solo la busta comune (uguale per ogni messaggio) — i payload specifici per tipo li valida il
/// chiamante (RunServeModeAsync in Program.cs), che conosce solo i pochi tipi che gestisce in
/// questa fase (hello, new_session, send_message). Il client di prova (ui/serve-test-client.mjs)
/// valida invece OGNI messaggio ricevuto con parseServerMessage di ui/protocol, quello sì completo.
/// </summary>
public static class ServeProtocol
{
    public const int Version = 1;

    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string MakeId() => Guid.NewGuid().ToString("N");

    // "o" (round-trip) produce un offset numerico (+00:00), non "Z": Date.parse lo accetta
    // comunque (ISO8601 valido), ma usiamo il formato con "Z" perché è quello degli esempi in
    // ui/protocol/examples/*.json.
    public static string NowIso() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    /// <summary>Costruisce la busta v1 completa: {v,id,ts,sessionId,type,payload}.</summary>
    public static object Envelope(string type, object payload, string? sessionId) =>
        new { v = Version, id = MakeId(), ts = NowIso(), sessionId, type, payload };

    /// <summary>Un messaggio client grezzo, con la busta comune già deserializzata.</summary>
    public sealed class ClientEnvelope
    {
        public int V { get; set; }
        public string? Id { get; set; }
        public string? Ts { get; set; }
        public string? SessionId { get; set; }
        public string? Type { get; set; }
        public JsonElement Payload { get; set; }
    }

    /// <summary>
    /// Valida la busta comune (stesse regole di parseEnvelope in ui/protocol/src/parse.ts: v==1,
    /// id/type stringhe non vuote, ts ISO8601 valido, payload un oggetto). Restituisce l'envelope
    /// o un messaggio d'errore leggibile (mai un'eccezione: l'input è sempre dati di rete non
    /// fidati).
    /// </summary>
    public static (ClientEnvelope? Envelope, string? Error) ParseEnvelope(string raw)
    {
        ClientEnvelope? env;
        try { env = JsonSerializer.Deserialize<ClientEnvelope>(raw, JsonOpts); }
        catch (JsonException ex) { return (null, $"JSON non valido: {ex.Message}"); }
        catch (NotSupportedException ex) { return (null, $"JSON non valido: {ex.Message}"); }

        if (env == null) return (null, "messaggio non è un oggetto JSON");
        if (env.V != Version) return (null, $"versione protocollo non supportata: v={env.V} (atteso {Version})");
        if (string.IsNullOrEmpty(env.Id)) return (null, "campo 'id' mancante o non una stringa non vuota");
        if (string.IsNullOrEmpty(env.Ts) || !DateTime.TryParse(env.Ts, out _))
            return (null, "campo 'ts' mancante o non una data ISO8601 valida");
        if (string.IsNullOrEmpty(env.Type)) return (null, "campo 'type' mancante o non una stringa non vuota");
        if (env.Payload.ValueKind != JsonValueKind.Object)
            return (null, $"payload del messaggio '{env.Type}' deve essere un oggetto");

        return (env, null);
    }

    /// <summary>
    /// Riassunto breve di un risultato di tool per tool_call_finished.payload.summary (il
    /// protocollo vuole una stringa corta, non l'intero risultato: può essere lunghissimo, es.
    /// l'elenco di un workspace). Rimuove i prefissi "ERRORE"/"⚠️" usati da questo codebase per
    /// segnalare un fallimento al modello: il client lo sa già da `ok`, non serve ripeterlo nel
    /// testo del riassunto.
    /// </summary>
    public static string Summarize(string result, int max = 200)
    {
        var firstLine = result.Split('\n')[0];
        foreach (var prefix in new[] { "ERRORE: ", "ERRORE ", "⚠️ " })
        {
            if (firstLine.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                firstLine = firstLine[prefix.Length..];
                break;
            }
        }
        return firstLine.Length <= max ? firstLine : firstLine[..max] + "…";
    }
}
