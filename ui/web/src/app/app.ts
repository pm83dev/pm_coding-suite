import { ChangeDetectionStrategy, Component, inject, signal } from "@angular/core";
import { FormsModule } from "@angular/forms";

import { WebSocketProtocolService } from "./protocol/websocket-protocol.service";
import { Settings } from "./settings/settings";

/**
 * App shell migliorata con Bootstrap 5: mostra stato connessione, sessioni,
 * chat, tool calls, approvazioni e impostazioni.
 */
@Component({
  selector: "app-root",
  imports: [Settings, FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: "./app.component.html",
  styleUrls: ["./app.component.css"],
})
export class App {
  protected readonly protocol = inject(WebSocketProtocolService);
  protected readonly draft = signal("");

  constructor() {
    // Avvio esplicito della connessione (il servizio non si connette da solo).
    this.protocol.connect();
  }

  protected newSession(): void {
    // Usa il workspace salvato nelle impostazioni (default: ".")
    const workspace = this.protocol.settings().workspace || ".";
    this.protocol.newSession(workspace);
  }

  protected send(): void {
    const text = this.draft();
    if (!text.trim()) return;
    this.protocol.sendMessage(text);
    this.draft.set("");
  }
}