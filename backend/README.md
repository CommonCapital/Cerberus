# Cerberus backend (.NET)

The .NET port of the Cerberus server (milestone **M2**). Fully local: ASP.NET Core + EF Core (SQLite) + SignalR, with no hosted services. This runs alongside the TypeScript baseline during the transition and will eventually replace `packages/server`.

## Projects

| Project | Role |
|---|---|
| `Cerberus.Api` | ASP.NET Core minimal API + SignalR hub; auth, sessions, chat endpoints |
| `Cerberus.Core` | Domain abstractions: model registry, `IModelProvider`, password hashing |
| `Cerberus.Data` | EF Core `DbContext` + entities (`User`, `AuthSession`, `Session`), SQLite |
| `Cerberus.Models` | `IModelProvider` implementations: Anthropic, OpenAI, Ollama (+ resolver) |

## Run

```bash
cd backend
dotnet run --project src/Cerberus.Api
```

Environment:

- `PORT` — HTTP port (default `3000`).
- `CERBERUS_DB` — SQLite file path (default `cerberus.db` under the API content root). The schema is created automatically on first run.
- `ANTHROPIC_API_KEY` / `OPENAI_API_KEY` — user-supplied keys for cloud models (optional).
- `OLLAMA_BASE_URL` — local Ollama endpoint (default `http://localhost:11434/v1`). Use Ollama for fully offline operation.

## Endpoints

| Method | Path | Auth | Notes |
|---|---|---|---|
| GET | `/health` | — | Liveness |
| POST | `/auth/register` | — | `{ email, password }` → `{ token }` |
| POST | `/auth/login` | — | `{ email, password }` → `{ token }` |
| POST | `/auth/logout` | Bearer | Revokes the token |
| GET | `/sessions` | Bearer | List sessions |
| GET | `/sessions/{id}` | Bearer | Session + parsed messages |
| POST | `/sessions` | Bearer | `{ title }` → created session |
| GET | `/settings/keys` | Bearer | Which providers have a key set (never returns the key) |
| PUT | `/settings/keys` | Bearer | `{ provider, apiKey }` — store a key (encrypted at rest) |
| DELETE | `/settings/keys/{provider}` | Bearer | Remove a stored key |
| POST | `/chat` | Bearer | AI-SDK UI-message stream; CLI-compatible body `{ id, messages, mode, model }`; persists UIMessages |
| GET, POST | `/engagements` | Bearer | List / create an engagement `{ name, maxTier?, startsAt?, endsAt? }` |
| GET | `/engagements/{id}` | Bearer | An engagement with its in-scope targets |
| POST | `/engagements/{id}/targets` | Bearer | Add a target `{ type?, locator, ownershipAttested }` to the scope |
| DELETE | `/engagements/{id}/targets/{targetId}` | Bearer | Remove a target from the scope |
| POST | `/engagements/{id}/abort` · `/close` | Bearer | Abort (kill switch) or close: no further runs are authorized |
| GET | `/audit` | Bearer | The caller's audit log, newest first (`?engagementId=&limit=&before=`) |
| GET | `/audit/verify` | Bearer | Re-check the audit log's hash chain |
| POST | `/jobs` | Bearer | Submit a background job `{ type?: "scan", path, sca?, engagementId? }` → 202 with the job |
| GET | `/jobs` · `/jobs/{id}` | Bearer | List jobs (`?engagementId=&state=&limit=`) / one job with its state and result id |
| POST | `/jobs/{id}/cancel` | Bearer | Cancel a queued job, or ask a running one to stop |
| POST | `/scan` | Bearer | Phase-1 passive scan of `{ path }` (within the scan root) → findings; persists the scan |
| GET | `/scans` | Bearer | List this user's past scans (id, target, counts, timestamp) |
| GET | `/scans/{id}` | Bearer | A persisted scan with its findings |
| GET | `/compliance/catalogs` | Bearer | List built-in control catalogs (summary) |
| GET | `/compliance/catalogs/{id}` | Bearer | A full catalog (sections + controls) |
| POST | `/compliance/assess` | Bearer | Phase-2 assessment of `{ catalogId?, scanId? \| path?, sca? }` → control matrix + score; persists it |
| GET | `/compliance` | Bearer | List this user's past assessments |
| GET | `/compliance/{id}` | Bearer | A persisted assessment with its control matrix |
| GET | `/compliance/{id}/export` | Bearer | Download the assessment as a vendor-assurance spreadsheet (.xlsx) |
| POST | `/compliance/{id}/import` | Bearer | Import operator answers from a completed export (body = the raw .xlsx file) |
| PUT | `/compliance/{id}/responses/{controlId}` | Bearer | Set the operator's answer `{ verdict, justification, actionPlan?, timeLimitDays? }` for a control |
| DELETE | `/compliance/{id}/responses/{controlId}` | Bearer | Remove the operator's answer (falls back to the scanner's verdict) |
| WS | `/hub/chat` | — (skeleton) | SignalR streaming hub |

