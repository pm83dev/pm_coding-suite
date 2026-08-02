import * as vscode from 'vscode';
import { stripThinkTags } from './utils';
import { REQUEST_TIMEOUT_MS, STOP_SEQUENCES } from './config';

export let activeController: AbortController | undefined;

export function abortActiveRequests() {
  activeController?.abort();
}

// ──────────────────────────────────────────────
// /completion — usata da fetchCompletion (FIM, legacy PM Autocomplete).
// Non toccata dall'integrazione con l'agent .NET: @pm ora parla con l'agent
// via AgentProcess (child_process + NDJSON su stdio), non più con questo endpoint.
// ──────────────────────────────────────────────

export async function callServer(
  prompt: string,
  maxTokens: number,
  temperature: number,
  timeoutMs: number,
  stopSequences: string[],
  signal: AbortSignal,
  serverUrl: string
): Promise<string | null> {
  const endpoint = `${serverUrl}/completion`;
  try {
    const response = await fetch(endpoint, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        prompt, max_tokens: maxTokens, temperature,
        stop: stopSequences, stream: false, cache_prompt: true,
        top_k: 40, top_p: 0.95, min_p: 0.05, repeat_penalty: 1.1,
      }),
      signal,
    });

    if (!response.ok) { console.error(`[PM] Server error: ${response.status}`); return null; }

    const data = await response.json() as { content?: string };
    return data.content != null ? stripThinkTags(data.content) : null;
  } catch (err: unknown) {
    if (err instanceof Error && err.name === 'AbortError') return null;
    console.error('[PM] Fetch error:', err);
    return null;
  }
}

export async function fetchCompletion(
  prompt: string,
  cancelToken: vscode.CancellationToken,
  serverUrl: string,
  maxTokens: number,
  temperature: number
): Promise<string | null> {
  activeController?.abort();
  activeController = new AbortController();
  const timeoutId     = setTimeout(() => activeController?.abort(), REQUEST_TIMEOUT_MS);
  const cancelDispose = cancelToken.onCancellationRequested(() => activeController?.abort());
  try {
    return await callServer(prompt, maxTokens, temperature, REQUEST_TIMEOUT_MS, STOP_SEQUENCES, activeController.signal, serverUrl);
  } finally {
    clearTimeout(timeoutId);
    cancelDispose.dispose();
  }
}
