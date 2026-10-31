# Refactor UI — Allineamento agent-backend con il protocollo v1

## Problema

Dopo il refactor del loop ReAct in `pm-code-core`, il `WebSocketSink` in
`agent-backend` non era allineato al protocollo v1 che l'UI (Angular) valida a
runtime. Il core `IAgentSink` non trasmette `messageId`/`callId`, ma l'UI li
richiede per `assistant_delta`/`assistant_done`/`tool_call_started`/
`tool_call_finished`. Inoltre il core emette `turn_finished` con reason
`"max_turns"` che l'UI non riconosce, e `Error` senza campo `code`.

## Soluzione (opzione minimale) ✅ Fatto

Riscritto `WebSocketSink` seguendo il pattern già provato di `ServeSink`
(`agent/pm-code/ServeSink.cs`), che:
- Genera `messageId` (un ID per messaggio assistant, reset a `AssistantDone`)
- Genera `callId` (un ID per tool call, start/finish sono sempre sequenziali)
- Mappa `turn_finished` reason: `max_turns` → `completed`
- Mappa `Error` → `{ code: "bad_request", message }` (l'UI valida `code`+`message`)
- `Notice` → no-op (non esiste tipo "notice" nel protocollo v1)
- `Args` → deserializza la stringa JSON in oggetto (l'UI richiede un oggetto)
- `Summary` → prima riga, senza prefissi ERRORE/⚠️, max 200 char

## Cambiamenti applicati

### 1. `packages/agent-backend/Hubs/WebSocketSink.cs` ✅
- Aggiunto stato `_currentMessageId` e `_currentCallId`
- Generazione ID in `AssistantDelta`/`AssistantDone`/`ToolCallStarted`/`ToolCallFinished`
- Mapping `turn_finished` reason (`max_turns` → `completed`)
- Mapping `Error` payload (aggiunto `code`)
- `Notice` → no-op
- `Args` → deserializzazione JSON
- `Summary` → riassunto breve (come `ServeProtocol.Summarize`)

### 2. `packages/agent-backend/Models/Dto/AgentEventDto.cs` ✅
- Aggiunto campo `Code` (JsonPropertyName `"code"`) per `Error`

### 3. Verifica ✅
- Build soluzione completa: **0 errori** (4 warning pre-existing: NU1903 System.Text.Json + CS4014 WebSocketHub)
- Tutti i progetti compilano: `pm-code-core`, `pm-code`, `agent-backend`

## Contratto verificato (backend ↔ UI)

| Evento | Campo UI | Backend | OK? |
|---|---|---|---|
| `turn_started` | `turnId` | ✅ | ✅ |
| `assistant_delta` | `turnId`, `messageId`, `text` | ✅ | ✅ |
| `assistant_done` | `turnId`, `messageId`, `usage{promptTokens,completionTokens}` | ✅ | ✅ |
| `tool_call_started` | `turnId`, `callId`, `name`, `args`(oggetto) | ✅ | ✅ |
| `tool_call_finished` | `turnId`, `callId`, `ok`, `summary`, `durationMs` | ✅ | ✅ |
| `approval_request` | `turnId`, `approvalId`, `kind`, `title`, `path?`, `unifiedDiff?`, `command?`, `expiresAt` | ✅ | ✅ |
| `approval_resolved` | `approvalId`, `outcome` | ✅ | ✅ |
| `turn_finished` | `turnId`, `reason` ∈ enum | ✅ (mappato) | ✅ |
| `error` | `code`, `message` | ✅ (aggiunto `code`) | ✅ |

## Smoke test (da fare a mano)

1. Avviare `agent-backend` (`dotnet run --project packages/agent-backend`)
2. Aprire l'UI web (dev: `ng serve` su :4200, o prod: servita dal backend)
3. Creare una nuova sessione
4. Inviare un messaggio
5. Verificare che:
   - Il testo assistant appare in streaming
   - Le tool call appaiono con nome + args + summary
   - In modalità `ask`: le approvazioni appaiono con diff/command
   - Il turno termina correttamente
6. Verificare il console del browser: nessun errore "Invalid message"

## File coinvolti
- `packages/agent-backend/Hubs/WebSocketSink.cs` (riscritto)
- `packages/agent-backend/Models/Dto/AgentEventDto.cs` (aggiunto campo `Code`)