# @pm-ui/protocol

Tipi TypeScript del **protocollo v1** pm-code (WebSocket, un messaggio JSON per frame) +
validazione runtime con type guard scritte a mano (nessuna dipendenza esterna).

## API

```ts
import { makeMessage, parseClientMessage, parseServerMessage } from "@pm-ui/protocol";

// Costruisce una busta v1 valida (id uuid e ts ISO8601 generati automaticamente)
const msg = makeMessage("send_message", { text: "ciao" }, "sess-1");

// Validazione runtime: restituisce { ok: true, value } oppure { ok: false, error }
const r = parseClientMessage(JSON.parse(rawFrame));
if (!r.ok) console.error(r.error); // errore leggibile in italiano
```

- `parseClientMessage` — valida i **comandi** (client → server): `hello`, `list_sessions`,
  `new_session`, `open_session`, `send_message`, `cancel`, `approve`, `reject`, `set_model`,
  `set_permission`.
- `parseServerMessage` — valida gli **eventi** (server → client): `hello_ok`, `error`,
  `sessions_list`, `session_state`, `models_list`, `permission_state`, `turn_started`,
  `assistant_delta`, `assistant_done`, `tool_call_started`, `tool_call_finished`,
  `approval_request`, `approval_resolved`, `turn_finished`.

Regole di validazione principali:

- busta: `v` deve essere `1` (altrimenti errore "versione non supportata"), `id` stringa non
  vuota, `ts` ISO8601 valido, `sessionId` stringa o null, `type` stringa non vuota,
  `payload` oggetto;
- il tipo deve appartenere alla direzione corretta (un evento server rifiutato da
  `parseClientMessage` e viceversa);
- vincoli specifici per payload, es. `approval_request` con `kind: "write"` richiede
  `path` + `unifiedDiff`, con `kind: "command"` richiede `command`.

## Esempi

[`examples/`](./examples/) contiene un JSON valido per ogni tipo di messaggio (nome file =
valore di `type`). I test unitari verificano che tutti gli esempi passino la validazione.

## Build e test

```bash
npm run build --workspace protocol   # tsc -> dist/
npm test --workspace protocol        # node --test sui test compilati
```
