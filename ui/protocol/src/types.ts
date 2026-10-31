/**
 * Protocollo v1 pm-code — tipi TypeScript.
 * Un messaggio WebSocket è un singolo JSON con la busta {@link Envelope}.
 */

export const PROTOCOL_VERSION = 1;

/** Busta comune a tutti i messaggi (client e server). */
export interface Envelope<P = unknown> {
  v: number;
  id: string;
  ts: string;
  sessionId: string | null;
  type: string;
  payload: P;
}

// ---------------------------------------------------------------------------
// Entità condivise
// ---------------------------------------------------------------------------

export type SessionStatus = "idle" | "running" | "waiting_approval";

export interface SessionInfo {
  id: string;
  title: string;
  workspace: string;
  updatedAt: string; // ISO8601
  status: SessionStatus;
}

/** Messaggio di chat conservato nello storico di una sessione. */
export interface ChatMessage {
  id: string;
  role: "user" | "assistant";
  text: string;
  ts: string; // ISO8601
}

export type PermissionMode = "readonly" | "ask" | "auto";

export interface ModelInfo {
  id: string;
  label: string;
  endpoint: string;
  contextSize: number;
}

// ---------------------------------------------------------------------------
// Payload dei comandi (client -> server)
// ---------------------------------------------------------------------------

export interface HelloPayload {
  token: string;
  clientName: string;
  clientVersion: string;
}

export interface ListSessionsPayload {}

export interface NewSessionPayload {
  workspace: string;
}

export interface OpenSessionPayload {
  sessionId: string;
}

export interface DeleteSessionPayload {
  sessionId: string;
}

export interface DeleteSessionAckPayload {
  sessionId: string;
}

export interface SendMessagePayload {
  text: string;
}

export interface CancelPayload {}

export interface ApprovePayload {
  approvalId: string;
}

export interface RejectPayload {
  approvalId: string;
  reason?: string;
}

export interface SetModelPayload {
  modelId: string;
}

export interface SetPermissionPayload {
  mode: PermissionMode;
}

// ---------------------------------------------------------------------------
// Payload degli eventi (server -> client)
// ---------------------------------------------------------------------------

export interface HelloOkPayload {
  serverVersion: string;
  protocol: number;
}

export type ErrorCode = "unauthorized" | "unsupported_version" | "unknown_session" | "bad_request";

export interface ErrorPayload {
  code: ErrorCode;
  message: string;
}

export interface SessionsListPayload {
  sessions: SessionInfo[];
}

export interface SessionStatePayload {
  session: SessionInfo;
  messages: ChatMessage[];
  model: string;
  permission: PermissionMode;
}

export interface ModelsListPayload {
  models: ModelInfo[];
  active: string;
}

export interface PermissionStatePayload {
  mode: PermissionMode;
  rules: unknown[];
}

export interface TurnStartedPayload {
  turnId: string;
}

export interface AssistantDeltaPayload {
  turnId: string;
  messageId: string;
  text: string; // frammento di testo (streaming)
}

export interface UsageInfo {
  promptTokens: number;
  completionTokens: number;
}

export interface AssistantDonePayload {
  turnId: string;
  messageId: string;
  usage: UsageInfo;
}

export interface ToolCallStartedPayload {
  turnId: string;
  callId: string;
  name: string;
  args: Record<string, unknown>;
}

export interface ToolCallFinishedPayload {
  turnId: string;
  callId: string;
  ok: boolean;
  summary: string;
  durationMs: number;
}

export type ApprovalKind = "write" | "command";

export interface ApprovalRequestPayload {
  turnId: string;
  approvalId: string;
  kind: ApprovalKind;
  path?: string; // solo kind "write"
  title: string;
  unifiedDiff?: string; // solo kind "write"
  command?: string; // solo kind "command"
  expiresAt: string; // ISO8601
}

export type ApprovalOutcome = "approved" | "rejected" | "expired" | "cancelled";

export interface ApprovalResolvedPayload {
  approvalId: string;
  outcome: ApprovalOutcome;
}

export type TurnFinishReason = "completed" | "cancelled" | "error" | "max_steps" | "timeout" | "aborted";

export interface TurnFinishedPayload {
  turnId: string;
  reason: TurnFinishReason;
}

// ---------------------------------------------------------------------------
// Messaggi completi (busta + payload) e union discriminate su `type`
// ---------------------------------------------------------------------------

export type ClientMessage =
  | Envelope<HelloPayload> & { type: "hello" }
  | Envelope<ListSessionsPayload> & { type: "list_sessions" }
  | Envelope<NewSessionPayload> & { type: "new_session" }
  | Envelope<OpenSessionPayload> & { type: "open_session" }
  | Envelope<DeleteSessionPayload> & { type: "delete_session" }
  | Envelope<SendMessagePayload> & { type: "send_message" }
  | Envelope<CancelPayload> & { type: "cancel" }
  | Envelope<ApprovePayload> & { type: "approve" }
  | Envelope<RejectPayload> & { type: "reject" }
  | Envelope<SetModelPayload> & { type: "set_model" }
  | Envelope<SetPermissionPayload> & { type: "set_permission" };

export type ServerMessage =
  | Envelope<HelloOkPayload> & { type: "hello_ok" }
  | Envelope<ErrorPayload> & { type: "error" }
  | Envelope<SessionsListPayload> & { type: "sessions_list" }
  | Envelope<SessionStatePayload> & { type: "session_state" }
  | Envelope<DeleteSessionAckPayload> & { type: "session_deleted" }
  | Envelope<ModelsListPayload> & { type: "models_list" }
  | Envelope<PermissionStatePayload> & { type: "permission_state" }
  | Envelope<TurnStartedPayload> & { type: "turn_started" }
  | Envelope<AssistantDeltaPayload> & { type: "assistant_delta" }
  | Envelope<AssistantDonePayload> & { type: "assistant_done" }
  | Envelope<ToolCallStartedPayload> & { type: "tool_call_started" }
  | Envelope<ToolCallFinishedPayload> & { type: "tool_call_finished" }
  | Envelope<ApprovalRequestPayload> & { type: "approval_request" }
  | Envelope<ApprovalResolvedPayload> & { type: "approval_resolved" }
  | Envelope<TurnFinishedPayload> & { type: "turn_finished" };

export const CLIENT_MESSAGE_TYPES = [
  "hello",
  "list_sessions",
  "new_session",
  "open_session",
  "delete_session",
  "send_message",
  "cancel",
  "approve",
  "reject",
  "set_model",
  "set_permission",
] as const;

export const SERVER_MESSAGE_TYPES = [
  "hello_ok",
  "error",
  "sessions_list",
  "session_state",
  "session_deleted",
  "models_list",
  "permission_state",
  "turn_started",
  "assistant_delta",
  "assistant_done",
  "tool_call_started",
  "tool_call_finished",
  "approval_request",
  "approval_resolved",
  "turn_finished",
] as const;
