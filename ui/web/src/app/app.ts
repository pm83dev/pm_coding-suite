import {
  AfterViewChecked,
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  effect,
  inject,
  signal,
  ViewChild,
} from "@angular/core";
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
export class App implements AfterViewChecked {
  protected readonly protocol = inject(WebSocketProtocolService);
  protected readonly draft = signal("");
  /** Id delle approvazioni accordion espanse (toggle manuale). */
  private readonly expandedApprovals = signal<Set<string>>(new Set());
  /** Id delle approvazioni che l'utente ha esplicitamente chiuso con un click. */
  private readonly collapsedApprovals = signal<Set<string>>(new Set());

  /** Elemento scrollabile della chat (.chat-area). */
  @ViewChild("chatScroll") private chatScroll?: ElementRef<HTMLElement>;

  /**
   * Autoscroll "sticky": se l'utente è in fondo alla chat, lo scroll segue
   * automaticamente i nuovi contenuti (streaming, tool call, messaggi). Se
   * l'utente scorre verso l'alto per rileggere, lo scroll si ferma e non lo
   * forza più in fondo finché non torna in fondo (o preme il bottone "vai in
   * fondo"). Il flag si resettà a true quando cambia sessione.
   */
  private stickToBottom = true;
  /** Distanza dal bordo inferiore (px) sotto la quale si considera "in fondo". */
  private static readonly NEAR_BOTTOM_PX = 80;

  /** Segnale per mostrare il bottone "vai in fondo" quando non siamo in fondo. */
  protected readonly showJumpToBottom = signal(false);

  constructor() {
    // Avvio esplicito della connessione (il servizio non si connette da solo).
    this.protocol.connect();

    // Le approvazioni pendenti devono essere SEMPRE visibili (espansioni automatiche):
    // l'utente potrebbe non sapere di dover cliccare l'header per vedere i bottoni
    // Approva/Rifiuta, e il turno resterebbe in attesa (il backend aspetta 5 min).
    // Un approval pendente non esplicitamente chiuso dall'utente è quindi espanso.
    effect(() => {
      const pending = this.protocol.approvals().filter((a) => !a.outcome);
      for (const a of pending) {
        if (!this.collapsedApprovals().has(a.approvalId) && !this.expandedApprovals().has(a.approvalId)) {
          const current = new Set(this.expandedApprovals());
          current.add(a.approvalId);
          this.expandedApprovals.set(current);
        }
      }
    });

    // Autoscroll: quando cambia qualcosa nella chat (testo in streaming, messaggi,
    // tool call, approvazioni, stato) e l'utente è "incollato" al fondo, porta la
    // vista in fondo. La lettura dei segnali qui crea la dipendenza dell'effect.
    effect(() => {
      // Consumo dei segnali per creare le dipendenze (i valori non servono,
      // contano i mutamenti).
      void this.protocol.streamingText();
      void this.protocol.messages();
      void this.protocol.toolCalls();
      void this.protocol.approvals();
      void this.protocol.statusText();
      void this.protocol.currentTurnId();
      // Se cambia sessione, torna a incollarsi al fondo (nuova conversazione).
      const sid = this.protocol.currentSessionId();
      if (sid !== this._lastSessionId) {
        this._lastSessionId = sid;
        this.stickToBottom = true;
      }
      if (this.stickToBottom) this.scrollToBottom();
    });
  }

  private _lastSessionId: string | null | undefined = undefined;

  /** Chiamato dal template sull'evento scroll di .chat-area. */
  protected onChatScroll(): void {
    const el = this.chatScroll?.nativeElement;
    if (!el) return;
    const distanceFromBottom = el.scrollHeight - el.scrollTop - el.clientHeight;
    const nearBottom = distanceFromBottom <= App.NEAR_BOTTOM_PX;
    if (nearBottom) {
      // L'utente è (tornato) in fondo: riattiva l'autoscroll.
      if (!this.stickToBottom) {
        this.stickToBottom = true;
        this.showJumpToBottom.set(false);
      }
    } else if (this.stickToBottom) {
      // L'utente è risalito: sospendi l'autoscroll e mostra il bottone.
      this.stickToBottom = false;
      this.showJumpToBottom.set(true);
    }
  }

  /** Bottone "vai in fondo": torna in fondo e riattiva l'autoscroll. */
  protected jumpToBottom(): void {
    this.stickToBottom = true;
    this.showJumpToBottom.set(false);
    this.scrollToBottom(true);
  }

  private scrollToBottom(smooth = false): void {
    const el = this.chatScroll?.nativeElement;
    if (!el) return;
    el.scrollTo({ top: el.scrollHeight, behavior: smooth ? "smooth" : "auto" });
  }

  /**
   * Dopo ogni check di change detection (cioè dopo che il DOM ha aggiornato i
   * nuovi contenuti), se siamo incollati al fondo e il contenuto è cresciuto,
   * riporto lo scroll in fondo. Questo copre anche i contenuti che vengono
   * aggiunti asincronamente (markdown renderizzato, immagini, tool call che
   * finiscono) e non solo i mutamenti diretti dei segnali.
   */
  ngAfterViewChecked(): void {
    if (!this.stickToBottom) return;
    const el = this.chatScroll?.nativeElement;
    if (!el) return;
    // Scrollo solo se siamo già (quasi) in fondo, per non contrastare un eventuale
    // scroll manuale in corso.
    const distanceFromBottom = el.scrollHeight - el.scrollTop - el.clientHeight;
    if (distanceFromBottom <= App.NEAR_BOTTOM_PX) {
      el.scrollTop = el.scrollHeight;
    }
  }

  protected toggleApproval(approvalId: string): void {
    const current = new Set(this.expandedApprovals());
    if (current.has(approvalId)) {
      current.delete(approvalId);
      this.expandedApprovals.set(current);
      // Ricorda l'intenzione di chiusura, così l'auto-expand non la riapre.
      const col = new Set(this.collapsedApprovals());
      col.add(approvalId);
      this.collapsedApprovals.set(col);
    } else {
      current.add(approvalId);
      this.expandedApprovals.set(current);
      // Riaperto manualmente: rimuovi l'intenzione di chiusura.
      const col = new Set(this.collapsedApprovals());
      col.delete(approvalId);
      this.collapsedApprovals.set(col);
    }
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