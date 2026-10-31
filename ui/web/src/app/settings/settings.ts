import { Component, inject, signal, computed } from "@angular/core";
import { FormsModule } from "@angular/forms";

import { ProtocolService } from "../protocol/protocol.service";
import type { ConnectionSettings } from "../protocol/settings";

/**
 * Finestra Impostazioni: URL del server, token di autenticazione e workspace, salvati in localStorage.
 * Il workspace è selezionabile da una combobox con ricerca: il backend fornisce una lista di
 * path candidati (recenti + driver + home) filtrati per query.
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
        Token (opzionale)
        <input type="password" [ngModel]="token()" (ngModelChange)="token.set($event)" placeholder="Token di autenticazione" />
      </label>
      <label class="workspace-label">
        Workspace (cartella)
        <div class="workspace-combo">
          <input
            type="text"
            [ngModel]="workspace()"
            (ngModelChange)="onWorkspaceInput($event)"
            (focus)="onWorkspaceFocus()"
            (blur)="onWorkspaceBlur()"
            placeholder="C:\\projects\\my-app"
            class="workspace-input"
          />
          @if (showDropdown()) {
            <div class="workspace-dropdown">
              @for (w of filteredWorkspaces(); track w) {
                <button type="button" class="workspace-option" (mousedown)="selectWorkspace(w)">
                  <i class="bi bi-folder2 me-1"></i> {{ w }}
                </button>
              } @empty {
                <div class="workspace-empty">Nessun risultato</div>
              }
            </div>
          }
        </div>
      </label>
      <label class="permission-label">
        Modalità permessi
        <select [ngModel]="permissionMode()" (ngModelChange)="onPermissionChange($event)">
          <option value="ask">Chiedi conferma (sicuro)</option>
          <option value="auto">Agisci senza conferma</option>
          <option value="readonly">Solo lettura</option>
        </select>
      </label>
      <div class="permission-hint">
        <i class="bi bi-info-circle"></i>
        {{ permissionHint() }}
      </div>
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
    select { padding: 0.5rem 0.75rem; border: 1px solid #3d424a; border-radius: 6px; background-color: #25282e; color: #fff; font-size: 0.85rem; cursor: pointer; }
    select:focus { outline: none; border-color: #0d6efd; box-shadow: 0 0 0 2px rgba(13,110,253,0.25); }
    .permission-hint { font-size: 0.75rem; color: #8b929b; display: flex; align-items: flex-start; gap: 0.35rem; line-height: 1.3; }
    .permission-hint i { margin-top: 1px; }
    .settings-actions { display: flex; gap: 0.5rem; margin-top: 0.5rem; }
    .btn { font-size: 0.8rem; padding: 0.4rem 0.75rem; }
    .workspace-label { position: relative; }
    .workspace-combo { position: relative; }
    .workspace-input { width: 100%; }
    .workspace-dropdown {
      position: absolute;
      top: 100%;
      left: 0;
      right: 0;
      max-height: 200px;
      overflow-y: auto;
      background: #1e2128;
      border: 1px solid #3d424a;
      border-radius: 6px;
      z-index: 1000;
      box-shadow: 0 4px 12px rgba(0,0,0,0.4);
      margin-top: 2px;
    }
    .workspace-option {
      display: flex;
      align-items: center;
      width: 100%;
      padding: 0.5rem 0.75rem;
      background: none;
      border: none;
      color: #c0c5cb;
      font-size: 0.8rem;
      text-align: left;
      cursor: pointer;
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
    }
    .workspace-option:hover {
      background: #0d6efd;
      color: #fff;
    }
    .workspace-empty {
      padding: 0.5rem 0.75rem;
      color: #6b7280;
      font-size: 0.8rem;
      font-style: italic;
    }
  `],
})
export class Settings {
  private readonly protocol = inject(ProtocolService);

  protected readonly url = signal(this.protocol.settings().serverUrl);
  protected readonly token = signal(this.protocol.settings().token ?? "");
  protected readonly workspace = signal(this.protocol.settings().workspace ?? ".");
  protected readonly permissionMode = signal<"readonly" | "ask" | "auto">(
    this.protocol.settings().permissionMode ?? "ask",
  );

  protected readonly permissionHint = computed(() => {
    switch (this.permissionMode()) {
      case "auto":
        return "L'agente modifica file ed esegue comandi senza chiedere conferma.";
      case "readonly":
        return "L'agente può solo leggere; modifiche e comandi vengono bloccati.";
      case "ask":
      default:
        return "L'agente chiede conferma prima di ogni modifica o comando, mostrando il diff.";
    }
  });

  protected onPermissionChange(mode: "readonly" | "ask" | "auto"): void {
    this.permissionMode.set(mode);
    // Persisti il default e, se c'è una sessione attiva, applicalo subito al backend.
    this.protocol.saveSettings({ permissionMode: mode });
    if (this.protocol.currentSessionId()) {
      this.protocol.setPermission(mode);
    }
  }

  // Combobox state
  private readonly allWorkspaces = signal<string[]>([]);
  private readonly dropdownOpen = signal(false);
  protected readonly showDropdown = this.dropdownOpen;

  protected readonly filteredWorkspaces = computed(() => {
    const query = this.workspace().trim().toLowerCase();
    const all = this.allWorkspaces();
    if (!query) return all.slice(0, 20);
    return all.filter(w => w.toLowerCase().includes(query)).slice(0, 20);
  });

  protected onWorkspaceInput(value: string): void {
    this.workspace.set(value);
    this.dropdownOpen.set(true);
    this.fetchWorkspaces(value);
  }

  protected onWorkspaceFocus(): void {
    this.dropdownOpen.set(true);
    this.fetchWorkspaces(this.workspace());
  }

  protected onWorkspaceBlur(): void {
    setTimeout(() => this.dropdownOpen.set(false), 200);
  }

  protected selectWorkspace(path: string): void {
    this.workspace.set(path);
    this.dropdownOpen.set(false);
  }

  private async fetchWorkspaces(query: string): Promise<void> {
    try {
      const baseUrl = this.url().replace(/^https?:\/\//, "").replace(/\/+$/, "");
      const url = `http://${baseUrl}/api/workspaces?query=${encodeURIComponent(query)}`;
      const res = await fetch(url);
      if (res.ok) {
        const data: { workspaces: string[] } = await res.json();
        this.allWorkspaces.set(data.workspaces ?? []);
      }
    } catch {
      // Backend not reachable
    }
  }

  protected save(): void {
    const s: Partial<ConnectionSettings> = { serverUrl: this.url().trim() };
    const t = this.token().trim();
    if (t) s.token = t;
    const w = this.workspace().trim();
    if (w) s.workspace = w;
    this.protocol.saveSettings(s);
  }

  protected disconnect(): void {
    this.protocol.disconnect();
  }

  protected resetSettings(): void {
    if (confirm("Sei sicuro di voler resettare le impostazioni? URL, token, workspace e permessi verranno eliminati.")) {
      localStorage.removeItem("pm-coding-suite.settings");
      this.url.set("http://127.0.0.1:8765");
      this.token.set("");
      this.workspace.set(".");
      this.permissionMode.set("ask");
      this.allWorkspaces.set([]);
      this.protocol.disconnect();
    }
  }
}