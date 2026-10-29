import { ChangeDetectionStrategy, Component, inject, signal } from "@angular/core";
import { FormsModule } from "@angular/forms";

import { ProtocolService } from "./protocol/protocol.service";
import { Settings } from "./settings/settings";

/**
 * App shell minimale (passo 3): mostra lo stato di connessione, le sessioni,
 * la sessione corrente e le impostazioni. Le viste chat/timeline/approvazioni
 * arrivano nei passi successivi.
 */
@Component({
  selector: "app-root",
  imports: [Settings, FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="layout">
      <aside class="sidebar">
        <h1>pm-ui</h1>
        <app-settings />
        <section class="sessions" aria-label="Sessioni">
          <h2>Sessioni</h2>
          @if (protocol.sessions().length === 0) {
            <p class="muted">Nessuna sessione.</p>
          } @else {
            <ul>
              @for (s of protocol.sessions(); track s.id) {
                <li [class.active]="s.id === protocol.currentSessionId()">
                  <button type="button" (click)="protocol.openSession(s.id)">
                    {{ s.title }}
                  </button>
                  <span class="status">{{ s.status }}</span>
                </li>
              }
            </ul>
          }
        </section>
      </aside>

      <main class="main">
        <header class="status-bar">
          <span class="conn" [class.ok]="protocol.connection() === 'open'" [class.bad]="protocol.connection() !== 'open'">
            {{ protocol.connection() }}
          </span>
          @if (protocol.error()) {
            <span class="error">{{ protocol.error() }}</span>
          }
        </header>

        <section class="chat" aria-label="Conversazione">
          @if (protocol.currentSessionId()) {
            <h2>Sessione: {{ protocol.currentSessionId() }}</h2>
            <div class="messages">
              @for (m of protocol.messages(); track m.id) {
                <div class="msg" [class.user]="m.role === 'user'" [class.assistant]="m.role === 'assistant'">
                  <span class="role">{{ m.role }}</span>
                  <p>{{ m.text }}</p>
                </div>
              }
            </div>
            @if (protocol.streamingText()) {
              <div class="msg assistant streaming">
                <span class="role">assistant</span>
                <p>{{ protocol.streamingText() }}</p>
              </div>
            }

            <!-- Sezione Approvazioni -->
            @for (appr of protocol.approvals(); track appr.approvalId) {
              @if (!appr.outcome) {
                <div class="approval-card">
                  <div class="approval-header">
                    <strong>Richiesta di Approvazione</strong>
                    <span class="approval-kind">{{ appr.kind }}</span>
                  </div>
                  <div class="approval-body">
                    <p><strong>{{ appr.title }}</strong></p>
                    @if (appr.kind === 'write') {
                      <pre class="diff">{{ appr.unifiedDiff }}</pre>
                    } @else if (appr.kind === 'command') {
                      <code class="command">{{ appr.command }}</code>
                    }
                  </div>
                  <div class="approval-actions">
                    <button type="button" (click)="protocol.approve(appr.approvalId)">Approva</button>
                    <button type="button" (click)="protocol.reject(appr.approvalId)">Rifiuta</button>
                  </div>
                </div>
              }
            }
          } @else {
            <p class="muted">Nessuna sessione aperta. Seleziona una sessione o configura la connessione.</p>
          }
        </section>

        <form class="input-bar" (ngSubmit)="send()">
          <input type="text" name="draft" [ngModel]="draft()" (ngModelChange)="draft.set($event)" placeholder="Scrivi un messaggio (Ctrl+Invio per inviare)" aria-label="Messaggio" />
          <button type="submit" [disabled]="!protocol.currentSessionId()">Invia</button>
          <button type="button" (click)="protocol.cancel()" [disabled]="!protocol.currentTurnId()">Annulla</button>
        </form>
      </main>
    </div>
  `,
  styles: [`
    :host { display: block; height: 100vh; }
    .layout { display: grid; grid-template-columns: 280px 1fr; height: 100%; }
    .sidebar { border-right: 1px solid var(--border, #ccc); padding: 1rem; overflow-y: auto; }
    .main { display: flex; flex-direction: column; height: 100%; }
    .status-bar { display: flex; gap: 1rem; align-items: center; padding: 0.5rem 1rem; border-bottom: 1px solid var(--border, #ccc); }
    .conn.ok { color: green; }
    .conn.bad { color: red; }
    .error { color: red; font-size: 0.85rem; }
    .chat { flex: 1; overflow-y: auto; padding: 1rem; }
    .messages { display: flex; flex-direction: column; gap: 0.5rem; }
    .msg { padding: 0.5rem 0.75rem; border-radius: 8px; max-width: 80%; }
    .msg.user { align-self: flex-end; background: var(--user-bg, #e3f2fd); }
    .msg.assistant { align-self: flex-start; background: var(--assistant-bg, #f5f5f5); }
    .msg.streaming { opacity: 0.8; }
    .role { font-size: 0.7rem; text-transform: uppercase; opacity: 0.6; display: block; margin-bottom: 0.25rem; }
    .input-bar { display: flex; gap: 0.5rem; padding: 0.75rem 1rem; border-top: 1px solid var(--border, #ccc); }
    .input-bar input { flex: 1; padding: 0.5rem; border: 1px solid var(--border, #ccc); border-radius: 4px; }
    .sessions ul { list-style: none; padding: 0; margin: 0; display: flex; flex-direction: column; gap: 0.25rem; }
    .sessions li { display: flex; justify-content: space-between; align-items: center; gap: 0.5rem; }
    .sessions li.active { font-weight: bold; }
    .sessions button { background: none; border: none; cursor: pointer; padding: 0.25rem 0.5rem; border-radius: 4px; }
    .sessions button:hover { background: var(--hover, #eee); }
    .status { font-size: 0.75rem; opacity: 0.6; }
    .muted { opacity: 0.6; font-size: 0.9rem; }

    .approval-card {
      background: #fffbe6;
      border: 2px solid #ffe58f;
      border-radius: 8px;
      padding: 1rem;
      margin-top: 1rem;
      box-shadow: 0 4px 6px rgba(0,0,0,0.1);
    }
    .approval-header {
      display: flex;
      justify-content: space-between;
      align-items: center;
      margin-bottom: 0.5rem;
      color: #856404;
    }
    .approval-kind {
      font-size: 0.7rem;
      text-transform: uppercase;
      background: #ffe58f;
      padding: 2px 6px;
      border-radius: 4px;
      font-weight: bold;
    }
    .approval-body {
      margin-bottom: 1rem;
      font-size: 0.9rem;
    }
    .diff {
      background: #f8f8f8;
      border: 1px solid #ddd;
      padding: 0.5rem;
      border-radius: 4px;
      overflow-x: auto;
      font-family: monospace;
      white-space: pre-wrap;
      font-size: 0.8rem;
    }
    .command {
      display: block;
      background: #333;
      color: #fff;
      padding: 0.5rem;
      border-radius: 4px;
      font-family: monospace;
    }
    .approval-actions {
      display: flex;
      gap: 0.5rem;
      justify-content: flex-end;
    }
    .approval-actions button {
      padding: 0.4rem 0.8rem;
      border-radius: 4px;
      cursor: pointer;
      border: 1px solid #ccc;
    }
    .approval-actions button:first-child {
      background: #52c41a;
      color: white;
      border-color: #52c41a;
    }
    .approval-actions button:last-child {
      background: #ff4d4f;
      color: white;
      border-color: #ff4d4f;
    }
  `],
})
export class App {
  protected readonly protocol = inject(ProtocolService);
  protected readonly draft = signal("");

  constructor() {
    // Avvio esplicito della connessione (il servizio non si connette da solo).
    this.protocol.connect();
  }

  protected send(): void {
    const text = this.draft();
    if (!text.trim()) return;
    this.protocol.sendMessage(text);
    this.draft.set("");
  }
}
