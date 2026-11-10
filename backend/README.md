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
| GET | `/engagements/{id}/targets/{targetId}/challenge` | Bearer | What to publish to prove control of a host/domain target |
| POST | `/engagements/{id}/targets/{targetId}/verify` | Bearer | Check the proof (`?method=dns\|http` to limit it); 200 when verified, 422 when not |
| POST | `/engagements/{id}/abort` · `/close` | Bearer | Abort (kill switch) or close: no further runs are authorized |
| GET | `/audit` | Bearer | The caller's audit log, newest first (`?engagementId=&limit=&before=`) |
| GET | `/audit/verify` | Bearer | Re-check the audit log's hash chain |
| GET | `/audit/export` | Bearer | Download the whole audit log, oldest first (`?format=jsonl\|csv`) |
| POST | `/jobs` | Bearer | Submit a background job `{ type?: "scan" \| "assess" \| "triage", path?, sca?, engagementId?, catalogId?, scanId?, model?, maxFindings? }` → 202 with the job |
| WS | `/hub/jobs` | Bearer | SignalR hub: pushes `jobUpdated` for the caller's own jobs |
| GET | `/jobs` · `/jobs/{id}` | Bearer | List jobs (`?engagementId=&state=&limit=`) / one job with its state and result id |
| POST | `/jobs/{id}/cancel` | Bearer | Cancel a queued job, or ask a running one to stop |
| GET | `/jobs/{id}/events` | Bearer | The run's event stream from storage, in order (`?after=<sequence>&limit=`) |
| GET | `/approvals` · `/approvals/{id}` | Bearer | Requests waiting for (or already given) the operator's decision (`?status=pending&engagementId=`) |
| POST | `/approvals/{id}/approve` · `/deny` | Bearer | Decide a pending request, optionally with `{ "note": "…" }` |
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

Path targets are relative to the scan root and require `ownershipAttested: true` (the operator confirms they own the code or are authorized to test it). Host and domain targets start `Pending` and must pass a **proof-of-control challenge** before anything above the passive tier may touch them (nothing active exists yet, but the scope gate already enforces it):

1. `GET …/targets/{targetId}/challenge` returns a value `cerberus-verify=<random token>` unique to that target.
2. Publish it either as a DNS **TXT record at `_cerberus-challenge.<name>`** or as the body of **`/.well-known/cerberus-challenge.txt`** on the host (https is tried first, then http).
3. `POST …/targets/{targetId}/verify` looks it up; on an exact match the target becomes `Verified` (with the method and time). Every attempt, passed or failed, is audited.

A domain target proven at its apex covers its subdomains. An IP address or `host:port` has no DNS name of its own, so only the file method applies. The file fetch is a single GET with no redirects, a 5-second timeout and a 4 KB cap, and it **refuses loopback and link-local addresses** (including the cloud metadata address 169.254.169.254) unless `CERBERUS_ALLOW_LOCAL_TARGETS=true`; private ranges such as 10.0.0.0/8 are allowed, since internal hosts are legitimate targets. A proof does not expire and is not re-checked later. `POST /engagements/{id}/abort` is the kill switch: no further runs are authorized, the engagement's queued jobs are cancelled and its running jobs are told to stop (the response reports how many). It does not interrupt a synchronous `POST /scan` or `/compliance/assess` request already in flight — use jobs for anything long-running.

`engagementId` is **optional by default**: a passive scan without one runs as before and is audited, but is not scope-checked beyond the scan root. Set `CERBERUS_REQUIRE_ENGAGEMENT=true` to refuse (400) any scan, assessment or job submitted without one.

**Audit log:** every engagement change and every run (the authorization decision, including denials, and the result) is appended to a per-user log. Each event's SHA-256 hash covers its content and the previous event's hash, and database triggers reject any UPDATE or DELETE on the table. `GET /audit/verify` recomputes the chain and reports the first altered, missing or reordered event. Limits: this is tamper *evidence* — someone with write access to the database file could drop the triggers and rewrite the whole chain consistently, or remove the most recent events, without `verify` noticing. Keep `headHash` somewhere external if you need to detect that. `GET /audit/export` downloads the full log with each event's `previousHash` and `hash`, as JSON Lines or CSV (CSV fields that a spreadsheet would treat as a formula are prefixed with an apostrophe).

