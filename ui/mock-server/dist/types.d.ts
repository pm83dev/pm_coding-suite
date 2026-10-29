import type { ChatMessage, ModelInfo, PermissionMode, SessionInfo } from "@pm-ui/protocol";
/** Stato di una sessione mantenuto in memoria dal mock. */
export interface MockSession {
    info: SessionInfo;
    messages: ChatMessage[];
    model: string;
    permission: PermissionMode;
}
/** Approvazione pendente (turno sospeso in waiting_approval). */
export interface PendingApproval {
    approvalId: string;
    turnId: string;
    kind: "write" | "command";
    path?: string;
    title: string;
    unifiedDiff?: string;
    command?: string;
    expiresAt: number;
    /** Timer di scadenza (clearato alla risoluzione). */
    expiryTimer?: NodeJS.Timeout;
}
/** Stato del turno in corso per una sessione. */
export interface ActiveTurn {
    turnId: string;
    messageId: string;
}
export interface MockOptions {
    token: string;
    serverVersion: string;
    /** Durata in ms delle approvazioni prima della scadenza (default 120000). */
    approvalTtlMs: number;
}
export declare const MODELS: ModelInfo[];
export declare const DEFAULT_MODEL: string;