### `/engagements` and `/audit` — authorization & safety spine (first slice)

An **engagement** states what may be tested: a list of in-scope targets, a maximum capability tier (T0 passive, T1 active-safe; T2/T3 cannot be granted yet) and an optional time window. `POST /scan` and `POST /compliance/assess` accept an `engagementId`; the run is then checked by `ScopePolicy` (`Cerberus.Authorization`) **before any work starts** and refused with 403 when:

- the path is not at or below one of the engagement's path targets (paths that are absolute or contain `..` are refused outright),
- the engagement is closed or aborted, or the current time is outside its window,
- the tier exceeds the engagement's maximum.

Path targets are relative to the scan root and require `ownershipAttested: true` (the operator confirms they own the code or are authorized to test it). Host and domain targets can be recorded but stay `Pending`: proof-of-control verification is not implemented, and nothing active runs against them yet. `POST /engagements/{id}/abort` is the kill switch: no further runs are authorized, the engagement's queued jobs are cancelled and its running jobs are told to stop (the response reports how many). It does not interrupt a synchronous `POST /scan` or `/compliance/assess` request already in flight — use jobs for anything long-running.

`engagementId` is still **optional**: a passive scan without one runs as before and is audited, but is not scope-checked beyond the scan root.

**Audit log:** every engagement change and every run (the authorization decision, including denials, and the result) is appended to a per-user log. Each event's SHA-256 hash covers its content and the previous event's hash, and database triggers reject any UPDATE or DELETE on the table. `GET /audit/verify` recomputes the chain and reports the first altered, missing or reordered event. Limits: this is tamper *evidence* — someone with write access to the database file could drop the triggers and rewrite the whole chain consistently, or remove the most recent events, without `verify` noticing. Keep `headHash` somewhere external if you need to detect that.

### `/jobs` — background job pipeline (first slice)

`POST /jobs` stores a job (`Queued`) and publishes it on the message bus; a background worker picks it up. Only `scan` jobs exist today; a finished job's `resultId` is the scan id (`GET /scans/{id}`). States move forward only: `Queued → Running → Succeeded | Failed | Cancelled | TimedOut`. Poll `GET /jobs/{id}` for progress (no push notifications yet).

- **Authorization twice:** a job under an engagement is scope-checked when submitted and again when it starts, so a job that waited in the queue cannot outlive an abort or a scope change.
- **Platform controls** (`Cerberus.Jobs/JobExecution.cs`), independent of what the work does: operator cancel, a time limit (`CERBERUS_JOB_TIMEOUT_SECONDS`, default 600), and a hard stop — if the work has not returned `CERBERUS_JOB_GRACE_SECONDS` (default 10) after being told to stop, the job is closed anyway and marked abandoned. Abandoned work is not killed (that needs the sandbox); it is only no longer waited for.
- **Concurrency:** `CERBERUS_JOB_CONCURRENCY` workers (default 2, max 16); at most 50 queued jobs per user.
- **Restarts:** the bus is in-memory (`InProcMessageBus` behind `IMessageBus`). On startup, jobs that were `Running` are marked `Failed` and jobs still `Queued` are re-published.

### `/compliance` — Phase 2 (Compliance), deterministic engine

Catalogs are data: every `*.json` under `catalogs/` (`CERBERUS_CATALOG_ROOT`, default: the repo's `catalogs/`) is loaded at startup. `POST /compliance/assess` takes the findings of an existing scan (`scanId`) or runs a fresh scan of `path`, then judges every control that declares `scannerCategories` in the catalog JSON (the mapping is data, not code). A mapped control with triggering findings is **NonCompliant** (findings attached as evidence), a mapped control with none is **Compliant**, and everything else is **NotAssessed** (manual review). `score` is Compliant / (Compliant + NonCompliant) over the automatically assessed controls only — it is not an overall compliance grade. "Compliant" means only that the passive scanner found nothing in the mapped categories.

Controls mapped to the `dependency` category (ESAP GSG-07, OWASP A06) use the SCA findings. A fresh `path` assessment runs the dependency check unless `sca: false`; a `scanId` assessment uses whatever that scan recorded (`scaCompleted`). If the check was skipped, failed (OSV.dev unreachable) or truncated, such a control with no findings is **NotAssessed** rather than Compliant, and the response carries `dependencyCheckCompleted: false`. Scans stored before this field existed count as not completed.

**Spreadsheet export:** `GET /compliance/{id}/export` returns an `.xlsx` with three sheets. *Summary* has the totals and how to read them. *Controls* has one row per control in the response schema enterprises expect back: Verdict, Detailed Response, Action Plan, Time limit. *Evidence* lists every finding per control. Only what the engine established is pre-filled: Non-compliant rows get the findings, a generic "remediate and re-scan" action plan and the catalog's standard deadline (ESAP: 30 days); Compliant rows get the scan rationale; controls it could not judge have an empty Verdict (a pick-list from the catalog's response scale) for the operator to complete. Built with ClosedXML (`Cerberus.Compliance/ComplianceWorkbook.cs`).

