import { ChangeDetectionStrategy, Component, inject, signal } from "@angular/core";
import { FormsModule } from "@angular/forms";

import { WebSocketProtocolService } from "./protocol/websocket-protocol.service";
import { Settings } from "./settings/settings";
import { MarkdownPipe } from "./shared/pipes/markdown.pipe";
import { DiffPipe } from "./shared/pipes/diff.pipe";

/**
 * App shell migliorata con Bootstrap 5: mostra stato connessione, sessioni,
 * chat, tool calls, approvazioni e impostazioni.
 */
@Component({
  selector: "app-root",
  imports: [Settings, FormsModule, MarkdownPipe, DiffPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: "./app.component.html",
  styleUrls: ["./app.component.css"],
})
export class App {
  protected readonly protocol = inject(WebSocketProtocolService);
  protected readonly draft = signal("");
  /** Id delle approvazioni accordion espanse. */
  private readonly expandedApprovals = signal<Set<string>>(new Set());

  constructor() {
    // Avvio esplicito della connessione (il servizio non si connette da solo).
    this.protocol.connect();
  }

  protected toggleApproval(approvalId: string): void {
    const current = new Set(this.expandedApprovals());
    if (current.has(approvalId)) {
      current.delete(approvalId);
    } else {
      current.add(approvalId);
    }
    this.expandedApprovals.set(current);
  }

  protected isApprovalExpanded(approvalId: string): boolean {
    return this.expandedApprovals().has(approvalId);
  }

  protected newSession(): void {
    // Usa il workspace salvato nelle impostazioni (default: ".")
    const workspace = this.protocol.settings().workspace || ".";
    this.protocol.newSession(workspace);
  }

  protected deleteSession(sessionId: string, ev: Event): void {
    // Evita il bubbling sul click (che aprirebbe la sessione).
    ev.stopPropagation();
    if (!confirm("Eliminare questa sessione?")) return;
    this.protocol.deleteSession(sessionId);
  }

  protected send(): void {
    const text = this.draft();
    if (!text.trim()) return;
    this.protocol.sendMessage(text);
    this.draft.set("");
  }

  protected onApprove(approvalId: string): void {
    console.log('[UI] Approve clicked, approvalId:', approvalId);
    this.protocol.approve(approvalId);
  }

  protected onReject(approvalId: string): void {
    console.log('[UI] Reject clicked, approvalId:', approvalId);
    this.protocol.reject(approvalId);
  }
}