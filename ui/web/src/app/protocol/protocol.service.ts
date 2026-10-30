import { Injectable, inject, OnDestroy } from "@angular/core";

import { WebSocketProtocolService } from "./websocket-protocol.service";
import type { ProtocolState } from "./state";
import type { ServerMessage } from "@pm-ui/protocol";

/**
 * Wrapper backward-compatible che delega a WebSocketProtocolService.
 * Mantiene la stessa API di ProtocolService per compatibilità con app.ts.
 */
@Injectable({ providedIn: "root" })
export class ProtocolService implements OnDestroy {
  private readonly websocket = inject(WebSocketProtocolService);

  readonly protocolState = this.websocket.protocolState;
  readonly connection = this.websocket.connection;
  readonly error = this.websocket.error;
  readonly sessions = this.websocket.sessions;
  readonly currentSessionId = this.websocket.currentSessionId;
  readonly messages = this.websocket.messages;
  readonly models = this.websocket.models;
  readonly activeModelId = this.websocket.activeModelId;
  readonly permissionMode = this.websocket.permissionMode;
  readonly currentTurnId = this.websocket.currentTurnId;
  readonly streamingText = this.websocket.streamingText;
  readonly toolCalls = this.websocket.toolCalls;
  readonly approvals = this.websocket.approvals;
  readonly lastUsage = this.websocket.lastUsage;
  readonly settings = this.websocket.settings;
  readonly serverUrl = this.websocket.serverUrl;

  connect(): void { this.websocket.connect(); }
  saveSettings(s: Parameters<WebSocketProtocolService["saveSettings"]>[0]): void { this.websocket.saveSettings(s); }
  disconnect(): void { this.websocket.disconnect(); }
  listSessions(): void { this.websocket.listSessions(); }
  newSession(workspace: string): void { this.websocket.newSession(workspace); }
  openSession(sessionId: string): void { this.websocket.openSession(sessionId); }
  sendMessage(text: string): void { this.websocket.sendMessage(text); }
  cancel(): void { this.websocket.cancel(); }
  approve(approvalId: string): void { this.websocket.approve(approvalId); }
  reject(approvalId: string): void { this.websocket.reject(approvalId); }

  _setStateForTest(st: ProtocolState): void { this.websocket._setStateForTest(st); }
  _applyForTest(msg: ServerMessage): void { this.websocket._applyForTest(msg); }
  _handleRawForTest(raw: string): void { this.websocket._handleRawForTest(raw); }

  ngOnDestroy(): void { this.websocket.ngOnDestroy(); }
}