### `/jobs` — background job pipeline (first slice)

`POST /jobs` stores a job (`Queued`) and publishes it on the message bus; a background worker picks it up. Two job types exist: `scan` (`resultId` is the scan id, `GET /scans/{id}`) and `assess` (a compliance assessment of `catalogId` over a stored `scanId` or a fresh `path`; `resultId` is the assessment id, `GET /compliance/{id}`). States move forward only: `Queued → Running → Succeeded | Failed | Cancelled | TimedOut`.

**Live updates — the run's event stream:** connect a SignalR client to `/hub/jobs` with the bearer token (the `access_token` query parameter, which SignalR clients send for WebSockets, or an `Authorization` header). The hub is receive-only and drops connections without a valid session. Everything a job does arrives as a `jobEvent` message `{ jobId, sequence, type, data, createdAt }`:

| `type` | `data` | When |
|---|---|---|
| `state` | the job, as `GET /jobs/{id}` returns it | every state change, including `AwaitingApproval` |
| `step` | `{ id, title, status }` with status `running` / `completed` / `skipped` | a step of the run's task list starts or ends |
| `progress` | `{ done, total, message }` | progress within the current step |
| `finding` | the finding | as soon as a pass has it (the first 300 per run; the full list is in the result) |
| `text` | `{ channel, text }` | model output as it is written, in chunks; concatenate per channel (for triage, the channel is the finding id) |
| `triage` | the triage result | a model's verdict on one finding |
| `approval_requested`, `approval_resolved` | the approval request | the run waits for, or gets, the operator's decision |
| `note`, `truncated` | `{ message }` | a remark; or the run exceeded 5,000 events and only state changes follow |

Events are numbered per job from 1 without gaps, **stored before they are pushed**, and pushed in that order. A client that connects late or drops out reads `GET /jobs/{id}/events?after=<last sequence it has>` (pages of up to 2,000, with `more` and `lastSequence`) and then carries on with live events — so nothing has to be polled and nothing is lost. A failed push or write is logged and never affects the job. The older `jobUpdated`, `approvalRequested` and `approvalResolved` messages are still sent for existing clients. A step that was running when a job failed or was cancelled gets no closing event; the final `state` event says how the run ended.

- **Authorization twice:** a job under an engagement is scope-checked when submitted and again when it starts, so a job that waited in the queue cannot outlive an abort or a scope change.
- **Platform controls** (`Cerberus.Jobs/JobExecution.cs`), independent of what the work does: operator cancel, a time limit (`CERBERUS_JOB_TIMEOUT_SECONDS`, default 600), and a hard stop — if the work has not returned `CERBERUS_JOB_GRACE_SECONDS` (default 10) after being told to stop, the job is closed anyway and marked abandoned. Abandoned work is not killed (that needs the sandbox); it is only no longer waited for.
- **Concurrency:** `CERBERUS_JOB_CONCURRENCY` workers (default 2, max 16); at most 50 queued jobs per user.
- **Restarts:** the bus is in-memory (`InProcMessageBus` behind `IMessageBus`). On startup, jobs that were `Running` are marked `Failed` and jobs still `Queued` are re-published.

### `/approvals` — human in the loop

Some jobs must not run on a submit alone. Such a job is stored in state `AwaitingApproval` together with an **approval request** that says what will happen, to what, and what leaves the machine; `POST /jobs` then returns `{ job, approval }`. Nothing runs until the operator calls `POST /approvals/{id}/approve`, which releases the job to the workers (where it is scope-checked again). `…/deny` ends the job as `Cancelled` with the operator's note as the reason. A decision is final (a second one gets 409), covers that one job only, and is audited (`approval.request`, `approval.approve`, `approval.deny`).

- **Live:** the hub pushes `approvalRequested` when a job starts waiting and `approvalResolved` when it is approved, denied or withdrawn, alongside the usual `jobUpdated`.
- **Durable:** the wait is in the database, so it survives a restart and can be answered later from any client. There is no expiry yet.
- **Withdrawn automatically:** cancelling the job, or aborting its engagement, closes the request as `Cancelled`.
- **What needs approval today:** a `triage` job with a cloud model (kind `send-code-to-cloud-model`), because source code would be sent to a third party. Local-model triage, scans and assessments do not. This is the mechanism Phase 3 will use for intrusive actions, and agents for individual tool calls (PLAN.md §6.5).

