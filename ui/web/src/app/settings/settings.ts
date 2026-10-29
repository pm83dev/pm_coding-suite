import { Component, inject, signal } from "@angular/core";
import { FormsModule } from "@angular/forms";

import { ProtocolService } from "../protocol/protocol.service";

/**
 * Finestra Impostazioni: URL del server e token, salvati in sessionStorage.
 * Il token non finisce mai nell'URL né nei log.
 */
@Component({
  selector: "app-settings",
  imports: [FormsModule],
  template: `
    <section class="settings" aria-label="Impostazioni di connessione">
      <h2>Impostazioni</h2>
      <label>
        URL server (WebSocket)
        <input type="text" [ngModel]="url()" (ngModelChange)="url.set($event)" placeholder="ws://127.0.0.1:8765" />
      </label>
      <label>
        Token
        <input type="password" [ngModel]="token()" (ngModelChange)="token.set($event)" placeholder="dev-token" />
      </label>
      <div class="actions">
        <button type="button" (click)="save()">Salva e connetti</button>
        <button type="button" (click)="disconnect()">Disconnetti</button>
      </div>
    </section>
  `,
  styles: [`
    .settings { display: flex; flex-direction: column; gap: 0.75rem; padding: 1rem; border: 1px solid var(--border, #ccc); border-radius: 8px; }
    .settings label { display: flex; flex-direction: column; gap: 0.25rem; font-size: 0.9rem; }
    .settings input { padding: 0.4rem 0.5rem; border: 1px solid var(--border, #ccc); border-radius: 4px; }
    .actions { display: flex; gap: 0.5rem; }
    button { padding: 0.4rem 0.8rem; border-radius: 4px; border: 1px solid var(--border, #ccc); background: var(--btn-bg, #f5f5f5); cursor: pointer; }
  `],
})
export class Settings {
  private readonly protocol = inject(ProtocolService);

  protected readonly url = signal(this.protocol.settings().serverUrl);
  protected readonly token = signal(this.protocol.settings().token);

  protected save(): void {
    this.protocol.saveSettings({ serverUrl: this.url().trim(), token: this.token() });
  }

  protected disconnect(): void {
    this.protocol.disconnect();
  }
}
