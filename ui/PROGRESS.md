# PROGRESS — Fase 1 UI (protocol + mock server)

## Checklist passi

- [x] **Passo 0 — Scaffold monorepo `ui/`**
  - npm workspaces (`protocol`, `mock-server`), `tsconfig.base.json` (NodeNext, strict).
  - Ambiente verificato: node v22.23.0, npm 10.9.8, Angular CLI 22.0.4 globale (non ancora usato: serve al passo web).
- [x] **Passo 1 — `ui/protocol`** (build + 7/7 test ok)
  - [x] Tipi TS busta comune + union discriminata su `type` + `makeMessage` (`src/types.ts`, `src/make-message.ts`)
  - [x] `parseServerMessage` / `parseClientMessage` (type guard manuali, errori leggibili in italiano) — `src/parse.ts`
  - [x] `examples/*.json`: 24 file, uno per ogni tipo di messaggio (nome file = valore di `type`)
  - [x] Test unitari (`src/tests/parse.test.ts`): tutti gli esempi validano; v=2, senza type, payload errato, direzione sbagliata e approval_request incompleto rifiutati
- [x] **Passo 2 — `ui/mock-server`**
  - [x] Server ws (127.0.0.1, `PM_MOCK_PORT` default 8765, token `PM_MOCK_TOKEN` default "dev-token")
  - [x] hello obbligatorio con token valido; rifiuto versione != 1
  - [x] 2 sessioni di esempio in memoria; list/new/open_session (riemissione storico)
  - [x] Scenari send_message: leggi / modifica (approval write + diff) / comando (approval command) / errore / default streaming
  - [x] Permessi readonly/ask/auto, set_model (2-3 modelli), cancel, expiry approvazioni
  - [x] Test automatici con client ws
- [x] **Passo 3 — `ui/web` (Angular)**
  - [x] Componenti Standalone e gestione stato con Signals (verificato)
  - [x] Integrazione ProtocolService con `@pm-ui/protocol` (verificato)
  - [x] UI base per sessioni e chat implementata (verificato)
  - [ ] UI per gestione approvazioni (da implementare)

## Note

- `docs/architettura-ui.md` non esiste nel workspace: seguo la specifica della richiesta.
- Nessuna libreria di validazione installata: uso type guard scritte a mano (come previsto dalla specifica).