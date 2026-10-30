import { Injectable, inject, OnDestroy } from "@angular/core";

import { SignalRProtocolService } from "./signalr-protocol.service";
import type { ProtocolState } from "./state";
import type { ServerMessage } from "@pm-ui/protocol";

/**
 * Wrapper backward-compatible che delega a SignalRProtocolService.
 * Mantiene la stessa API di ProtocolService per compatibilità con app.ts.
 */
@Injectable({ providedIn: "root" })
export class ProtocolService implements OnDestroy {
  private readonly signalr = inject(SignalRProtocolService);

  readonly protocolState = this.signalr.protocolState;
  readonly connection = this.signalr.connection;
  readonly error = this.signalr.error;
  readonly sessions = this.signalr.sessions;
  readonly currentSessionId = this.signalr.currentSessionId;
  readonly messages = this.signalr.messages;
  readonly models = this.signalr.models;
  readonly activeModelId = this.signalr.activeModelId;
  readonly permissionMode = this.signalr.permissionMode;
  readonly currentTurnId = this.signalr.currentTurnId;
  readonly streamingText = this.signalr.streamingText;
  readonly toolCalls = this.signalr.toolCalls;
  readonly approvals = this.signalr.approvals;
  readonly lastUsage = this.signalr.lastUsage;
  readonly settings = this.signalr.settings;
  readonly serverUrl = this.signalr.serverUrl;

  connect(): void { this.signalr.connect(); }
  saveSettings(s: Parameters<SignalRProtocolService["saveSettings"]>[0]): void { this.signalr.saveSettings(s); }
  disconnect(): void { this.signalr.disconnect(); }
  listSessions(): void { this.signalr.listSessions(); }
  newSession(workspace: string): void { this.signalr.newSession(workspace); }
  openSession(sessionId: string): void { this.signalr.openSession(sessionId); }
  sendMessage(text: string): void { this.signalr.sendMessage(text); }
  cancel(): void { this.signalr.cancel(); }
  approve(approvalId: string): void { this.signalr.approve(approvalId); }
  reject(approvalId: string): void { this.signalr.reject(approvalId); }

  _setStateForTest(st: ProtocolState): void { this.signalr._setStateForTest(st); }
  _applyForTest(msg: ServerMessage): void { this.signalr._applyForTest(msg); }
  _handleRawForTest(raw: string): void { this.signalr._handleRawForTest(raw); }

  ngOnDestroy(): void { this.signalr.ngOnDestroy(); }
}
