import { Component, inject, signal } from "@angular/core";
import { FormsModule } from "@angular/forms";

import { ProtocolService } from "../protocol/protocol.service";

/**
 * Finestra Impostazioni: URL del server e token, salvati in localStorage.
 */
@Component({
  selector: "app-settings",
  imports: [FormsModule],
  template: `
    <section class="settings-form">
      <h2 style="font-size: 0.8rem; text-transform: uppercase; letter-spacing: 0.05em; color: #8b929b; margin-bottom: 0.75rem; font-weight: 600;">
        <i class="bi bi-gear"></i> Impostazioni
      </h2>
      <label>
        URL server (HTTP)
        <input type="text" [ngModel]="url()" (ngModelChange)="url.set($event)" placeholder="http://127.0.0.1:8765" />
      </label>
      <label>
        Token
        <input type="password" [ngModel]="token()" (ngModelChange)="token.set($event)" placeholder="dev-token" />
      </label>
      <div class="settings-actions">
        <button type="button" class="btn btn-primary btn-sm" (click)="save()">
          <i class="bi bi-check-lg"></i> Salva e connetti
        </button>
        <button type="button" class="btn btn-outline-secondary btn-sm" (click)="disconnect()">
          <i class="bi bi-x-lg"></i> Disconnetti
        </button>
      </div>
      <div class="settings-actions mt-2">
        <button type="button" class="btn btn-outline-danger btn-sm" (click)="resetSettings()">
          <i class="bi bi-arrow-counterclockwise"></i> Reset impostazioni
        </button>
      </div>
    </section>
  `,
  styles: [`
    section { display: flex; flex-direction: column; gap: 0.75rem; }
    label { display: flex; flex-direction: column; gap: 0.3rem; font-size: 0.85rem; color: #c0c5cb; }
    input { padding: 0.5rem 0.75rem; border: 1px solid #3d424a; border-radius: 6px; background-color: #25282e; color: #fff; font-size: 0.85rem; }
    input:focus { outline: none; border-color: #0d6efd; box-shadow: 0 0 0 2px rgba(13,110,253,0.25); }
    input::placeholder { color: #6b7280; }
    .settings-actions { display: flex; gap: 0.5rem; margin-top: 0.5rem; }
    .btn { font-size: 0.8rem; padding: 0.4rem 0.75rem; }
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

  protected resetSettings(): void {
    if (confirm("Sei sicuro di voler resettare le impostazioni? URL e token verranno eliminati.")) {
      localStorage.removeItem("pm-coding-suite.settings");
      this.url.set("http://127.0.0.1:8765");
      this.token.set("");
      this.protocol.disconnect();
    }
  }
}