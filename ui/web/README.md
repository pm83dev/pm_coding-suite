# @pm-ui/web — Angular SPA per pm-coding-suite

## Panoramica

Applicazione Angular (v22) che funge da interfaccia web per l'agente pm-coding-suite.
Utilizza **WebSocket standard** (protocollo v1) per la comunicazione real-time con il backend.
I messaggi seguono la busta `{v, id, ts, sessionId, type, payload}` definita in `@pm-ui/protocol`.

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
│   ├── websocket-protocol.service.ts  # Servizio WebSocket (collegamento al backend)
│   ├── protocol.service.ts          # Wrapper backward-compatible
│   ├── state.ts                     # Stato dell'app + reducer puro
│   ├── settings.ts                  # Impostazioni di connessione (localStorage)
│   └── protocol.service.spec.ts     # Test del wrapper
└── settings/
    └── settings.ts                  # Componente Impostazioni (URL + token)
```

## Protocollo WebSocket v1

Il backend espone un endpoint WebSocket su `/ws` con autenticazione via query param `?token=`.
Tutti i messaggi usano la busta v1 definita in `@pm-ui/protocol`:

```json
{ "v": 1, "id": "uuid", "ts": "ISO8601", "sessionId": "..." | null, "type": "...", "payload": {} }
```

### Messaggi client → server (send)
| type | payload | Descrizione |
|------|---------|-------------|
| `hello` | `{ token, clientName, clientVersion }` | Handshake iniziale |
| `list_sessions` | `{}` | Elenco sessioni |
| `new_session` | `{ workspace: string }` | Crea nuova sessione |
| `open_session` | `{ sessionId: string }` | Apri sessione esistente |
| `send_message` | `{ text: string }` | Invia messaggio (richiede sessionId) |
| `cancel` | `{}` | Annulla turno corrente |
| `approve` | `{ approvalId: string }` | Approva action pendente |
| `reject` | `{ approvalId: string }` | Rifiuta action pendente |

### Messaggi server → client (onmessage)
| type | payload | Descrizione |
|------|---------|-------------|
| `hello_ok` | `{ serverVersion, protocol }` | Risposta handshake |
| `error` | `{ code, message }` | Errore |
| `sessions_list` | `{ sessions: SessionInfo[] }` | Elenco sessioni |
| `session_state` | `{ session, messages, model, permission }` | Stato sessione |
| `models_list` | `{ models, active }` | Modelli disponibili |
| `permission_state` | `{ mode, rules }` | Stato permessi |
| `turn_started` | `{ turnId }` | Turno iniziato |
| `assistant_delta` | `{ turnId, messageId, text }` | Frammento streaming |
| `assistant_done` | `{ turnId, messageId, usage }` | Streaming completato |
| `tool_call_started` | `{ turnId, callId, name, args }` | Tool iniziato |
| `tool_call_finished` | `{ turnId, callId, ok, summary, durationMs }` | Tool completato |
| `approval_request` | `{ turnId, approvalId, kind, ... }` | Richiesta approvazione |
| `approval_resolved` | `{ approvalId, outcome }` | Approvazione risolta |
| `turn_finished` | `{ turnId, reason }` | Turno terminato |

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

18 test totali: 14 sul reducer di stato, 2 sul wrapper WebSocket, 2 sul componente root.

## Dipendenze

| Pacchetto | Versione | Scopo |
|-----------|----------|-------|
| `@angular/*` | 22.2.x | Framework UI |
| `@pm-ui/protocol` | file:../protocol | Tipi e utility protocollo |
| `rxjs` | 7.8.1 | Reattivo |

## Note sulla migrazione da SignalR a WebSocket

La migrazione da SignalR a WebSocket standard è trasparente per i componenti UI:
- `WebSocketProtocolService` gestisce la connessione WebSocket e usa `@pm-ui/protocol` per validazione
- `ProtocolService` è un wrapper che delega a `WebSocketProtocolService`
- L'API pubblica di `ProtocolService` è identica alla versione precedente
- I componenti esistenti (`app.component.html`, `settings.ts`) non richiedono modifiche
- Il backend deve esporre l'endpoint WebSocket su `/ws` con il protocollo v1
