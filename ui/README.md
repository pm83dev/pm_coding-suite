# pm-ui — Fase 1 (protocol + mock server)

UI web per l'agente di sviluppo `pm-code`. La fase 1 è un client sottile che parla con un
**mock server** WebSocket: tutta la logica dell'agente resta nel core C#.

## Struttura

```
ui/
  protocol/     tipi TypeScript condivisi + validazione runtime + esempi JSON
  mock-server/  server Node + ws che simula l'agente con scenari scriptati
  web/          app Angular (fase successiva)
```

Monorepo npm workspaces.

## Installazione

```bash
cd ui
npm install
```

## Avvio mock server

```bash
# da ui/
npm run start --workspace mock-server
```

Variabili d'ambiente:

| Variabile        | Default      | Descrizione                          |
|------------------|--------------|--------------------------------------|
| `PM_MOCK_PORT`   | `8765`       | porta WebSocket (127.0.0.1)          |
| `PM_MOCK_TOKEN`  | `dev-token`  | token richiesto nel messaggio hello  |

Il server stampa in avvio la porta e il token in uso.

## Test

```bash
# da ui/
npm test            # tutti i workspace (protocol + mock-server)
npm test --workspace protocol
npm test --workspace mock-server
```

I test del mock server avviano il server su una porta effimera e lo pilotano con un client ws.

## Protocollo v1 (riassunto)

WebSocket, un messaggio JSON per frame. Busta comune:

```json
{ "v": 1, "id": "<uuid>", "ts": "<ISO8601>", "sessionId": "<string|null>", "type": "<tipo>", "payload": { } }
```

- **Comandi (client → server):** `hello`, `list_sessions`, `new_session`, `open_session`,
  `send_message`, `cancel`, `approve`, `reject`, `set_model`, `set_permission`
- **Eventi (server → client):** `hello_ok`, `error`, `sessions_list`, `session_state`,
  `models_list`, `permission_state`, `turn_started`, `assistant_delta`, `assistant_done`,
  `tool_call_started`, `tool_call_finished`, `approval_request`, `approval_resolved`,
  `turn_finished`

Dettagli completi in [`protocol/README.md`](./protocol/README.md) e negli esempi in
[`protocol/examples/`](./protocol/examples/).
