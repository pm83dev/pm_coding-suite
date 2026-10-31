/**
 * Protocollo v1 pm-code — tipi TypeScript.
 * Un messaggio WebSocket è un singolo JSON con la busta {@link Envelope}.
 */
export const PROTOCOL_VERSION = 1;
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
];
export const SERVER_MESSAGE_TYPES = [
    "hello_ok",
    "error",
    "sessions_list",
    "session_state",
    "session_deleted",
    "models_list",
    "permission_state",
    "turn_started",
    "status",
    "assistant_delta",
    "assistant_done",
    "tool_call_started",
    "tool_call_finished",
    "approval_request",
    "approval_resolved",
    "turn_finished",
];
//# sourceMappingURL=types.js.map