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
  expiresAt: number; // epoch ms
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

export const MODELS: ModelInfo[] = [
  { id: "llama-3.1-8b", label: "Llama 3.1 8B (locale)", endpoint: "http://localhost:8080/v1", contextSize: 8192 },
  { id: "llama-3.1-70b", label: "Llama 3.1 70B (locale)", endpoint: "http://localhost:8081/v1", contextSize: 32768 },
  { id: "gpt-4o-mini", label: "GPT-4o mini (API)", endpoint: "https://api.openai.com/v1", contextSize: 128000 },
];

export const DEFAULT_MODEL = MODELS[1]!.id;