**Operator answers:** the scanner can judge only a minority of controls, so each control can also carry an operator answer in the catalog's response schema. The verdict must be one of the catalog's response scale, a justification is required, an action plan is required for *Partially compliant* / *Non-compliant*, and the time limit must be within the catalog's maximum (ESAP: 1–180 days). An operator answer takes precedence over the scanner's verdict for that control (e.g. to record a false positive); the scanner's own verdict is kept and stays visible. `GET /compliance/{id}` returns both `summary` (scanner only) and `effective` (operator answers applied: compliant / partiallyCompliant / nonCompliant / notApplicable / unanswered).

**Spreadsheet import:** edit the *Controls* sheet of an export and `POST` the file back as the raw request body (max 5 MB), e.g. `curl -X POST .../compliance/{id}/import -H "Authorization: Bearer …" --data-binary @file.xlsx`. Rows with an empty Verdict and rows unchanged since the export are ignored, valid rows are saved, and invalid rows are returned as `errors` with their row number. Columns are found by header name, so they may be reordered but not renamed.

**Local database upgrades:** on startup the API creates the schema for a new database and, for an existing one, additively creates missing tables/indexes and adds missing columns (`Cerberus.Data/SchemaUpgrader.cs`). It never drops or alters existing columns.

### `/scan` — Phase 1 (Scan & Detect), MVP

Walks a target path under the **scan root** (`CERBERUS_SCAN_ROOT`, default: current directory) and applies a regex rule set for secret detection (AWS keys, private keys, GitHub/Slack tokens, hardcoded credentials — CWE-798) and common insecure patterns (weak hashes CWE-327, disabled TLS verification CWE-295, `eval` CWE-95, `shell=True` CWE-78). Returns normalized **findings** (`id, title, severity, category, cwe, file, line, evidence, confidence`), sorted by severity. Paths are confined to the scan root (traversal → 400). This is the deterministic layer and a **trusted-target MVP** — real engagements run it inside the sandbox with LLM reasoning on top (PLAN.md M3/M5).

### Per-user API keys

Each user stores their own Anthropic/OpenAI keys via `/settings/keys`. Keys are **encrypted at rest** with ASP.NET Core Data Protection (keyring under `dp-keys/`, or `CERBERUS_KEYRING`), never returned by the API, and decrypted in-memory only to authorize a `/chat` request — taking precedence over the `*_API_KEY` env vars. Ollama models need no key.

### `/chat` protocol

The endpoint speaks the **AI-SDK UI-message stream** wire format, so `@ai-sdk/react`'s `DefaultChatTransport` consumes it unchanged: `text/event-stream` with `x-vercel-ai-ui-message-stream: v1`, chunks framed as `data: {json}\n\n` in the sequence `start` → `start-step` → `text-start` → `text-delta*` → `text-end` → `finish-step` → `finish` → `[DONE]`, with `messageMetadata` carrying mode/model/durationMs. Sessions store and return UIMessages (`{ id, role, parts }`). A dev model id `echo` streams a canned reply with no API key for headless verification.

## Status & follow-ups

- Auth, sessions, the streaming chat pipeline (provider resolver → Anthropic/OpenAI/Ollama → UI-message stream → persistence), and AI-SDK protocol compatibility are implemented and verified (raw SSE checked against the `ai` package format; UIMessage persistence round-trips).
- Per-user **encrypted** API-key storage is implemented (`/settings/keys`, Data Protection) and used by `/chat`.
- **Follow-ups:** retarget the TS CLI at this server (swap the hono client base URL / calls); SignalR hub auth; tool-calling (tool-input/tool-output UI chunks + client execution).

## Tests

```bash
dotnet test Cerberus.slnx
```

`tests/Cerberus.Tests` covers the compliance engine's verdict rules, the shipped catalogs (they load, ids are unique, mappings use known scanner categories), operator-answer validation, the spreadsheet export/import round trip, the engagement scope policy, the audit hash chain, and the job executor (cancel, time limit, hard stop), message bus and state rules. The API endpoints, scanner and SCA have no automated tests yet.