### `/compliance` — Phase 2 (Compliance), deterministic engine

Catalogs are data: every `*.json` under `catalogs/` (`CERBERUS_CATALOG_ROOT`, default: the repo's `catalogs/`) is loaded at startup. `POST /compliance/assess` takes the findings of an existing scan (`scanId`) or runs a fresh scan of `path`, then judges every control that declares `scannerCategories` in the catalog JSON (the mapping is data, not code). A mapped control with triggering findings is **NonCompliant** (findings attached as evidence), a mapped control with none is **Compliant**, and everything else is **NotAssessed** (manual review). `score` is Compliant / (Compliant + NonCompliant) over the automatically assessed controls only — it is not an overall compliance grade. "Compliant" means only that the passive scanner found nothing in the mapped categories.

Controls mapped to the `dependency` category (ESAP GSG-07, OWASP A06) use the SCA findings. A fresh `path` assessment runs the dependency check unless `sca: false`; a `scanId` assessment uses whatever that scan recorded (`scaCompleted`). If the check was skipped, failed (OSV.dev unreachable) or truncated, such a control with no findings is **NotAssessed** rather than Compliant, and the response carries `dependencyCheckCompleted: false`. Scans stored before this field existed count as not completed.

**Spreadsheet export:** `GET /compliance/{id}/export` returns an `.xlsx` with three sheets. *Summary* has the totals and how to read them. *Controls* has one row per control in the response schema enterprises expect back: Verdict, Detailed Response, Action Plan, Time limit. *Evidence* lists every finding per control. Only what the engine established is pre-filled: Non-compliant rows get the findings, a generic "remediate and re-scan" action plan and the catalog's standard deadline (ESAP: 30 days); Compliant rows get the scan rationale; controls it could not judge have an empty Verdict (a pick-list from the catalog's response scale) for the operator to complete. Built with ClosedXML (`Cerberus.Compliance/ComplianceWorkbook.cs`).

**Operator answers:** the scanner can judge only a minority of controls, so each control can also carry an operator answer in the catalog's response schema. The verdict must be one of the catalog's response scale, a justification is required, an action plan is required for *Partially compliant* / *Non-compliant*, and the time limit must be within the catalog's maximum (ESAP: 1–180 days). An operator answer takes precedence over the scanner's verdict for that control (e.g. to record a false positive); the scanner's own verdict is kept and stays visible. `GET /compliance/{id}` returns both `summary` (scanner only) and `effective` (operator answers applied: compliant / partiallyCompliant / nonCompliant / notApplicable / unanswered).

**Spreadsheet import:** edit the *Controls* sheet of an export and `POST` the file back as the raw request body (max 5 MB), e.g. `curl -X POST .../compliance/{id}/import -H "Authorization: Bearer …" --data-binary @file.xlsx`. Rows with an empty Verdict and rows unchanged since the export are ignored, valid rows are saved, and invalid rows are returned as `errors` with their row number. Columns are found by header name, so they may be reordered but not renamed.

**Local database upgrades:** on startup the API creates the schema for a new database and, for an existing one, additively creates missing tables/indexes and adds missing columns (`Cerberus.Data/SchemaUpgrader.cs`). It never drops or alters existing columns.

### `/scan` — Phase 1 (Scan & Detect), deterministic layer

Walks a target path under the **scan root** (`CERBERUS_SCAN_ROOT`, default: current directory), applies the rule set line by line, and — unless `sca: false` — resolves dependencies against OSV.dev. Paths are confined to the scan root (traversal → 400) and symbolic links are never followed. This is a **trusted-target** layer: real engagements run it inside the sandbox with LLM reasoning on top (PLAN.md M3/M5).

**Rules** (`Cerberus.Scanning/Rules.cs`, 22 of them): secrets (AWS, GitHub, Slack, Google, Stripe, JWTs, private keys, credentials in connection URLs, hardcoded assignments), weak hashes and ciphers, disabled TLS verification, debug mode, wildcard CORS, world-writable permissions, dynamic code evaluation, shell commands, SQL built by concatenation, and unsafe deserialization — with Erlang/Elixir forms (`os:cmd`, `binary_to_term`, `{verify, verify_none}`, `Code.eval_string`) alongside Python, JS/TS, Java, C#, Go and shell. They are per-line regexes: **candidates to review, not confirmed vulnerabilities**. There is no data-flow analysis, so a rule cannot tell whether untrusted input actually reaches a sink.

