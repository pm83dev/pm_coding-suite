# @pm-ui/web — Angular SPA per pm-coding-suite

## Panoramica

Applicazione Angular (v22) che funge da interfaccia web per l'agente pm-coding-suite.
Utilizza **SignalR** per la comunicazione real-time con il backend `agent-backend`
(e non più WebSocket raw come nella versione precedente).

## Architettura

```
app/
├── app.ts                        # Componente root
├── app.component.html            # Template dell'app (chat, sidebar, input)
├── app.component.css             # Stili specifici del componente
├── app.config.ts                 # Configurazione Angular (router)
├── app.routes.ts                 # Route (vuote — single-page)
├── app.spec.ts                   # Test unitari del componente root
├── protocol/
│   ├── signalr-protocol.service.ts  # Servizio SignalR (collegamento al backend)
│   ├── protocol.service.ts          # Wrapper backward-compatible
│   ├── state.ts                     # Stato dell'app + reducer puro
│   ├── settings.ts                  # Impostazioni di connessione (localStorage)
│   └── protocol.service.spec.ts     # Test del wrapper
└── settings/
    └── settings.ts                  # Componente Impostazioni (URL + token)
```

## Protocollo SignalR

Il backend espone un Hub SignalR (`AgentHub`) con i seguenti metodi/eventi:

### Metodi client → server (invoke)
| Metodo | Parametri | Descrizione |
|--------|-----------|-------------|
| `hello` | `clientName`, `clientVersion` | Handshake iniziale |
| `listSessions` | — | Elenco sessioni |
| `newSession` | `{ workspace: string }` | Crea nuova sessione |
| `sendMessage` | `sessionId`, `text` | Invia messaggio |
| `cancel` | `sessionId` | Annulla turno corrente |

### Eventi server → client (on)
| Evento | Payload | Descrizione |
|--------|---------|-------------|
| `HelloResponse` | `{ ok, serverVersion, protocol }` | Risposta handshake |
| `SessionsList` | `SessionInfo[]` | Elenco sessioni |
| `SessionState` | `{ session, messages, model, permission }` | Stato sessione |
| `AgentEvent` | `{ type, payload }` | Evento agente (streaming) |
| `Error` | `{ code, message }` | Errore |

### Mappatura eventi AgentEvent

| type SignalR | type Protocollo |
|--------------|-----------------|
| `turn_started` | `turn_started` |
| `assistant_delta` | `assistant_delta` |
| `assistant_done` | `assistant_done` |
| `tool_call_started` | `tool_call_started` |
| `tool_call_finished` | `tool_call_finished` |
| `turn_finished` | `turn_finished` |

## Configurazione

### Impostazioni di connessione

Salvate in `localStorage` (`pm-coding-suite.settings`):

```json
{
  "serverUrl": "http://127.0.0.1:8765",
  "token": "dev-token",
  "workspace": "."
}
```

### Avvio sviluppo

```bash
cd ui/web
npm start          # ng serve --configuration development
```

L'app si avvia su `http://localhost:4200` e si riconnette automaticamente al backend.

### Build

```bash
npm run build      # ng build --configuration production
```

Output: `dist/web/`

## Test

```bash
npm run test       # ng test --no-watch
```

18 test totali: 14 sul reducer di stato, 2 sul wrapper SignalR, 2 sul componente root.

## Dipendenze

| Pacchetto | Versione | Scopo |
|-----------|----------|-------|
| `@angular/*` | 22.2.x | Framework UI |
| `@microsoft/signalr` | latest | Comunicazione real-time |
| `@pm-ui/protocol` | file:../protocol | Tipi e utility protocollo |
| `rxjs` | 7.8.1 | Reattivo |

## Aggiornamento da WebSocket a SignalR

La migrazione da WebSocket a SignalR è trasparente per i componenti UI:
- `ProtocolService` è un wrapper che delega a `SignalRProtocolService`
- L'API pubblica di `ProtocolService` è identica alla versione precedente
- I componenti esistenti (`app.component.html`, `settings.ts`) non richiedono modifiche
