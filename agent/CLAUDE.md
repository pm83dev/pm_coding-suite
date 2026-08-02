# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**pm-code-agent** is a local AI coding agent built in C# (.NET 8) that uses a ReAct (Reasoning + Acting) loop to autonomously generate and analyze code. It communicates with a local LLM inference server (llama.cpp) via an OpenAI-compatible REST API. The agent interaction language is Italian.

## Commands

All commands are run from `pm-code/`:

```powershell
dotnet build                        # Debug build
dotnet build -c Release             # Release build
dotnet run                          # Run the agent REPL
dotnet publish -c Release -r win-x64 --self-contained true  # Publish standalone exe
```

**Prerequisites**: llama-server.exe must be running on port 9000 (or the URL configured in `appsettings.json`). The server requires `--jinja` flag — do NOT use `--chat-template`.

No automated test framework exists. Validation is done through live agent interaction.

## Configuration

`pm-code/appsettings.json` controls:
- `LlmSettings:BaseUrl` — LLM server URL (default: `http://llm-coding.pm-softwareautomation.com`)
- `LlmSettings:Model` — Model name sent in API requests
- `AgentSettings:Workspace` — Agent's sandboxed working directory (default: `./AgentWorkspace`)
- `AgentSettings:MaxSteps` — Max tool-call iterations per user turn (default: 20)

## Architecture

### ReAct Loop (Program.cs)

The core loop in `Program.cs` drives all agent behavior:
1. Builds a system prompt containing the workspace tree snapshot and optional `AGENTS.md` project context
2. Sends the conversation history to the LLM
3. If the response contains tool calls → dispatches them via `ToolDispatcher`, injects results as `tool`-role messages, and loops
4. If the response is plain text → returns it to the user

Special REPL commands: `/init`, `/reset`, `/workspace`, `/tools`, `/cd <path>`, `/clear`

### Security Sandbox (WorkspaceContext.cs)

Every file operation is validated through `WorkspaceContext.Resolve(relativePath)`, which:
- Converts to an absolute path and checks it starts with the workspace root
- Blocks sibling-directory traversal (e.g., `../other-project`)
- Throws `UnauthorizedAccessException` on violations

Tree snapshots are capped at **2 levels deep and 30 files**; `.git`, `bin`, `obj`, `node_modules` are excluded. Use `get_directory_details` to drill into a specific folder on demand.

### Tool System (ToolDispatcher.cs + tool files)

`ToolDispatcher` registers all tools, routes calls, and applies a **soft-block** on duplicate `read_file` calls. On a blocked call it returns a structured summary (types, methods extracted via regex from the cached content) instead of re-reading. The cache clears on `/reset` or `/cd`.

| Tool file | Tools provided |
|---|---|
| `FileSystemTools.cs` | `read_file`, `edit_file`, `write_file`, `glob_files`, `list_directory`, `search_in_files`, `search_symbol`, `create_directory`, `move_file`, `delete_file` |
| `TerminalTools.cs` | `run_command` (PowerShell, 60s timeout), `run_dotnet`, `analyze_solution` |
| `AgentTools.cs` | `get_workspace_tree`, `get_directory_details`, `set_workspace` |
| `GitTools.cs` | `git_status`, `git_diff`, `git_log`, `git_add`, `git_commit`, `git_checkout` |

`analyze_solution` runs `dotnet build` and filters output to errors/warnings/files only — prefer it over `run_dotnet build` to keep context small.

`search_symbol` searches for a symbol with word-boundary matching and returns `file:line` pairs — more compact than `search_in_files` for navigating to definitions.

`TerminalTools` blocks destructive commands (`rm -rf`, `del /f`, `format`, `shutdown`, `rd /s`, `rmdir /s`) and appends `[ESITO: SUCCESSO/FALLITO]` to output. Terminal output is truncated to 3000 chars (tail preserved for error messages).

### History Compression (Program.cs)

When `history.Count > 12`, `CompressHistory()` is called automatically at the start of each `RunAgentLoopAsync` invocation. It keeps the system message + last 8 interactions (cutting always at a user-message boundary) and replaces earlier messages with a compact text summary. This prevents quality degradation on 7B/8B models with long contexts.

### LLM Client (LlamaClient.cs)

HTTP client for `/v1/chat/completions`. Uses snake_case JSON serialization and a 5-minute request timeout. Health check hits `/v1/models` on startup.

### Chat Protocol (ChatModels.cs)

OpenAI-compatible DTOs: `ChatRequest`, `ChatMessage` (roles: system/user/assistant/tool), `ToolDefinition` (JSON schema), `ChatResponse`. Tool results are injected as messages with `role: "tool"` and the matching `tool_call_id`.

## AGENTS.md Convention

The `/init` command analyzes the active workspace project and writes an `AGENTS.md` file (≤200 words) that is automatically injected into every system prompt. This gives the LLM project-specific context without manual configuration. If `AGENTS.md` exists in the workspace root, it is always loaded.