**Findings** carry `id, title, severity, category, cwe, owasp, file, line, evidence, confidence, remediation`, plus:

- `context`: `"test"` or `"docs"` when the file is test code, documentation or an example. Confidence is lowered to `low` there, and compliance controls are judged on production-code findings only (the others are mentioned in the control's rationale).
- `suppressed`: `true` when the target marked the line with `cerberus:ignore` (optionally followed by rule ids or a category, e.g. `cerberus:ignore crypto.weak-hash`), on the same line or alone in a comment on the line above. Suppressed findings are **still returned**, sorted last, but excluded from `findingCount` and from compliance.

**What is not scanned**, and how it is reported: dependency and build directories (`node_modules`, `.git`, `bin`, `obj`, `dist`, `vendor`, …); binary, media, archive, lock and minified files, files over 1 MB and lines over 2,000 characters (`filesSkipped`); and paths listed in a `.cerberusignore` at the scan root or in the scanned directory (`filesIgnored`). Ignore patterns follow `.gitignore` conventions: `dir/`, `*.ext` at any depth, `a/b.py` anchored, `**` across directories. A scan stops at 5,000 findings and sets `truncated`.

Because markers and ignore files live in the target, a target can hide findings from the totals — which is why they are counted and returned rather than dropped. Check `suppressedCount` and `filesIgnored` when assessing code you do not control.

**Measured** (static pass only): the upstream RabbitMQ repository — 3,512 files in about 3 seconds, 251 candidates of which 82 are in production code (mostly shell commands, credentials in example URLs, `binary_to_term`, MD5 use); this repository's own code — 153 files, 2 candidates, both sample strings in test files (the repo's `.cerberusignore` excludes the vendored `security-skills/` material, the rule definitions and the rule tests).


### LLM triage of findings (`triage` job)

`POST /jobs {"type":"triage","scanId":"…","model":"…","maxFindings"?: 15,"includeTestsAndDocs"?: false}` asks a model to review a stored scan's findings one by one. For each it gets the rule that fired and about 12 lines of code either side, and must answer with a JSON verdict — `TruePositive`, `FalsePositive` or `NeedsReview` — plus its reasoning and a fix. Opinions are saved on the scan as they arrive and returned by `GET /scans/{id}` under `triage` (keyed by `findingId`).

- **Advisory only.** The code under review is untrusted input to the model and may try to talk it into "false positive", so an opinion never removes a finding and never changes a compliance verdict. The system prompt tells the model to treat the source as data, but that is a mitigation, not a guarantee.
- **You choose the model; there is no default.** A local Ollama model keeps the code on the machine. With a cloud model the job first waits for your approval (see `/approvals`), showing how many findings and files would be sent; only then is the code context sent to that provider, after credential-shaped values are replaced by `[REDACTED len=N]`. The audit log records which model reviewed how many findings and where the code went.
- **What is reviewed:** unsuppressed static findings in production code, most severe first, up to `maxFindings` (max 100). Dependency findings are skipped. An unusable or failed model reply becomes `NeedsReview` with `modelAnswered: false`.
- **Status:** tested with a scripted model only. It has **not** been run against a real model yet — see PLAN.md §17 "Pending verification" for the steps and the expected results.

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

`tests/Cerberus.Tests` hosts the real API in-process (`ApiIntegrationTests.cs`: jobs end to end, the jobs hub, the run event stream (order, replay, live delivery, model text), approvals, engagement scope and abort, audit, cross-user isolation; every model provider is replaced by a scripted stand-in, so tests never call a real model) and unit-tests the compliance engine's verdict rules, the shipped catalogs (they load, ids are unique, mappings use known scanner categories), operator-answer validation, the spreadsheet export/import round trip, the engagement scope policy, the audit hash chain, the job executor (cancel, time limit, hard stop), message bus and state rules, and proof-of-control verification (with DNS and HTTP faked; one test uses a real local listener to check the loopback guard). Every scanner rule has positive and negative cases (`ScannerRuleTests.cs`), alongside tests for suppression, ignore files, file filtering and symlinks. Not covered yet: the chat and API-key endpoints, the compliance response/import endpoints over HTTP, and SCA (it needs OSV.dev).
