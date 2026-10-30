# agent-backend

Servizio backend ASP.NET Core 8.0 con SignalR per l'agente AI pm-coding-suite.

## Architettura

```
agent-backend/
├── Controllers/
│   └── SessionsController.cs    # REST API per CRUD sessioni + config
├── Hubs/
│   └── AgentHub.cs              # Hub SignalR per comunicazione real-time
├── Middleware/
│   └── AuthMiddleware.cs        # Validazione token API
├── Models/Dto/
│   ├── AgentEventDto.cs         # Eventi live (delta, tool_calls, turn_*)
│   └── SessionDto.cs            # DTO per sessioni, config, messaggi
├── Services/
│   ├── AgentSession.cs          # Rappresenta una sessione agente attiva
│   ├── AgentSessionService.cs   # Ciclo di vita sessioni (CRUD, turni)
│   └── ToolExecutionService.cs  # Esecuzione turno (LLM → tool → response)
├── Program.cs                   # Entry point, configurazione, middleware
├── appsettings.json             # Configurazione LLM, Agent, WebSearch, Serve
└── agent-backend.csproj         # Progetto .NET 8
```

## Protocollo SignalR

L'hub `AgentHub` espone questi metodi client-callable:

| Metodo | Descrizione |
|--------|-------------|
| `Hello(clientName, clientVersion)` | Handshake iniziale |
| `ListSessions()` | Lista tutte le sessioni |
| `NewSession({ workspace, model? })` | Crea nuova sessione |
| `SendMessage(sessionId, text)` | Invia messaggio → esecuzione agente |
| `Cancel(sessionId)` | Annulla turno in corso |

Eventi server → client:

| Type | Descrizione |
|------|-------------|
| `hello_ok` | Conferma handshake |
| `sessions_list` | Lista sessioni |
| `session_state` | Stato sessione (sessione crezata/aperta) |
| `turn_started` | Inizio turno agente |
| `assistant_delta` | Frammento testo dall'LLM (streaming) |
| `tool_call_started` | Tool call avviata |
| `tool_call_finished` | Tool call completata |
| `assistant_done` | Risposta LLM completata + usage |
| `turn_finished` | Turno completato/errore/cancellato |
| `error` | Errore |

## REST API

| Metodo | Endpoint | Descrizione |
|--------|----------|-------------|
| GET | `/api/sessions` | Lista sessioni |
| GET | `/api/sessions/{id}` | Dettagli sessione |
| DELETE | `/api/sessions/{id}` | Elimina sessione |
| GET | `/api/sessions/config` | Configurazione corrente |

## Configurazione

`appsettings.json`:

```json
{
  "LlmSettings": {
    "BaseUrl": "http://localhost:9000",
    "Model": "PM LLM",
    "Temperature": 0.0,
    "FrequencyPenalty": 0.3
  },
  "AgentSettings": {
    "MaxSteps": 30,
    "MaxContextTokens": 81920,
    "RolloverTokens": 40000,
    "MaxLinesPerFile": 400
  },
  "WebSearchSettings": {
    "BraveApiKey": ""
  },
  "ServeSettings": {
    "Port": 8765,
    "BindAddress": "127.0.0.1",
    "Token": ""
  }
}
```

## Avvio

```bash
dotnet run --project packages/agent-backend/agent-backend.csproj
```

Il servizio si avvia sull'indirizzo e porta configurati in `ServeSettings`.

## Dipendenze

- **pm-code-core**: Libreria core con LlamaClient, ToolDispatcher, SessionManager, WorkspaceContext
- **Microsoft.AspNetCore.SignalR**: Comunicazione real-time bidirezionale
- **Swashbuckle.AspNetCore**: Documentazione API (Swagger)
