# Lore (v2) Architecture

Lore is a multi-developer shared memory system for coding agents (**Agy**, **Claude Code**, and **Codex CLI**) built on **.NET 10**, **Azure Cosmos DB Serverless**, **Azure OpenAI**, and **Anthropic Claude Haiku**.

## Four Memory Types

1. **`rule` (Hard Rules)** — Standing rules, branch/PR policies, environment traps. Always injected at session start via fast local cache; never left to search.
2. **`process` (Runbooks / Long Processes)** — Multi-step procedures (local login, deploys, E2E test setup) stored as coherent Markdown documents, indexed per `## ` section so semantic search lands on the right step while `lore_get` returns the complete doc.
3. **`app_ui` (App & UI Behavior)** — Living screen/feature specifications (routes, visible UI states, step-by-step user flows, APIs/tables touched, quirks) linked to source path globs for automatic git staleness checks.
4. **`note` (Discoveries & Decisions)** — Architectural rationale, bug root causes, and environment gotchas with explicit provenance (`user`, `verified`, `inferred`).

## Storage & Hybrid Search (Azure Cosmos DB Serverless)

- `memories` container (`/repoId`) — Canonical full documents.
- `chunks` container (`/repoId`) — Searchable units with `1536`-dim `Float32` `Cosine` `DiskANN` vector index (`/embedding`) + `en-US` `FullTextIndex` (`/searchText`), queried via `ORDER BY RANK RRF(VectorDistance(...), FullTextScore(...), [2, 1])`.
- `telemetry` container (`/repoId`, 90-day TTL) — Search latencies, similarity scores, prompt-push hit/skip, and capture outcomes.

## Recall & Capture Loops

- **Session start:** Injects `rule` items + compact TOC of `process` and `app_ui` items from a 5-minute local cache (<30ms).
- **Prompt push:** Runs hybrid search on *new* user prompts (deduped per prompt hash in Agy `PreInvocation`); injects only hits clearing `vectorScore >= 0.45` and never previously injected in the session. Emits `{}` when nothing qualifies.
- **Background capture:** On `Stop` / `PreCompact`, spawns `lore capture` detached. Deterministic `TranscriptPreFilter` extracts user corrections, error→retry→success loops, Playwright UI traces, and multi-step command runs (~12k char cap), then **Claude Haiku (`claude-haiku-5-5`)** classifies `scope` (`repo` vs `global`) and runs a **search-before-write** check (`SKIP`, `MERGE`, or `SUPERSEDE`). `inferred` discoveries can never overwrite `user` rules.

## Profile Isolation & Non-Repo Scopes

- **Named profiles + `pathRoutes`:** `~/.lore/config.json` supports `profiles` (separate Cosmos account/database, OpenAI endpoint, API URL, and optional `allowedHarnesses` allowlist) and directory-prefix `pathRoutes` (checked against both `cwd` and linked git worktree main paths), with isolated per-profile local caches under `%LOCALAPPDATA%\lore\profiles\<profile>`.
- **Global & non-repo scopes:** Every profile has its own `_global` partition (via `--global` / `scope: "global"`, or auto-classified by Haiku) plus named non-repo namespaces (`--scope area:<name>`) for cross-repo or non-git knowledge.

