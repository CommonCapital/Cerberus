# Cerberus backend (.NET)

**The backend of Cerberus.** Fully local: ASP.NET Core + EF Core (SQLite) + SignalR, with no hosted services. It has every capability of the original TypeScript server (`packages/server`) — auth, sessions, and the streaming chat agent with tool calling — plus everything security-specific. The TypeScript server is superseded and kept only for reference; the CLI talks to this one. See "Parity with the TypeScript server" below.

## Projects

| Project | Role |
|---|---|
| `Cerberus.Api` | ASP.NET Core minimal API + SignalR hubs: every endpoint, the job worker, the chat runner |
| `Cerberus.Core` | Domain abstractions: model registry, `IModelProvider`, the chat model (turns, tools, events), password hashing |
| `Cerberus.Data` | EF Core `DbContext`, entities, additive schema upgrades; SQLite |
| `Cerberus.Models` | Model providers: Anthropic (official SDK), OpenAI and Ollama (Chat Completions), the `echo` stand-in |
| `Cerberus.Scanning` | Deterministic scanner rules and the dependency (SCA) check |
| `Cerberus.Compliance` | Control catalogs, compliance engine, spreadsheet export/import, operator answers |
| `Cerberus.Authorization` | Capability tiers, engagement scope policy, proof of control, audit hash chain |
| `Cerberus.Jobs` | Message bus, job states and executor, run events, operator questions |
| `Cerberus.Triage` | LLM review of findings: prompt, redaction, reply parsing |
| `Cerberus.Skills` | The skills library: index, search, tier-based access |

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
- `CERBERUS_BIND` — address to listen on (default `localhost`; the container image sets `0.0.0.0`).
- `CERBERUS_SKILLS_ROOT` / `CERBERUS_CATALOG_ROOT` — where the skills library and control catalogs are (default: the repository's `security-skills/skills` and `catalogs`).

Or in a container, from the repository root (see `docker-compose.yml`; written but not yet run):

```bash
docker compose up -d
```

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
| GET, PUT, DELETE | `/settings/permissions` | Bearer | The permission policy (`?engagementId=` for one engagement's override): `{ mode?, allowedKinds? }` |
| GET | `/skills` | Bearer | The skills library catalog (`?q=&subdomain=&limit=`), with the tier each skill needs |
| GET | `/skills/{name}` | Bearer | One skill's text (403 for skills reserved for a tier that cannot be granted) |
| GET | `/settings/keys` | Bearer | Which providers have a key set (never returns the key) |
| PUT | `/settings/keys` | Bearer | `{ provider, apiKey }` — store a key (encrypted at rest) |
| DELETE | `/settings/keys/{provider}` | Bearer | Remove a stored key |
| POST | `/chat` | Bearer | The chat agent: AI-SDK UI-message stream with tool calls; body `{ id, messages, mode, model }` |
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
| POST | `/jobs` | Bearer | Submit a background job `{ type?: "scan" \| "assess" \| "triage" \| "review" \| "sast" \| "threat-model" \| "evidence", path?, sca?, engagementId?, catalogId?, catalogs?, scanId?, assessmentId?, model?, maxFindings?, maxSteps?, maxControls?, controlIds?, instructions? }` → 202 with the job |
| POST, DELETE | `/compliance/{id}/suggestions/{controlId}/accept` · `/compliance/{id}/suggestions/{controlId}` | Bearer | Accept a model's suggested answer as the operator's (body: `{ justification?, actionPlan?, timeLimitDays? }`) / dismiss it |
| GET | `/agent-runs` · `/agent-runs/{id}` · `/agent-runs/{id}/report` | Bearer | What agent jobs produced: summary, counts and (for one run) what the model recorded; the written report as Markdown, for agents that write one (the threat model) |
| GET | `/reviews` · `/reviews/{id}` · `/reviews/{id}/report` | Bearer | Outcomes of `review` jobs: the scan and assessments each produced; the report as Markdown |
| WS | `/hub/jobs` | Bearer | SignalR hub: pushes `jobUpdated` for the caller's own jobs |
| GET | `/jobs` · `/jobs/{id}` | Bearer | List jobs (`?engagementId=&state=&limit=`) / one job with its state and result id |
| POST | `/jobs/{id}/cancel` | Bearer | Cancel a queued job, or ask a running one to stop |
| POST | `/jobs/{id}/messages` | Bearer | Say something to the job's running agent `{ text }` → 202; 409 if no agent of that job is running |
| GET | `/questions` · `/questions/{id}` | Bearer | Questions running jobs have put to the operator (`?status=pending&jobId=`) |
| POST | `/questions/{id}/answer` | Bearer | Answer a pending question: `{ "selected": ["…"], "text"?: "…" }` |
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

`engagementId` is **optional by default**: a passive scan without one runs as before and is audited, but is not scope-checked beyond the scan root. Set `CERBERUS_REQUIRE_ENGAGEMENT=true` to refuse (400) any scan, assessment or job submitted without one. The CLI manages engagements itself (`/engagement`, `/scope`, `/abort`) and sends the chosen one with every job it starts.

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
| `question`, `question_resolved` | the question (with its answer once resolved) | the run asks the operator something; it was answered, expired or withdrawn |
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
- **What needs approval:** decided by the permission policy (see `/settings/permissions`). With the standard policy that is a `triage` job with a cloud model (kind `send-code-to-cloud-model`), because source code would be sent to a third party; local-model triage, scans and assessments do not. In `ask` mode every job does. This is the mechanism Phase 3 will use for intrusive actions, and agents for individual tool calls (PLAN.md §6.5).

### `/settings/permissions` — how much runs without asking

A **permission policy** says which kinds of action wait for the operator. It has a mode and a list of kinds that are always allowed:

| Kind | What it covers | `default` mode | `ask` mode |
|---|---|---|---|
| `read-files` | chat tools that read the project | runs | asks |
| `run-job` | starting a scan, assessment or triage | runs | asks |
| `run-plan` | carrying out a multi-step plan (a `review` job) — asked so you see the plan first | asks | asks |
| `edit-files` | chat tools that create or change files | asks | asks |
| `run-commands` | the chat `bash` tool | asks | asks |
| `send-code-to-cloud-model` | sending source code to a cloud model provider | asks | asks |

A kind in `allowedKinds` never asks, in either mode — that is "always allow", an explicit decision that is recorded in the audit log and can be withdrawn by removing the kind.

- **Scopes:** each user has a policy (the standard one — `default`, nothing always allowed — until they change it). An engagement can have its own (`engagementId` on the request), which then replaces the user's for runs under that engagement; `DELETE` removes it.
- **Jobs:** the backend applies the policy when a job is submitted. If anything the job does still needs approval, it waits in `AwaitingApproval` with one request covering all of it (`kinds`). `POST /approvals/{id}/approve` with `{ "remember": true }` approves and adds those kinds to "always allow" for the job's engagement, or for the user if it has none. When a question is skipped because of "always allow", the audit log says so (`approval.auto`).
- **Chat:** the tools run on the operator's machine, so the CLI enforces the same policy there: it reads `GET /settings/permissions`, asks before a tool call that needs it, and stores an "always" answer with `PUT`. `GET` also returns every kind with whether it currently asks, for display.

### `/memory` — what carries over between runs

| Method | Path | |
|---|---|---|
| GET | `/memory` | Everything remembered (`?engagementId=` for what applies under one engagement) |
| POST | `/memory` | Add `{ kind?: "note" \| "instruction", text, engagementId?, targetPath? }` → 201 |
| DELETE | `/memory/{id}` | Forget one entry |

- **Instructions** are the operator's standing orders ("report in English", "always say what you did not read"). Only the operator can write them, and they apply to every target.
- **Notes** are facts worth keeping. The operator writes them through the API; the SAST agent writes them with its `remember` tool (at most 5 per run, 500 characters each, no duplicates). A note may be about one target (`targetPath`), in which case only runs on that target get it.
- **Who gets what:** a job's agent gets the user's entries plus those of its engagement, and of the notes those about its target or about no target. The chat agent gets the instructions and the notes about no target. They are appended to the system prompt (the 30 most recent of each).
- **Trust:** nothing is ever read from the target as an instruction — a repository under review is untrusted input. A note written by an agent is stored with `source: "agent"` and the job that wrote it, and is handed to later runs marked `[model]` with the warning that it may be wrong or planted by the code that model was reading: a hint to check, not an instruction. The operator can list and delete any entry; additions and deletions are in the audit log (`memory.add`, `memory.delete`).
- Limits: 2000 characters per entry, 200 entries per user.

### `/questions` — the run asks, the operator answers

A running job that needs a decision it should not make alone puts a **question** to the operator and waits. The job moves to `AwaitingInput`, its time limit is paused (waiting for a person does not time a job out), and the run's stream carries a `question` event with the text and the options. `POST /questions/{id}/answer` with `{ "selected": ["<option label>"] }` (and `text` where the question allows free text) wakes the job, which carries on in `Running`; the stream shows `question_resolved` with the answer. Asking and answering are audited (`question.ask`, `question.answer`).

- **Validation:** the answer must use the question's options (matched case-insensitively), exactly one unless the question is multi-select; anything else is a 400 and the job keeps waiting. A question is answered once (409 afterwards).
- **No answer:** after `CERBERUS_QUESTION_TIMEOUT_SECONDS` (default 3600) the question becomes `Expired` and the job continues with its stated safe default, recorded as a `note` on the stream.
- **Stopped while waiting:** cancelling the job or aborting its engagement ends it as `Cancelled` and closes the question as `Cancelled`.
- **Restart:** a job waiting for an answer is in memory; after a server restart it is marked `Failed` and its question `Cancelled` (unlike approvals, which wait in the database before a job starts).
- **What asks today:** a `scan` job whose target excludes files with `.cerberusignore` or suppresses findings with `cerberus:ignore` asks whether to honour that — *"Honour them"* (default) or *"Scan everything"*. Decide up front with `"exclusions": "honour" | "ignore"` on the job to skip the question; the synchronous `POST /scan` has nobody to ask and takes `honour` (default) or `ignore`. A target that excludes nothing is not asked about.

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

Because markers and ignore files live in the target, a target can hide findings from the totals — which is why they are counted and returned rather than dropped. For code you do not control, scan with `"exclusions": "ignore"`: every file is scanned and nothing is suppressed. A scan *job* asks you about this when the target actually excludes something (see `/questions`).

**Measured** (static pass only): the upstream RabbitMQ repository — 3,512 files in about 3 seconds, 251 candidates of which 82 are in production code (mostly shell commands, credentials in example URLs, `binary_to_term`, MD5 use); this repository's own code — 153 files, 2 candidates, both sample strings in test files (the repo's `.cerberusignore` excludes the vendored `security-skills/` material, the rule definitions and the rule tests).


### LLM triage of findings (`triage` job)

`POST /jobs {"type":"triage","scanId":"…","model":"…","maxFindings"?: 15,"includeTestsAndDocs"?: false}` asks a model to review a stored scan's findings one by one. For each it gets the rule that fired and about 12 lines of code either side, and must answer with a JSON verdict — `TruePositive`, `FalsePositive` or `NeedsReview` — plus its reasoning and a fix. Opinions are saved on the scan as they arrive and returned by `GET /scans/{id}` under `triage` (keyed by `findingId`).

- **Advisory only.** The code under review is untrusted input to the model and may try to talk it into "false positive", so an opinion never removes a finding and never changes a compliance verdict. The system prompt tells the model to treat the source as data, but that is a mitigation, not a guarantee.
- **You choose the model; there is no default.** A local Ollama model keeps the code on the machine. With a cloud model the job first waits for your approval (see `/approvals`), showing how many findings and files would be sent; only then is the code context sent to that provider, after credential-shaped values are replaced by `[REDACTED len=N]`. The audit log records which model reviewed how many findings and where the code went.
- **What is reviewed:** unsuppressed static findings in production code, most severe first, up to `maxFindings` (max 100). Dependency findings are skipped. An unusable or failed model reply becomes `NeedsReview` with `modelAnswered: false`.
- **Status:** tested with a scripted model only. It has **not** been run against a real model yet — see PLAN.md §17 "Pending verification" for the steps and the expected results.

### Reviews: plan → approve → execute (`review` job)

`POST /jobs {"type":"review","path":"…","catalogs"?: ["owasp-top10"],"model"?: "…","sca"?: true,"exclusions"?: "ask"}` is the first orchestrated run: one job that scans the target, optionally has a model review the findings, assesses the result against each catalog (all installed ones if none are named), and writes a report.

- **The plan comes first.** The job is parked in `AwaitingApproval` with an approval of kind `run-plan` whose detail is the numbered plan — each step, what it does, and whether anything leaves the machine. The plan is built by rules from the request, so what you approve is exactly what runs. "Always allow" for `run-plan` skips the question (recorded in the audit log as `approval.auto`). With a cloud `model`, the same approval also covers `send-code-to-cloud-model`.
- **One run, one task list.** The run emits a `plan` event and every step as `pending`, then each step `running` → `completed` in order. Progress, findings, model text and operator questions of the parts appear on the same stream; the parts' own internal steps do not.
- **The result** is a review (`resultId`): `GET /reviews/{id}` gives the scan id, assessment ids and the report; `GET /reviews/{id}/report` returns the report as `text/markdown`. The scan and assessments are ordinary stored ones and can be opened, exported and answered as usual. The report states its limits: findings are candidates, scores cover auto-assessed controls only, model opinions are advisory.
- **With agents:** add `"agentModel": "…"` (and optionally `maxSteps`, `"agents": ["sast","threat-model","evidence"]`) and the plan gains a step per agent, run by that model: the SAST agent (the default when `agents` is left out), the threat-model agent, and the evidence agent after each catalog's assessment. A cloud `agentModel` makes the plan's approval cover `send-code-to-cloud-model` too. The review's result carries `agentRunId` and `threatModelRunId`. Three agents in one job can outlast the default job time limit with a real model; raise `CERBERUS_JOB_TIMEOUT_SECONDS` if so.
- **One report, kept honest:** the model's findings get their own section, marked as candidates; they are not merged into the scan and change no compliance result. Scanner and model findings are correlated by place (same file, within two lines): the scanner's table says whether the model reported it too, the model's table says whether the scanner flagged it, flagged it but the target suppressed it, or the model found it alone. The threat model appears in short (counts and the top threats) with a pointer to the full one; evidence suggestions are counted per catalog and change nothing until accepted.
- **Not yet:** the plan is not written by a model and cannot be edited before approval; steps run one after another.

### The SAST agent (`sast` job) — a model reads the code

`POST /jobs {"type":"sast","path":"…","model":"…","maxSteps"?: 20,"instructions"?: "…"}` starts the first model-driven agent: the model explores the code under `path` on its own and reports the vulnerabilities it finds.

- **The loop** (`Agents/AgentLoop.cs`): the model is called, the tools it asks for are run here, their results go back, and so on until it answers without a tool call or reaches `maxSteps` (default 20, max 50). Its text is streamed (`text` events); every tool call is on the run stream as `tool_call { id, name, input }` followed by `tool_result { id, name, output, truncated, isError }`. A bad call (unknown tool, invalid input, a refused path) is answered to the model as a failure so it can correct itself.
- **Tools** — all read-only: `listDirectory`, `readFile` (numbered lines, 400 per call), `grep` (regex, time-bounded, 100 matches), `searchSkills` / `loadSkill` (passive tier), and `reportFinding`. File tools are confined to the target directory: no absolute paths, no `..`, no links followed; dependency and build directories are skipped by `grep`.
- **Findings** are accepted only at a file and line that exist; the evidence is the line itself. They are stored with the run (`GET /agent-runs/{id}`), marked `confidence: "model"`, and are **not** a scan: they never count in a compliance verdict. The code is untrusted input and may try to steer the model; the system prompt tells it to treat file contents as data, and nothing it can call changes anything.
- **You choose the model; there is no default.** A local model keeps the code on the machine. A cloud model needs approval first (`send-code-to-cloud-model`: the agent decides what to read, so anything under the path may be sent), and credential-shaped values are masked in everything it reads.
- **Steering:** while the agent runs, `POST /jobs/{id}/messages {"text":"…"}` tells it something — "look at the upload handler", "stop, that is enough". The message is put in front of the model before its next step, marked as the operator's and as taking priority over its plan; if the agent was just finishing, it gets one more step to act on it. It appears on the stream as `operator_message` and in the audit log (`job.message`). Messages are held in memory for the run in progress only; a job with no running agent answers 409.
- **A stopped run keeps its results:** cancelled, failed, timed out or at its step limit, the run is stored with `complete: false` and what it had found. Each run is in the audit log (`agent.run`) with where the code went.
- **The `echo` dev model** acts out a fixed mini-review (list, read up to five top-level files, flag a few well-known calls) so the pipeline can be tried without a model. It is not a review.
- **Status:** tested with scripted stand-ins only. It has **not** been run against a real model yet — see PLAN.md §17 "Pending verification".

### The threat-model agent (`threat-model` job)

`POST /jobs {"type":"threat-model","path":"…","model":"…","maxSteps"?: 25,"instructions"?: "…"}` has a model build a threat model of the code under `path`.

- **What it records:** components with `recordComponent` (kind: entry-point, process, data-store, external-service or trust-boundary) and threats with `recordThreat` (a STRIDE category, the component it is against, likelihood and impact as low / medium / high). A threat must name a component already recorded; code references must exist in the target; risk is computed from likelihood × impact (low, medium, high, critical), not chosen by the model.
- **The result** is an agent run (`GET /agent-runs/{id}`: `findings.components`, `findings.threats`) with a written report (`GET /agent-runs/{id}/report`, Markdown): summary, components, threats most severe first, each with its mitigation and code references. The report says what it is — a model's hypothesis from reading code, to be reviewed by people who know the system; deployment, network and process are not visible in code.
- **Stream:** `threat_component` and `threat` events as they are recorded, besides the tool calls.
- **Same agent machinery as the SAST agent:** confined read-only tools, steering, memory, approval and masking for cloud models, a stopped run keeps a partial model marked as such, `agent.run` in the audit log.
- **The `echo` dev model** records one component and one placeholder threat, to exercise the pipeline.
- **Status:** tested with scripted stand-ins only; not yet run against a real model (PLAN.md §17).

### The evidence agent (`evidence` job) — suggested answers for manual controls

Most controls cannot be judged by the scanner (31 of 39 in ESAP, 5 of 10 in OWASP Top 10). `POST /jobs {"type":"evidence","assessmentId":"…","model":"…","maxControls"?: 8,"maxSteps"?: 30,"controlIds"?: […]}` has a model look in the assessed code for evidence and suggest an answer for them.

- **Which controls:** those named in `controlIds`, or by default the first `maxControls` (max 20) that the engine left not assessed and the operator has not answered.
- **What it may say:** one of the catalog's response-scale values, or "Cannot tell". A definite answer (anything but "Not applicable" and "Cannot tell") is accepted only with evidence — files and lines that exist in the target; the line's text is stored with it. The prompt tells the model that controls about an organization's processes cannot be shown by code and should get "Cannot tell".
- **Advisory:** suggestions are stored beside the assessment (`suggestion` on each control in `GET /compliance/{id}`) and change no verdict, score or operator answer. `POST /compliance/{id}/suggestions/{controlId}/accept` makes one the operator's answer — validated like any answer, so a non-compliant one still needs an `actionPlan` in the body; "Cannot tell" cannot be accepted. `DELETE /compliance/{id}/suggestions/{controlId}` dismisses one. Acceptance is audited (`evidence.accept`), each run as `evidence.run`.
- **Same agent machinery as the SAST agent:** confined read-only tools, tool calls and `suggestion` events on the run stream, steering with `/jobs/{id}/messages`, memory, approval and masking for cloud models. The job's result id is the assessment.
- **The `echo` dev model** answers "Cannot tell" for the first control, to exercise the pipeline.
- **Status:** tested with scripted stand-ins only; not yet run against a real model (PLAN.md §17). Suggestions are not in the spreadsheet export yet.

### Per-user API keys

Each user stores their own Anthropic/OpenAI keys via `/settings/keys`. Keys are **encrypted at rest** with ASP.NET Core Data Protection (keyring under `dp-keys/`, or `CERBERUS_KEYRING`), never returned by the API, and decrypted in-memory only to authorize a `/chat` request — taking precedence over the `*_API_KEY` env vars. Ollama models need no key.

### `/chat` — the chat agent

`POST /chat` with `{ id, messages, mode, model }` produces one reply of the agent, streamed in the **AI-SDK UI-message stream** format (`text/event-stream`, `x-vercel-ai-ui-message-stream: v1`, chunks as `data: {json}\n\n`, ending with `data: [DONE]`), so `@ai-sdk/react`'s chat client — the one the CLI uses — consumes it unchanged.

- **Request:** `id` is the session; `messages` are UI messages (`{ id, role, parts }`), at least one; `mode` is `BUILD` or `PLAN`; `model` is a supported model id. Anything else is a 400 `Invalid request body`; an unknown session is a 404. An incoming message replaces a stored one with the same id, otherwise it is appended.
- **Stream:** `start` → per model step `start-step` … `finish-step` → `finish`. Within a step: `text-start/-delta/-end`, `reasoning-start/-delta/-end`, and for each tool call `tool-input-start`, `tool-input-delta*`, `tool-input-available` (or `tool-input-error`). The final `finish` carries `messageMetadata` with `mode`, `model`, `durationMs` and `usage` (`inputTokens`, `outputTokens`, `totalTokens`) when the provider reports it. A model failure is an `error` chunk.
- **Client tools** — `readFile`, `listDirectory`, `glob`, `grep`, and in BUILD mode also `writeFile`, `editFile`, `bash` (the same contracts as the TypeScript server). The server only describes them and relays the call; the reply ends, the CLI runs the tool in the project it was started in, and resubmits the same assistant message with the result. The server then **continues that message** (same `messageId`). Nothing is stored while calls are pending — the client holds the message.
- **Server tools** — `searchSkills` and `loadSkill` run in the backend within the same reply, marked `providerExecuted` so the client neither runs nor waits for them; their result arrives as `tool-output-available` / `tool-output-error` and the model is called again (at most 8 steps per reply).
- **Providers:** Anthropic through the official C# SDK (streaming, tool use, adaptive thinking with summaries; thinking blocks go back unchanged with their signatures after a tool call), OpenAI and Ollama through Chat Completions (tools, usage, and the reasoning fields some servers stream). The `echo` model is a rule-following stand-in for development, not a language model.

### Skills library

The vendored library under `security-skills/skills` (818 skills) is indexed at startup from each skill's front matter. Agents reach it in two ways: the chat agent calls `searchSkills` / `loadSkill`, and the triage reviewer is pointed at the skills matching each finding (the result records which). Access follows the capability tier: skills in the library's offensive subdomains need tier T2, which cannot be granted yet, so an agent at the passive tier neither finds nor loads them. `GET /skills` lists the whole catalog with the tier each skill needs; `GET /skills/{name}` returns the text only for skills a grantable tier may load.

### Parity with the TypeScript server

Checked feature by feature against `packages/server`:

| TypeScript server | .NET backend |
|---|---|
| `POST /auth/register`, `/login`, `/logout` — same bodies, status codes and messages; 30-day session tokens | ✅ same (tested) |
| `GET/POST /sessions`, `GET /sessions/:id` — same shapes; 400 on a missing title; 404 for another user's session | ✅ same (tested) |
| `POST /chat` request validation (id, ≥1 message, mode enum, supported model) | ✅ same; `mode` is also accepted in lower case |
| Stored messages merged with incoming ones by id | ✅ |
| Tools by mode (4 read-only in PLAN, 7 in BUILD), executed by the CLI | ✅ same names, descriptions and input schemas |
| Reply continues the resubmitted assistant message; not stored while tool calls are pending | ✅ |
| System prompt with modes, tool list and rules | ✅ ported, plus the security framing and skills tools |
| `messageMetadata`: mode, model, durationMs, usage | ✅ usage has `inputTokens`, `outputTokens`, `totalTokens` (the AI SDK's extra usage fields are not filled) |
| Model list (Anthropic, OpenAI, Ollama) | ✅ same ids, plus `echo` and `codestral` |
| Anthropic thinking on Opus/Sonnet (fixed 10,000-token budget) | 🟡 adaptive thinking with summaries instead — the fixed budget is deprecated on those models. **Not yet run against the real API.** |
| OpenAI reasoning summaries on `gpt-5.4` (via the Responses API) | ⬜ not ported: the backend uses Chat Completions, which does not return them. Replies and tool calls work; the reasoning text is not shown. |
| Model keys from the environment | ✅ plus per-user encrypted keys |
| JSON error bodies (`{ "error": … }`) for bad input and unexpected failures | ✅ |
| Strict validation of every message part against the tool schemas | 🟡 lighter: messages need an id, a role and parts; tool inputs are checked to be JSON objects for a known tool |

Beyond parity: server-run tools, per-user keys, and everything under scans, compliance, jobs, approvals, questions, engagements, audit and skills.

Accounts are not shared between the two servers (each has its own database); the CLI simply registers on whichever it talks to.

## Status & follow-ups

- The CLI's login, sessions, chat with tool calls, `/scan` and `/runs` all work against this backend. Chat was verified by driving the running backend with the CLI's own AI SDK chat client and local tools, using the `echo` stand-in.
- **Not verified against real models** (no model may be run on the development machine): tool calling and thinking with Anthropic, tool calling with OpenAI and Ollama, and LLM triage. The provider code is tested against canned streams in the providers' documented formats. See PLAN.md §17 "Pending verification".
- **Seen once, not explained:** `SkillApiTests.Skills_reserved_for_intrusive_tiers_are_listed_but_not_handed_out` failed one time while opening the SQLite connection during user registration; 18 further full runs passed. If it recurs, capture the inner `SqliteException` — the suspects are lock contention with a background job of an earlier test, or the shared test database file.
- **Follow-ups:** OpenAI reasoning summaries (Responses API); authentication on the `/hub/chat` SignalR skeleton (unused by the CLI); moving the model list to current Claude models.

## Tests

```bash
dotnet test Cerberus.slnx
```

`tests/Cerberus.Tests` hosts the real API in-process (`ApiIntegrationTests.cs`, `ChatTests.cs`: the chat agent with client and server tools, auth and sessions as the TypeScript server answered them; jobs end to end, the jobs hub, the run event stream (order, replay, live delivery, model text), approvals, permission modes, operator questions (answer, expiry, cancel, paused time limit), engagement scope and abort, audit, cross-user isolation; every model provider is replaced by a scripted stand-in, so tests never call a real model) and unit-tests the compliance engine's verdict rules, the shipped catalogs (they load, ids are unique, mappings use known scanner categories), operator-answer validation, the spreadsheet export/import round trip, the engagement scope policy, the audit hash chain, the job executor (cancel, time limit, hard stop), message bus and state rules, and proof-of-control verification (with DNS and HTTP faked; one test uses a real local listener to check the loopback guard). Every scanner rule has positive and negative cases (`ScannerRuleTests.cs`), alongside tests for suppression, ignore files, file filtering and symlinks. Not covered yet: the chat and API-key endpoints, the compliance response/import endpoints over HTTP, and SCA (it needs OSV.dev). `AgentTests.cs` covers the agent loop (tool results fed back, bad calls, step limit), the code tools (confinement, links, masking, grep), the finding checks, and the `sast` job end to end. It also covers steering (a message reaches the model before its next step; only a running agent takes one) and a review that dispatches the agent. `MemoryTests.cs` covers memory: validation and privacy, which entries reach an agent run and chat, the agent's own notes reaching the next run on the same target only, and the limits on what an agent may save. `EvidenceTests.cs` covers the evidence agent: which controls it looks at, the checks on a suggestion, that a run changes nothing about the assessment, and accepting or dismissing a suggestion. `ThreatModelTests.cs` covers the threat-model agent: the checks on components and threats, the risk rating, the report, and the job end to end. `ReviewTests.cs` covers the review job: the plan, its approval (alone, always allowed, and together with a cloud model), the step sequence on the stream, the report, validation and privacy.
