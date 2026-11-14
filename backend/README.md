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
- `CERBERUS_RATE_PER_ADDRESS` / `CERBERUS_RATE_PER_USER` — requests per minute allowed from one network address (default `1200`) and with one sign-in token (default `600`); `0` switches a limit off. See "Protection of the service".
- `CERBERUS_MAX_REQUEST_BYTES` — largest request body accepted (default 16 MB).
- `CERBERUS_CORS_ORIGINS` — comma-separated origins a browser client may call from, e.g. `https://app.example.com`. Not set: no cross-origin access.
- `CERBERUS_SKILLS_ROOT` / `CERBERUS_CATALOG_ROOT` — where the skills library and control catalogs are (default: the repository's `security-skills/skills` and `catalogs`).

Or in a container, from the repository root (see `docker-compose.yml`; written but not yet run):

```bash
docker compose up -d
```

### Protection of the service

- **Settings are checked before start-up.** A setting that is present but cannot be understood stops the service (exit code 2) with a message naming the setting — never its value, which may be an address with a password in it. Switches are exact: `true` / `false`, `on` / `off`. Before this, `CERBERUS_REQUIRE_ENGAGEMENT=True` silently meant *off*.
- **Request rate.** Each request must fit two allowances over a sliding minute: the network address it comes from, and the sign-in token it carries. Over either: `429` with `Retry-After` and a JSON error. `/health` is never limited (a container's liveness probe must not be refused). A made-up token has its own allowance, so it cannot use up a real user's. Limits are counted in this process; behind a reverse proxy the address is the proxy's, so set the per-address limit accordingly or leave rate limiting by client address to the proxy.
- **Request size.** A body over `CERBERUS_MAX_REQUEST_BYTES` is refused with `413`, whether or not it declared its length.
- **Headers** on every response, errors included: `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`, `Content-Security-Policy: default-src 'none'; frame-ancestors 'none'`, `Cache-Control: no-store`; no `Server` header. The API serves data, never a page.
- **Browser origins.** None by default. `CERBERUS_CORS_ORIGINS` lists the origins that may call the API from a browser; a wildcard is refused at start-up.
- **Per-user data has two locks.** Every query filters by the signed-in user; in addition the request's database context is restricted — to no one until the token is checked, then to that user — so a query that forgot its filter still returns only that user's rows, and a write to another user's row is refused. Background services (job and chat workers, audit log, scheduler) open their own, unrestricted scope: they are told the user by their caller. Code that must look past the lock inside a request says so with `IgnoreQueryFilters()`.
- **TLS** is not terminated by the service: it listens on plain HTTP on `localhost`. Put a reverse proxy that terminates TLS in front of it for anything beyond one machine.

## Endpoints

| Method | Path | Auth | Notes |
|---|---|---|---|
| GET | `/health` | none | Liveness: the process answers. Checks nothing |
| GET | `/health/ready` | none | Readiness: each dependency's state; `503` when something required is missing |
| POST | `/auth/register` | — | `{ email, password }` → `{ token }` |
| POST | `/auth/login` | — | `{ email, password }` → `{ token }` |
| POST | `/auth/logout` | Bearer | Revokes the token |
| GET | `/sessions` | Bearer | List sessions (helpers' conversations are not listed) |
| GET | `/sessions/{id}` | Bearer | Session + parsed messages |
| POST | `/sessions` | Bearer | `{ title, parentId? }` → created session (the title is kept as one line); with `parentId`, a helper's conversation under that session |
| POST | `/sessions/{id}/title` | Bearer | `{ model? }` → the model names the session from its first prompt |
| PUT | `/sessions/{id}/title` | Bearer | `{ title }` → the operator names it |
| POST | `/sessions/{id}/fork` | Bearer | `{ title?, through? }` → a copy of the session, whole or up to the reply `through` |
| GET | `/sessions/{id}/export` | Bearer | The conversation as a Markdown file (`text/markdown`) |
| GET, PUT, DELETE | `/settings/permissions` | Bearer | The permission policy (`?engagementId=` for one engagement's override): `{ mode?, allowedKinds? }` |
| GET | `/skills` | Bearer | The skills library catalog (`?q=&subdomain=&limit=`), with the tier each skill needs |
| GET | `/skills/{name}` | Bearer | One skill's text (403 for skills reserved for a tier that cannot be granted) |
| GET | `/settings/keys` | Bearer | Which providers have a key set (never returns the key) |
| PUT | `/settings/keys` | Bearer | `{ provider, apiKey }` — store a key (encrypted at rest) |
| DELETE | `/settings/keys/{provider}` | Bearer | Remove a stored key |
| GET | `/jobs/{id}/artifacts` · `/artifacts/{id}` | Bearer | What a run kept besides its findings (screenshots): the list, and one as a download |
| GET | `/mcp/servers` | Bearer | The configured MCP servers: whether each answers, the tools agents get, the tools withheld and why (`?refresh=true` asks the servers again) |
| GET | `/models` | Bearer | What can be chosen as a model: each provider, whether it is ready, its models, how to name any other |
| POST | `/chat` | Bearer | The chat agent, answered as the response: AI-SDK UI-message stream with tool calls; body `{ id, messages, mode, model }` |
| POST | `/chat/turns` | Bearer | The same request, queued: 202 with `{ turnId, sessionId, state, ahead }`; the reply comes over `/hub/chat` and from the endpoints below |
| GET | `/chat/turns` · `/chat/turns/{id}` | Bearer | The caller's turns (`?sessionId=`, `?active=true`) · one turn: `{ state, error, replyStored, lastSequence, … }` |
| GET | `/chat/turns/{id}/events` | Bearer | The reply's pieces after `?after=<sequence>`: `{ state, lastSequence, events: [{ sequence, chunk }], more }` |
| POST | `/chat/turns/{id}/cancel` | Bearer | Withdraw a waiting turn, or stop one that is being answered |
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
| POST | `/jobs` | Bearer | Submit a background job `{ type?: "scan" \| "assess" \| "triage" \| "review" \| "sast" \| "threat-model" \| "investigate" \| "evidence" \| "index", path?, sca?, engagementId?, catalogId?, catalogs?, scanId?, assessmentId?, model?, maxFindings?, maxSteps?, maxControls?, controlIds?, instructions? }` → 202 with the job |
| GET · POST | `/schedules` | Bearer | List recurring runs · create one `{ name?, every, firstRunAt?, job }` (`job` as for `POST /jobs`) |
| GET · PUT · DELETE | `/schedules/{id}` | Bearer | One schedule · change `{ name?, every?, enabled? }` · remove |
| POST | `/schedules/{id}/run` | Bearer | Fire it now, leaving its rhythm as it is |
| POST, DELETE | `/compliance/{id}/suggestions/{controlId}/accept` · `/compliance/{id}/suggestions/{controlId}` | Bearer | Accept a model's suggested answer as the operator's (body: `{ justification?, actionPlan?, timeLimitDays? }`) / dismiss it |
| PUT | `/scans/{id}/findings/decision` | Bearer | The operator's decision on one finding `{ findingId, status: "accepted-risk" \| "false-positive", note }` |
| GET, DELETE | `/decisions` · `/decisions/{id}` | Bearer | All decisions / withdraw one |
| GET, DELETE | `/code-index` · `/code-index/{id}` | Bearer | The semantic indexes built with `index` jobs / remove one |
| POST | `/code-search` | Bearer | Search an indexed target by meaning `{ path?, query, limit? }` |
| GET | `/scans/{id}/sarif` · `/agent-runs/{id}/sarif` · `/reviews/{id}/sarif` | Bearer | Findings as a SARIF 2.1.0 file (`application/sarif+json`) |
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
| WS | `/hub/chat` | Bearer | SignalR hub: pushes the caller's own turns — `chatEvent { turnId, sessionId, sequence, chunk }` for each piece of a reply, `chatTurn` when a turn starts or ends. (Also `Stream({ model, system?, messages })`: a model's plain text with the caller's keys, no tools, nothing stored) |

### Health, readiness and the server's other modes

- **`/health`** is liveness and checks nothing, on purpose: a slow database must not get the process restarted.
- **`/health/ready`** says whether the service can do its work: `{ status, checkedAt, checks: [{ name, status, detail }] }`. Required dependencies — `database` (opens, and has its schema), `key-ring` (writable; it is created on a fresh installation), `scan-root` (exists), `job-worker` (started, and not stopped on an error) — make the service `unhealthy` (HTTP 503) when they fail. Optional ones — `catalogs`, `skills`, `embeddings` (the embedding server lists the model; nothing is loaded or embedded) — make it `degraded` (HTTP 200) and say what is unavailable. A check is cut off after 3 s; one that throws is a failed check, reported by exception type only; results are kept for 5 s. No sign-in is needed, so details never hold a path or an address.
- **`dotnet Cerberus.Api.dll health [url]`** asks the service on this machine (`PORT`, or the given address) and exits 0 when it is ready (healthy or degraded) and 1 when it is unhealthy or silent, printing what is not healthy. It is the container's healthcheck, so the image needs no curl.
- **`dotnet Cerberus.Api.dll smoke-llm <model>`** tests a model the way the agents use it: one request with a tool on offer, through the real provider stack and agent loop. It passes when the model calls the tool with valid arguments and its answer contains a random word only the tool's result could have told it. Keys come from `ANTHROPIC_API_KEY` / `OPENAI_API_KEY`; exit code 0, 1, or 64 for an unknown model. Use it after configuring a local model or a key, before trusting an agent run.

### Recurring runs (`/schedules`)

A schedule is a stored job request and an interval. Firing it submits the request **as its owner through the same code as `POST /jobs`** (`JobEndpoints.SubmitAsync`), so validation, engagement scope and the permission policy apply each time, and a run that needs approval waits in `AwaitingApproval` like any other. Nothing about consent is stored with a schedule.

- **What can be scheduled:** `scan`, `review`, `sast`, `threat-model`, `index`, `assess` (with a path). Not `triage` or `evidence`, which work on one scan or one assessment. `every` is a number of minutes or `"45m"`, `"6h"`, `"1d"`, `"1w"`: 15 minutes to 30 days. At most 20 schedules per user. The first run is one interval after creation unless `firstRunAt` says otherwise.
- **Firing:** `SchedulerService` looks for due schedules every 30 s. A due run is claimed by a conditional update of `NextRunAt` (only the caller that moves it on fires it), so several instances of the service do not fire it twice. The next time is the next point on the schedule's own grid after now: after downtime it fires once.
- **What a firing can come to** (`lastOutcome`): `Started.`; `Waiting for your approval: …`; `Skipped: the previous run …` (still running, queued, waiting for approval or for an answer — runs do not pile up); `Refused: …` with the server's reason. Three refusals in a row pause the schedule. Each firing is in the audit log (`schedule.run`), as are creation, changes and removal.
- **Pause and resume** (`enabled`): resuming does not fire for the time it was paused; changing `every` starts a new interval from now.

### The loop guard

Limits on an agent run enforced in code (`LoopGuard`), for the SAST, threat-model and evidence agents: the same call — same tool, same arguments, in whatever order or spacing they were written — runs at most twice and is then answered with a note instead of being run; a run has a budget of tool calls (five per step of its step limit, at least 20), and calls beyond it are not made; three steps in a row in which every call was repeated or failed stop the run. A run stopped by the guard, like one that reaches its step limit, gets one last turn without tools to say what it did and did not get to; the run is stored as incomplete and its stream carries the reason.

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
- **The queue** (`IMessageBus`): a message is `WorkMessage(id, userId)` — which work and whose, nothing else — on `cerberus.jobs` or `cerberus.chat-turns`; a worker checks the user against the stored work, and acknowledges the message only when the work has reached a final state. In one process by default (`InProcMessageBus`); with `CERBERUS_RABBITMQ_URL=amqp://user:password@host:5672/` on RabbitMQ (`RabbitMqBus`: durable queues, persistent messages, publisher confirms, manual acknowledgement; a broker that is away loses nothing — publishes are kept and retried, consumers reconnect). Readiness reports it as `queue`. The RabbitMQ side is written and not yet verified against a broker (PLAN.md §17).
- **Restarts:** on startup, jobs that were `Running` are marked `Failed` and jobs still `Queued` are published again — whatever the queue: a second message for a job is harmless, because a job that is no longer queued is not started.

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

**Command rules.** Besides kinds, the settings hold `allowedCommands`: prefixes of shell commands ("git status", "npm run build") that the chat agent may run without asking. `PUT /settings/permissions { allowedCommands: [...] }` replaces the list; `GET` returns it. The CLI decides whether a command line is covered (it is the one that runs it) and adds a rule when the operator answers "always" to a command. The server stores the rules, normalises their spacing, and refuses any that is not a plain prefix or whose command can run or destroy anything (`rm`, `sudo`, shells and interpreters, `curl`, `find`, `xargs`, …) — the same list the CLI never offers.

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

**Built artefacts.** A file that is an ELF or PE executable or library (recognised by its signature, whatever its name; up to 128 MB) is not read as text: its headers are checked for hardening the build left out, and it is counted in `binariesInspected`. ELF: `binary.elf.no-pie`, `binary.elf.exec-stack` (marked executable, or not declared at all), `binary.elf.no-relro` / `binary.elf.partial-relro`, `binary.elf.no-stack-protector` (low confidence — a binary may have no function that needs a canary). PE: `binary.pe.no-aslr` (also when relocations were stripped), `binary.pe.no-dep`, `binary.pe.no-cfg` (not asked of managed .NET assemblies), `binary.pe.no-high-entropy-va`. These findings have category `binary` (CWE-693, OWASP A05) and line 1; a header flag is the build's statement, not a measurement of the running process. Nothing is executed and no external tool is started. Mach-O is not read yet, and object files (`.o`) are not checked. Build-output directories are skipped as before, so point the scan at `dist/` or `bin/` itself to check what is in it.

**What a release exposes.** Built text and debug files are not read by the line rules (they stay in `filesSkipped`) but are checked for what they give away, and counted in `releaseFilesInspected`. Category `release` (OWASP A05):

- `release.source-map-sources` (medium) — a source map carrying the original source in `sourcesContent`; `release.source-map` (low) — one that only names the sources. Both for a `.map` file and for a map embedded as a `data:` URL at the end of a `.js` / `.mjs` / `.cjs` / `.css` file.
- `release.debug-symbols` (low) — a `.pdb` (recognised by its signature) or a `.dSYM` bundle (reported once).
- `release.build-path` (low) — a home-directory path of the build machine (`/home/x/…`, `/Users/x/…`, `C:\Users\x\…`) inside an ELF / PE binary or among a source map's sources. Example names (`user`, `Public`, …) and paths that are the tail of a longer one (a URL route) are not reported.
- `release.credential.<kind>` — a token-shaped credential (AWS key id, private key, GitHub, Slack, Google, Stripe) in a minified bundle (`.min.js`, `.min.mjs`, `.min.css`) or a source map, wherever it is on the line. One finding per kind and line with the number of occurrences; the evidence is masked. The looser secret rules are not applied to minified code. A source map's sources are searched decoded.

Files over 16 MB are not read. Not covered: Electron fuses, paths stored as UTF-16, credentials inside binaries. `release` findings are not sent to triage and do not feed a compliance control.

**Dependencies from built artefacts.** The dependency check reads what shipped as well as what the manifests declare, and checks both against OSV as one de-duplicated inventory (`manifestsScanned` counts artefacts that yielded dependencies too):

- `*.deps.json` beside a published .NET application — its NuGet packages.
- A Go binary (ELF, PE or Mach-O) — the modules the linker recorded, with replace directives applied, and the toolchain as the package `stdlib`.
- A `.jar` / `.war` / `.ear` — the `pom.properties` inside it and inside the archives nested one level down (a Spring Boot or WAR layout).

A dependency finding's `fixedVersion` is the lowest fixed version above the one in use: an advisory lists one fix per maintained branch, and the first listed may be on an older branch. When nothing listed is above the version in use, none is suggested.

Nothing is unpacked or executed; files over 128 MB are not read. A library that left no metadata behind (a shaded JAR without `META-INF/maven`, a statically linked C library, a JavaScript bundle) is not seen. As for the scan, build directories are skipped unless they are the target.

**What is not scanned**, and how it is reported: dependency and build directories (`node_modules`, `.git`, `bin`, `obj`, `dist`, `vendor`, …); other binary, media, archive, lock and minified files, files over 1 MB and lines over 2,000 characters (`filesSkipped`); and paths listed in a `.cerberusignore` at the scan root or in the scanned directory (`filesIgnored`). Ignore patterns follow `.gitignore` conventions: `dir/`, `*.ext` at any depth, `a/b.py` anchored, `**` across directories. A scan stops at 5,000 findings and sets `truncated`.

Because markers and ignore files live in the target, a target can hide findings from the totals — which is why they are counted and returned rather than dropped. For code you do not control, scan with `"exclusions": "ignore"`: every file is scanned and nothing is suppressed. A scan *job* asks you about this when the target actually excludes something (see `/questions`).

**Measured** (static pass only): the upstream RabbitMQ repository — 3,512 files in about 3 seconds, 251 candidates of which 82 are in production code (mostly shell commands, credentials in example URLs, `binary_to_term`, MD5 use); this repository's own code — 153 files, 2 candidates, both sample strings in test files (the repo's `.cerberusignore` excludes the vendored `security-skills/` material, the rule definitions and the rule tests).


### LLM triage of findings (`triage` job)

`POST /jobs {"type":"triage","scanId":"…","model":"…","maxFindings"?: 15,"includeTestsAndDocs"?: false}` asks a model to review a stored scan's findings one by one. For each it gets the rule that fired and about 12 lines of code either side, and must answer with a JSON verdict — `TruePositive`, `FalsePositive` or `NeedsReview` — plus its reasoning and a fix. Opinions are saved on the scan as they arrive and returned by `GET /scans/{id}` under `triage` (keyed by `findingId`).

- **Advisory only.** The code under review is untrusted input to the model and may try to talk it into "false positive", so an opinion never removes a finding and never changes a compliance verdict. The system prompt tells the model to treat the source as data, but that is a mitigation, not a guarantee.
- **You choose the model; there is no default.** A local Ollama model keeps the code on the machine. With a cloud model the job first waits for your approval (see `/approvals`), showing how many findings and files would be sent; only then is the code context sent to that provider, after credential-shaped values are replaced by `[REDACTED len=N]`. The audit log records which model reviewed how many findings and where the code went.
- **What is reviewed:** unsuppressed static findings in production code, most severe first, up to `maxFindings` (max 100). Dependency, binary-hardening and release-exposure findings are skipped. An unusable or failed model reply becomes `NeedsReview` with `modelAnswered: false`.
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
- **Tools** — all read-only: `listDirectory`, `readFile` (numbered lines, 400 per call), `grep` (regex, time-bounded, 100 matches), the code map (`codeMap`, `outline`, `dependents` — below), `searchSkills` / `loadSkill` (passive tier), and `reportFinding`.
- **The code map** (`Agents/CodeMap.cs`) is built once per run, without a model, from patterns in the source: which file imports which (JS/TS, Python, Java/Kotlin, Go, Ruby), what each file defines, and where requests come in (route declarations in JS/TS, Python, C#, Java and Go). `codeMap` gives the overview — request routes, entry points (files nothing imports; test files apart), the files most others depend on. `outline` lists a file's functions, classes, types and routes with line numbers, so an agent reads the part it needs. `dependents` shows what a file imports, who imports it, and everything that depends on it through others — the blast radius of a problem. It is a guide and says so: an import it cannot resolve (a package, a C# namespace) is simply absent, and for code with no resolvable imports the overview says that instead of showing empty lists. File tools are confined to the target directory: no absolute paths, no `..`, no links followed; dependency and build directories are skipped by `grep`.
- **Findings** are accepted only at a file and line that exist; the evidence is the line itself. They are stored with the run (`GET /agent-runs/{id}`), marked `confidence: "model"`, and are **not** a scan: they never count in a compliance verdict. The code is untrusted input and may try to steer the model; the system prompt tells it to treat file contents as data, and nothing it can call changes anything.
- **You choose the model; there is no default.** A local model keeps the code on the machine. A cloud model needs approval first (`send-code-to-cloud-model`: the agent decides what to read, so anything under the path may be sent), and credential-shaped values are masked in everything it reads.
- **Steering:** while the agent runs, `POST /jobs/{id}/messages {"text":"…"}` tells it something — "look at the upload handler", "stop, that is enough". The message is put in front of the model before its next step, marked as the operator's and as taking priority over its plan; if the agent was just finishing, it gets one more step to act on it. It appears on the stream as `operator_message` and in the audit log (`job.message`). Messages are held in memory for the run in progress only; a job with no running agent answers 409.
- **A stopped run keeps its results:** cancelled, failed, timed out or at its step limit, the run is stored with `complete: false` and what it had found. Each run is in the audit log (`agent.run`) with where the code went.
- **The `echo` dev model** acts out a fixed mini-review (list, read up to five top-level files, flag a few well-known calls) so the pipeline can be tried without a model. It is not a review.
- **Status:** tested with scripted stand-ins only. It has **not** been run against a real model yet — see PLAN.md §17 "Pending verification".

### Semantic code search (`index` job, `/code-search`)

`grep` finds code by the words it uses; this finds it by what it does — "where are passwords checked", "code that builds SQL from input" — also when you do not know the words.

- **Indexing:** `POST /jobs {"type":"index","path":"…"}` cuts the target's source (and SQL, shell, YAML, Terraform, Markdown) into chunks at its definitions — a function or type with the comment above it; files without definitions in windows of 60 lines — and embeds each chunk with a **local** embedding model. Stored per chunk: file, lines, name, the file's content hash and the vector. The code itself is not stored; previews are read from disk when a search returns. Running the job again embeds only files whose content changed and drops what is gone.
- **The model** is whatever the local Ollama serves under `CERBERUS_EMBEDDING_MODEL` (default `nomic-embed-text`; `ollama pull nomic-embed-text`, 274 MB), reached at `CERBERUS_EMBEDDING_URL` (default: `OLLAMA_BASE_URL`, else `http://localhost:11434/v1`). Building an index sends every chunk to that server, so anything but this machine (or the compose service `ollama`) is refused unless `CERBERUS_EMBEDDING_ALLOW_REMOTE=true`. Credential-shaped values are masked before embedding.
- **Searching:** `POST /code-search {"path":"…","query":"…","limit"?: 8}` embeds the question with the same model and returns the closest chunks, best first: file, lines, name, score, a preview as the file is now, and `stale` when the file changed since it was indexed. A path below an indexed directory uses that index and returns only what lies under it. Vectors are compared in memory (a target has thousands of chunks, not millions), so there is no vector database to run.
- **Agents** working on a target that has an index get a `searchCode` tool and are told so; without an index nothing changes for them. A hit is a lead to read, not a finding — the tool's answer says so.
- **Measured** on this repository's `backend/src` with `nomic-embed-text`: 81 files, 990 chunks, 53 s to index, about 30 ms per search, 1 s to re-index when nothing changed. Of five test questions, four had the right code in the top three; closeness in meaning is approximate, which is why hits come with the code to check.

### The threat-model agent (`threat-model` job)

`POST /jobs {"type":"threat-model","path":"…","model":"…","maxSteps"?: 25,"instructions"?: "…"}` has a model build a threat model of the code under `path`.

- **What it records:** components with `recordComponent` (kind: entry-point, process, data-store, external-service or trust-boundary) and threats with `recordThreat` (a STRIDE category, the component it is against, likelihood and impact as low / medium / high). A threat must name a component already recorded; code references must exist in the target; risk is computed from likelihood × impact (low, medium, high, critical), not chosen by the model.
- **The result** is an agent run (`GET /agent-runs/{id}`: `findings.components`, `findings.threats`) with a written report (`GET /agent-runs/{id}/report`, Markdown): summary, components, threats most severe first, each with its mitigation and code references. The report says what it is — a model's hypothesis from reading code, to be reviewed by people who know the system; deployment, network and process are not visible in code.
- **Stream:** `threat_component` and `threat` events as they are recorded, besides the tool calls.
- **Same agent machinery as the SAST agent:** confined read-only tools, steering, memory, approval and masking for cloud models, a stopped run keeps a partial model marked as such, `agent.run` in the audit log.
- **The `echo` dev model** records one component and one placeholder threat, to exercise the pipeline.
- **Status:** tested with scripted stand-ins only; not yet run against a real model (PLAN.md §17).

### The team (`investigate` job) — a lead agent and its specialists

`POST /jobs { type: "investigate", path, model, maxSteps?, maxSpecialists?, instructions?, approvePlan? }`. A lead agent (`LeadAgentRunner`, `Agents/Team.cs`) reads the layout with the read-only code tools and has one tool for acting, `dispatch { assignments: [{ specialist, focus }] }`.

- **Plan → approve → execute.** A dispatch is first checked by code (known specialists; a focus of 20–600 characters; no assignment given twice; at most `maxSpecialists` in the whole run, 1–6, default 4; at most three plans put to the operator). Then the plan is put to the operator as the run's question (`AwaitingInput`, `/questions`): *Run the plan*, *Do not run it*, or free text. Text without *Run the plan* goes back to the lead as a change request and it must ask again; no answer in time dispatches nobody. The plan is not asked about when the job was submitted with `approvePlan: true` or the operator's permissions always allow `run-plan` — both are noted on the run with the plan, and audited (`plan.auto`, `plan.approved`, `plan.refused`, `plan.unanswered`).
- **Specialists** (`Team.Specialists`): `code-review`, `auth`, `injection`, `secrets-crypto` are the SAST agent with one paragraph added to its instructions and the lead's focus as its task; `threat-model` is the threat-model agent. Each runs in a service scope of its own, from its assignment alone, with `maxSteps` steps (default 20). A specialist has no `dispatch` tool, cannot ask the operator, does not read the job's messages and does not use the internet (`research` on an `investigate` job is a 400). One that fails is noted and the others carry on.
- **Side by side.** With an API model up to `CERBERUS_AGENT_PARALLELISM` specialists (default 2, at most 4) work at once; with a local model one at a time. Their events share the run's stream, each marked `agent: "<label>"` (`auth`, `auth-2` for a second of a kind), their text on channels of their own, and each has a step in the plan.
- **One list.** `FindingMerger` merges the specialists' findings without a model: the same file and line is one finding; within two lines it is one when the CWE matches, or — where one names none — the category. The merged finding takes the words and severity of the most severe report and lists who reported it (`reportedBy`).
- **The result** is the lead's agent run (`agent: "lead"`): the merged findings, the lead's summary and a Markdown report (`/agent-runs/{id}`, `/report`, `/sarif`). `GET /agent-runs/{id}` of a lead lists its `specialists`; each specialist's own run carries `parentRunId` and its unmerged findings. Kept when the run is stopped part-way, marked incomplete.
- **Consent.** With a cloud model the job waits for `send-code-to-cloud-model` first, and the request says that a lead and up to N specialists will read the code.
- **The `echo` dev model** lists the top directory, proposes the injection and the access-control specialist, proposes the injection specialist alone when asked for changes, and reports the merged count.
- **Status:** tested with stand-in specialists and with the dev model end to end; parallel work is tested with stand-ins only (the dev model counts as local); never led by a real model (PLAN.md §17).

### The evidence agent (`evidence` job) — suggested answers for manual controls

Most controls cannot be judged by the scanner (31 of 39 in ESAP, 5 of 10 in OWASP Top 10). `POST /jobs {"type":"evidence","assessmentId":"…","model":"…","maxControls"?: 8,"maxSteps"?: 30,"controlIds"?: […]}` has a model look in the assessed code for evidence and suggest an answer for them.

- **Which controls:** those named in `controlIds`, or by default the first `maxControls` (max 20) that the engine left not assessed and the operator has not answered.
- **What it may say:** one of the catalog's response-scale values, or "Cannot tell". A definite answer (anything but "Not applicable" and "Cannot tell") is accepted only with evidence — files and lines that exist in the target; the line's text is stored with it. The prompt tells the model that controls about an organization's processes cannot be shown by code and should get "Cannot tell".
- **Advisory:** suggestions are stored beside the assessment (`suggestion` on each control in `GET /compliance/{id}`) and change no verdict, score or operator answer. `POST /compliance/{id}/suggestions/{controlId}/accept` makes one the operator's answer — validated like any answer, so a non-compliant one still needs an `actionPlan` in the body; "Cannot tell" cannot be accepted. `DELETE /compliance/{id}/suggestions/{controlId}` dismisses one. Acceptance is audited (`evidence.accept`), each run as `evidence.run`.
- **Same agent machinery as the SAST agent:** confined read-only tools, tool calls and `suggestion` events on the run stream, steering with `/jobs/{id}/messages`, memory, approval and masking for cloud models. The job's result id is the assessment.
- **The `echo` dev model** answers "Cannot tell" for the first control, to exercise the pipeline.
- **Status:** tested with scripted stand-ins only; not yet run against a real model (PLAN.md §17). Suggestions are not in the spreadsheet export yet.

### Decisions on findings

A finding stays "open" until the operator decides otherwise: `PUT /scans/{id}/findings/decision` with `status` `accepted-risk` (real, lived with) or `false-positive` (not a problem here), and a `note` saying why — a decision without a reason is refused.

- **It follows the finding, not the scan.** A decision is stored against the finding's fingerprint — its rule, its file and the code on the line (for a dependency: the advisory, package and version), not the line number. Later scans of the same code show the decision even when lines above moved; when the flagged code itself changes, it is a new finding and open again. Two identical lines in one file share a decision.
- **Where it shows:** `GET /scans/{id}` gives every finding its `fingerprint` and `decision`; a review's report counts decided and open findings and lists the open ones first; the SARIF export marks a decided finding with an `external` suppression carrying the reason. `GET /decisions` lists them all; `DELETE /decisions/{id}` withdraws one.
- **What it does not do:** a decision does not remove the finding, change the scan's counts or a compliance verdict (answer the control for that), or skip the finding in triage. Only the operator can decide — a model's opinion stays an opinion — and only on scanner findings, not on a model's.
- Audited as `finding.decide` and `finding.undecide`, with the reason.

### SARIF export

`GET /scans/{id}/sarif`, `GET /agent-runs/{id}/sarif` (SAST agent runs only) and `GET /reviews/{id}/sarif` return the findings as a SARIF 2.1.0 log, the format code-scanning dashboards and many other tools import.

- **One SARIF run per source.** A scan is one run by the tool `Cerberus`, with a rule per scanner rule (or per advisory, for dependency findings). A SAST agent run is one run by `Cerberus SAST agent (<model>)`, with its findings grouped by the category the model chose and marked `source: "model"`. A review's file holds both runs side by side; they are never merged, so a reader can tell rules from a model's candidates.
- **Exact severity.** SARIF's `level` has three steps (`error`, `warning`, `note`), so each result also carries `properties.severity` (`critical` … `info`). The CLI's headless `run` reads it to decide whether a build fails.
- **Nothing is dropped.** Findings the target suppressed with a marker are exported with a SARIF `suppressions` entry (`inSource`); findings in test code or documentation carry `properties.context`; a model's triage opinion on a finding rides along as `properties.modelReview` and changes nothing about the finding.
- **Paths** are relative to the scan root (`uriBaseId: %SRCROOT%`) in both runs. Each result carries a stable fingerprint (`cerberusFindingId/v1`).
- **Severity:** `level` is error (critical, high), warning (medium) or note; `security-severity` is a fixed bucket per severity (9.5 / 8.0 / 5.5 / 3.0 / 0.0) so dashboards can sort — it is not a CVSS score.
- **Snippets are masked** where they look like credentials: an export is a file that gets passed around.

### Credentials found in the code under review

A scan that finds a credential must not become a second copy of it. Everything Cerberus keeps or passes on is masked by the secret rules' patterns (`[REDACTED len=N]`), so the credential stays only in the file it was found in:

- a finding's **evidence** — in the stored scan, the compliance matrix built from it, the job's events, the API and the SARIF export;
- what an **agent** read and reported: tool inputs and outputs on the run stream, the evidence of its findings and suggestions, its streamed text and its stored summary. A local model still receives the real file content; a cloud model receives masked content, as before;
- scans stored **before** this was introduced are masked when read through `GET /scans/{id}`; their rows, and old compliance matrices and run events, are not rewritten — re-scan to replace them.

Limits: only what a secret rule recognises is masked. Model text is masked chunk by chunk, so a credential a model quotes across a chunk boundary can slip through. The chat agent's file tools run on the operator's machine and its sessions are stored as sent.

### Sign-in throttling

Five failed sign-ins in a row for one account (within 15 minutes) lock sign-in for that account for 5 minutes: `POST /auth/login` answers 429 with `Retry-After`, also for the right password, so a guesser learns nothing during the lock. A successful sign-in clears the count. When an existing account is locked, its audit log gets an `auth.lockout` entry. The count is per account name and held in memory (a restart clears it); there is no per-address limit, since on a local install every client shares one address.

### Internet access for agents (`research`)

Off unless `CERBERUS_WEB=on`. With it on, a `sast` or `threat-model` job submitted with `research: true` gives its agent one tool, `research(question)`, after the operator's consent (`use-the-internet`, an ordinary approval; with a cloud model it is asked together with `send-code-to-cloud-model`). `research` on any other job, or on a server with web access off, is a `400`.

- **The clean room.** The agent that holds the code never gets internet tools. `research` checks the question, then runs a second agent loop that is given the question and nothing else — no code, no memory, no history — with `webFetch` and, if `CERBERUS_SEARXNG_URL` is set, `webSearch`. At most 3 questions a run and 8 steps a question. Every question sent is a note on the run; a refused one is a note that does not repeat it, and does not count.
- **The egress guard** (`EgressGuard`) is applied to the question and to every address and query the researcher sends. It refuses, never rewrites, and names the kind of thing found, never the thing: a credential (the scanner's secret rules), an e-mail address, an internal address (non-public IPv4 literals, `localhost`, `.internal`, `.local`, `.lan`, `.corp`, …), a name that belongs to the run (the engagement's name and its host and domain targets, the operator's e-mail address and its parts), a term from `CERBERUS_EGRESS_BLOCKED_TERMS`, text that looks like source code, or more than 400 characters. The URL-decoded form is checked as well.
- **The fetcher** (`SafeFetcher`) cannot be used to reach this network: http and https on standard ports only, no credentials in the address; the address a name resolves to is checked at the moment of connecting and the connection goes to exactly that address (non-public ranges of IPv4 and IPv6, including IPv4 carried in IPv6); redirects are followed by hand, five at most, each through the same checks; no cookies, no proxy, a fixed user agent; 20 s, 2 MB, text only. A page comes back as its words — scripts, styles and markup removed — cut at 20,000 characters.
- **The fence** (`Untrusted.Wrap`): pages, search results and the researcher's answer reach a model inside `<<<EXTERNAL_DATA … EXTERNAL_DATA>>>` with a line saying it is information to weigh and not instructions. Control characters are removed and fence markers inside the text are defused, so the text cannot end the fence.
- **The browser** (`CERBERUS_BROWSER=on`, on top of web access; `ChromeBrowser`): the researcher also gets `browse { url, scroll?, screenshot? }`. One headless Chrome or Chromium per visit — found in the usual places or at `CERBERUS_BROWSER_PATH` — with an empty profile in the temp directory that is deleted afterwards, driven over the DevTools protocol on a local port that exists for the visit; there is no browser library in the build. It is started with `EgressProxy` as its only route (loopback included, so that is refused too; QUIC and non-proxied WebRTC off): the proxy only tunnels — HTTPS on port 443 — resolves the name itself, and connects to that address only if it is a public one. So the page, its redirects and everything its scripts load obey the same rule as the fetcher, and a plain-http page cannot be opened at all. Downloads are denied. A visit is limited to 45 s, 10 screens of scrolling and 20,000 characters of text; pages are rendered in a light colour scheme whatever the machine's setting. The model gets the rendered text inside the fence; the screenshot (PNG, at most 4 MB, six a run) is stored with the run: `GET /jobs/{id}/artifacts` lists them, `GET /artifacts/{id}` returns one to its owner as a download. In a container the browser needs `CERBERUS_BROWSER_NO_SANDBOX=true` when the server runs as root, which gives up Chrome's own sandbox — run the server as a non-root user instead where possible. It only looks: no clicking, typing or form filling.
- **`dotnet Cerberus.Api.dll browser-check <url> [screens] [file.png]`** opens one page the same way, scrolls, says what it saw and what the page was not allowed to reach, and saves the screenshot if a file is named.
- **`dotnet Cerberus.Api.dll web-check <url>`** fetches one page the same way and says what came back or why not — to see that web access works, and what it refuses.

#### MCP servers (`CERBERUS_MCP_CONFIG`)

The researcher can also be given tools of outside servers that speak the Model Context Protocol. Which servers is the installation's decision: a JSON file, `{ "servers": [ { "name", "url", "headers"?, "tools"? } ] }` (at most 8; names of letters, digits and dashes; a header value `env:NAME` is taken from the environment). A broken file or entry configures less, never stops the server, and is reported by `mcp-check` and by the `mcp` readiness check.

- **The client** (`McpClient`) implements the Streamable HTTP transport of protocol revision 2025-06-18 (and 2025-03-26) directly: `initialize`, the `Mcp-Session-Id` and `MCP-Protocol-Version` headers, replies as one JSON object or as server-sent events, paged `tools/list`, `tools/call`, a new session when the server answers 404 for the old one, and a `DELETE` on shutdown. It declares no client capabilities, ignores the server's `instructions`, follows no redirect (the token would travel with it), keeps no cookies, uses plain http only for addresses that are not public, and stops at 30 seconds or 2 MB.
- **What agents see** (`McpExposure`): the tools named under `tools`; without that, only tools with `readOnlyHint: true` — the protocol's defaults make an unannotated tool potentially destructive, so it is withheld. A tool reaches a model as `mcp_{server}_{tool}` with the server's description cut to 300 characters, flattened, defused and labelled as the server's words; its input schema is copied with strings cut to 200 characters and nesting to 8 levels. At most 20 tools per server. `McpServers` keeps a server's list for 10 minutes (1 minute after a failure) and refuses a call to any tool that was not exposed.
- **Who gets them**: the clean-room researcher only (`ResearchTools`), never the agent that holds the code and not chat. Every string in a call's arguments — values and property names — goes through the `EgressGuard`; a refusal is a note on the run and nothing is sent. Each call is a note with its arguments. Results are text only (pictures, sound and files are named, not carried) inside the `EXTERNAL_DATA` fence; a JSON-RPC error's message stays in `McpException.Detail` for the operator and is not shown to a model.
- **Consent**: with servers configured, `research: true` involves `use-mcp-servers`, asked in the same approval as `use-the-internet` and `send-code-to-cloud-model` where those apply. Research is accepted with web access off when servers are configured; the researcher then has their tools only.
- **`dotnet Cerberus.Api.dll mcp-check [server [tool ['{"json":"arguments"}']]]`** reaches the servers and prints what agents would get and what is withheld; with a tool it makes one call through the same argument check. Exit 0 when every server answers.

Not supported: the stdio transport, OAuth sign-in to a server, resources and prompts, servers per engagement.

Search goes through a SearXNG instance the installation runs (the `web` profile of the compose file), because what agents search for says a lot about what is being reviewed. It has not been run here; the client is tested against canned answers.

### Models: any model of a supported provider

`ModelRegistry` decides which provider serves a model id. The listed models are an offer, not a limit:

- **A listed id** — `claude-opus-5-5` (the default), `claude-sonnet-5-5`, `claude-haiku-5-5`, `claude-fable-5-2`; `gpt-5.4`, `gpt-5.4-mini`, `gpt-5.4-nano`; `deepseek-chat`, `deepseek-reasoner`; local `llama3.1`, `qwen2.5-coder`, `deepseek-r1`, `codestral`; and `echo`, the built-in test model.
- **`provider:model`** — `anthropic:`, `openai:`, `deepseek:`, `ollama:` followed by the model as that provider knows it (`ollama:qwen2.5-coder:7b`, `openai:gpt-4.1`). The provider is sent only the part after the prefix.
- **A family that belongs to one provider** — `claude-…`, `gpt-…`, `chatgpt-…`, `o3…`.
- Anything else is refused, not guessed: a bare unknown name could be a local model or a slip, and the two differ in where code is sent. Ids are at most 200 characters of letters, digits and `._:/@+-`.

Providers: Claude through the official Anthropic SDK (adaptive thinking on 4.6 and later, read from the version in the id; older models are sent no thinking setting); OpenAI, DeepSeek (`https://api.deepseek.com/v1`, or `DEEPSEEK_BASE_URL`) and Ollama through one Chat Completions client, which also reads a model's separate reasoning stream. Keys come from the user's stored key, else `ANTHROPIC_API_KEY` / `OPENAI_API_KEY` / `DEEPSEEK_API_KEY` on the server. Only Ollama and the test model count as local: every other provider needs the `send-code-to-cloud-model` consent before a job sends code to it.

`GET /models` returns `{ default, providers: [{ id, name, kind: "api" | "local", ready, keySource?, reachable?, models, anyModel }] }`. An API provider is `ready` when the caller has stored a key (`keySource: "yours"`) or the server was started with one (`"server"`). The local provider lists what Ollama has installed — asked through its `/v1/models`, which loads nothing — each under an id that can be sent back; embedding models are left out; with Ollama down it says `reachable: false` and offers the listed names as suggestions.

**Never verified against a real provider:** no API key was used and no local chat model was run while building this. Routing, request shape and stream parsing are tested against canned streams; see PLAN.md §17.

### Per-user API keys

Each user stores their own Anthropic, OpenAI and DeepSeek keys via `/settings/keys`. Keys are **encrypted at rest** with ASP.NET Core Data Protection (keyring under `dp-keys/`, or `CERBERUS_KEYRING`), never returned by the API, and decrypted in-memory only to authorize a `/chat` request — taking precedence over the `*_API_KEY` env vars. Ollama models need no key.

### A conversation over the queue (`/chat/turns`, `/hub/chat`)

How the CLI talks to the chat agent. The request is the same as for `/chat`; what differs is how it travels (PLAN.md §22):

1. **Accepted.** `POST /chat/turns` checks the request, stores it as a turn under the user id of the bearer token — a `userId` in the body is ignored — and publishes `(turnId, userId)` on the queue. The answer is `202` at once.
2. **Answered.** `ChatTurnWorker` takes the message, refuses it if the user does not own the turn, and writes the reply with the same `ChatService` the direct endpoint uses. One turn of a conversation at a time, in the order they were sent; at most `CERBERUS_CHAT_PER_USER` (2) of one user's turns at once out of `CERBERUS_CHAT_CONCURRENCY` (4); at most 20 waiting per user (then `429`); `CERBERUS_CHAT_TURN_TIMEOUT_SECONDS` (900) per turn.
3. **Delivered.** Every chunk of the reply — the same chunks `/chat` streams — gets the turn's next sequence number, is pushed to the user's group on `/hub/chat` as `chatEvent`, and is stored (`TurnDelivery`: text fragments in batches of 32 or 250 ms, everything else at once; all of it before `chatTurn` announces the end).
4. **Caught up.** A client that was not connected, or sees a gap in the sequence, reads `GET /chat/turns/{id}/events?after=n`. It also returns what is written but not stored yet.

A reply that stops at a tool the client runs ends its turn with `replyStored: false`; the client runs the tool and sends the next turn with the result, as with `/chat`. A stopped or failed reply stores nothing in the session. Turns and their events are kept for 24 hours. One worker process only: the ordering and the places are in memory, and a turn interrupted by a restart is failed, not resumed.

### `/chat` — the chat agent

`POST /chat` with `{ id, messages, mode, model }` produces one reply of the agent, streamed in the **AI-SDK UI-message stream** format (`text/event-stream`, `x-vercel-ai-ui-message-stream: v1`, chunks as `data: {json}\n\n`, ending with `data: [DONE]`), so `@ai-sdk/react`'s chat client — the one the CLI uses — consumes it unchanged.

- **Request:** `id` is the session; `messages` are UI messages (`{ id, role, parts }`), at least one; `mode` is `BUILD` or `PLAN`; `model` is a supported model id. Anything else is a 400 `Invalid request body`; an unknown session is a 404. An incoming message replaces a stored one with the same id, otherwise it is appended.
- **Stream:** `start` → per model step `start-step` … `finish-step` → `finish`. Within a step: `text-start/-delta/-end`, `reasoning-start/-delta/-end`, and for each tool call `tool-input-start`, `tool-input-delta*`, `tool-input-available` (or `tool-input-error`). The final `finish` carries `messageMetadata` with `mode`, `model`, `durationMs` and `usage` (`inputTokens`, `outputTokens`, `totalTokens`) when the provider reports it. A model failure is an `error` chunk.
- **Client tools** — `readFile`, `listDirectory`, `glob`, `grep`, and in BUILD mode also `writeFile`, `editFile`, `bash` (the same contracts as the TypeScript server). The server only describes them and relays the call; the reply ends, the CLI runs the tool in the project it was started in, and resubmits the same assistant message with the result. The server then **continues that message** (same `messageId`). Nothing is stored while calls are pending — the client holds the message.
- **What the client says about its project** — the optional `context` of a request: `{ environment: { workingDirectory, platform, date, isGitRepository, gitBranch }, instructions: { file, content, truncated } }`. It becomes an "Environment" section and a "Project instructions" section of the system prompt, after the mode and tool guidance and before memory. Every field is length-limited; one-line facts are flattened to one line, so a value cannot start a section of its own; anything missing or misshapen is simply left out.
- **Tool contracts** — `readFile` takes `offset` and `limit` and returns numbered lines in pages; `editFile` takes `replaceAll`; `bash` has a 2-minute default timeout (10 at most). The descriptions tell the model that line numbers are not part of the file and that long output is saved to a file it can read.
- **Helpers (`task`)** — when a chat request says `subagents: true` (the client can run helper conversations), the agent is offered a client tool `task { description, prompt }` and told when delegating is worth it. The client answers a `task` call by running a helper: it creates a child session (`POST /sessions { title, parentId }`), sends the prompt to `/chat` on that session, runs the helper's tool calls, and returns the helper's last message as the tool's output (`{ report, toolCalls, complete, note? }`). A child session is treated differently in three ways, all decided here and not by the request: it always gets the read-only tool set (PLAN), it is never offered `task`, and its system prompt says it works for another agent and that its last message is its report. A parent must be the caller's own session and not itself a child. Child sessions are left out of `GET /sessions`; `GET /sessions/{id}` returns `parentId`. The server cannot run a helper itself because the chat agent's file tools run on the operator's machine.
- **Fork and export** — `POST /sessions/{id}/fork` creates a new session of the caller's own with a copy of the messages: all of them, or up to and including the reply named by `through` (it must be an assistant message — a copy ending on a prompt would hold a question nobody answered). The title is the original's with " (fork)", or `title`. A compaction summary is carried over only when the copy still contains the message it reaches up to; the agent's task list only with a whole copy. The original is not changed, and a fork of a helper's conversation is an ordinary session. `GET /sessions/{id}/export` returns the conversation as Markdown (`cerberus-chat-<id>.md`): a heading per message (you, a command you ran, a note to the agent, the assistant with its model and mode), the assistant's text as written, and one list item per tool call — what it was about, how it went, the diff of an edit (first 60 lines), the end of a command's output (last 20 lines), a helper's report. Everything is passed through the secret masker, reasoning parts are left out, the contents of files the agent read are not reproduced, and code fences are made longer than any run of backticks inside them, so tool output cannot break out of its block.
- **Session titles** — `POST /sessions/{id}/title` asks a model (the one that wrote the last reply, unless `model` names another) for a title of at most six words for a conversation that begins with the session's first prompt; a command the operator ran or a note added for them is not a prompt. The prompt is sent as material to name, not as an instruction, and at most 2,000 characters of it. The reply becomes one plain line: a reasoning model's `<think>` block, a "Title:" label, quotes, markdown and a final full stop are dropped, control characters are removed, anything shaped like a credential is masked, and it is cut at a word to 60 characters. Nothing usable, a failing provider or a model that takes longer than a minute is a `502` and the title stays as it was. Only the title column is written, so a reply being stored at the same moment is not overwritten. `PUT /sessions/{id}/title` sets a title by hand (one line, at most 100 characters). The client asks once, after the first reply of a new session.
- **Long conversations** — when a new user message would take the conversation past 80% of the model's context window, its earlier part is first summarised by the same model (fixed headings: objective, requirements, decisions, work state, next move, relevant files, important context). From then on the model gets that summary in its system prompt and, in full, only the messages after it; the last exchange is always kept word for word. The session keeps every message (`summarizedThrough` marks where the summary ends) and the client is told with a transient `data-compaction` part. `POST /sessions/{id}/compact` does the same on demand (`{ model? }`; 409 when there is nothing to summarise). The size is an estimate (characters / 4 plus a fixed part), the window a conservative figure per provider — 200k Anthropic, 128k OpenAI, **8k for local models**, since Ollama runs a short window unless configured; set `CERBERUS_CONTEXT_TOKENS` to the window you actually run with.
- **A busy provider** — a call that fails for a passing reason before any output (rate limit, overload, 5xx, dropped connection, timeout) is retried up to 5 times, waiting 2 s doubling to 30 s or what `Retry-After` asks (a request to wait more than a minute is reported instead). The client is told with a transient `data-retry` part (`attempt`, `maxAttempts`, `seconds`, `reason`); a job's run gets a note. A 4xx, a missing key, cancellation, and a failure after output has started are not retried. Applies to every model call: chat, triage and the agents.
- **Several messages in one request** — a request may carry more than one new message; they are merged into the session in order. The CLI uses this for a command the operator ran themself with `!`: its record goes along as a user message (with `metadata.shell` so the client can show it again) right before the prompt, and the model reads both as the user's words.
- **The agent's task list** — `updateTasks` is a server tool in both modes. The model sends the whole list each time (`{ tasks: [{ id?, content, status, dependsOn? }] }`, statuses `pending`, `in_progress`, `completed`, `cancelled`, at most 30). The server refuses a list that does not hold together — duplicate ids, a dependency that is not in the list or goes in a circle, a task started or finished before what it depends on is completed — and the refusal goes back to the model as a failed call. A good list is stored with the session (`tasks` in `GET /sessions/{id}`), returned with `next` (the tasks that can start now) and a one-line `summary`, and from then on included in the system prompt of every request, so the model still has it after a compaction. It is kept as soon as it changes, also when the reply is waiting for client tools.
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
- **Follow-ups:** OpenAI reasoning summaries (Responses API); moving the model list to current Claude models.

## Tests

```bash
dotnet test Cerberus.slnx
```

`tests/Cerberus.Tests` hosts the real API in-process (`ApiIntegrationTests.cs`, `ChatTests.cs`: the chat agent with client and server tools, auth and sessions as the TypeScript server answered them; jobs end to end, the jobs hub, the run event stream (order, replay, live delivery, model text), approvals, permission modes, operator questions (answer, expiry, cancel, paused time limit), engagement scope and abort, audit, cross-user isolation; every model provider is replaced by a scripted stand-in, so tests never call a real model) and unit-tests the compliance engine's verdict rules, the shipped catalogs (they load, ids are unique, mappings use known scanner categories), operator-answer validation, the spreadsheet export/import round trip, the engagement scope policy, the audit hash chain, the job executor (cancel, time limit, hard stop), message bus and state rules, and proof-of-control verification (with DNS and HTTP faked; one test uses a real local listener to check the loopback guard). Every scanner rule has positive and negative cases (`ScannerRuleTests.cs`), alongside tests for suppression, ignore files, file filtering and symlinks. Not covered yet: the chat and API-key endpoints, the compliance response/import endpoints over HTTP, and SCA (it needs OSV.dev). `AgentTests.cs` covers the agent loop (tool results fed back, bad calls, step limit), the code tools (confinement, links, masking, grep), the finding checks, and the `sast` job end to end. It also covers steering (a message reaches the model before its next step; only a running agent takes one) and a review that dispatches the agent. `MemoryTests.cs` covers memory: validation and privacy, which entries reach an agent run and chat, the agent's own notes reaching the next run on the same target only, and the limits on what an agent may save. `EvidenceTests.cs` covers the evidence agent: which controls it looks at, the checks on a suggestion, that a run changes nothing about the assessment, and accepting or dismissing a suggestion. `ThreatModelTests.cs` covers the threat-model agent: the checks on components and threats, the risk rating, the report, and the job end to end. `ChatHubTests.cs` covers the chat hub: no session, no stream; a signed-in caller gets the model's text; bad requests are refused before any model is called. `SarifTests.cs` covers the SARIF export: rules and results, suppressed findings, masking of credentials, dependency findings, the model's run beside the scanner's, and the three endpoints. `HardeningTests.cs` covers that a credential found in code appears in no response, stored event or database row, and the sign-in lock. `DecisionTests.cs` covers finding decisions: the fingerprint, a decision following its finding across scans and lapsing when the code changes, its place in the report and the SARIF export, and withdrawal. `ChatContextTests.cs` covers the project context a chat request may carry: its place in the prompt, its limits, and that malformed context is ignored. `RetryTests.cs` covers retrying a busy provider (what is retried and what is not, the waits, the notices); `CompactionTests.cs` covers compaction of long chats (the cut, the summary request, what the model gets afterwards, the on-demand endpoint). `PermissionTests.cs` also covers command rules: stored normalised, returned with the policy, and refused when dangerous. `CodeMapTests.cs` covers the code map over a small polyglot project: import resolution per language, dependents, entry points, routes, outlines, and the three tools. `TaskListTests.cs` covers the chat task list: the checks on a list, what can start next, its storage and its place in the prompt. `ShellRecordTests.cs` covers a prompt that brings along the record of a command the operator ran. `BrowserTests.cs` covers the browser's side: the outgoing proxy (refusals by kind, a real tunnel to a local listener, noise), the rules for an address, the browse tool (fenced text, the kept screenshot and its limit, refusals) and the stored screenshots end to end with a stand-in browser — the real browser is exercised by `browser-check`, not by the test suite. `ChatTurnTests.cs` covers the queue and the conversation over it: acknowledgement, the broker message format and what is rejected, a broker that is away; and through the API a turn accepted, answered and replayed, pushes to the right user and to nobody else, an unauthenticated connection, another user's turns and sessions, order within a conversation, stopping a waiting and a running turn, a reply continued after a client tool, a conversation removed meanwhile, a queue message naming the wrong user, a reply the model could not finish, and readiness. `TeamTests.cs` covers the team: the merging rules, the dispatch tool (what a plan may contain, the limits across rounds, a plan that is changed, refused or unanswered, how many specialists work at once, one breaking off, stopping), the marking of a specialist's events, the report, and through the API whole runs with the plan approved, revised, refused, approved in advance and always allowed. `McpTests.cs` covers MCP servers: the configuration file and what is refused in it, which tools are exposed and how their descriptions and schemas are cut down, the client against a small server on this machine that follows the transport (JSON and event-stream replies, sessions and their loss, redirects, refusals, limits), the argument guard and the fence of the agent tool, `mcp-check`, and through the API the consent, a run whose researcher calls a server's tool, and research with the internet off — a real server is exercised by `mcp-check`, not by the test suite. `WebAccessTests.cs` covers internet access: what the egress guard refuses and lets through, which addresses count as public, the fence, the fetcher against a local server (text extraction, redirects, limits, nothing of the caller sent, refusal of private addresses before and at connecting), the web tools, the clean room (the researcher gets only the question; what it sends is checked again; the question limit), the search client, and the job's consent end to end. `ModelSelectionTests.cs` covers model selection: which provider serves which id (listed, prefixed, by family, refused), the provider getting the model under its own name, DeepSeek's address, key and stream, adaptive thinking by model version, the listing endpoint with installed local models and stored keys, and that code sent to DeepSeek needs the cloud consent. `ScheduleTests.cs` covers recurring runs: intervals, the grid after downtime, creation and its refusals, firing once when two instances notice, approval and "always allow" working as for any run, skipping behind an unfinished run, pausing after refusals, pause/resume and run-now. `LoopGuardTests.cs` covers the loop guard (the limits themselves, and the agent loop stopping early with the model's own account). `HealthTests.cs` covers readiness (required vs optional, a throwing and a hanging check, caching, nothing sensitive in the report), the two endpoints, and the `health` and `smoke-llm` modes. `SubagentTests.cs` covers helpers: when the `task` tool is offered, child sessions (ownership, depth, listing), and that a helper gets read-only tools and its own instructions whatever the request asks for. `SessionForkExportTests.cs` covers forking (whole and to an earlier reply, what is carried over, independence from the original) and the Markdown export (each kind of message and tool call, masking, fences, shortening, times). `SessionTitleTests.cs` covers session titles: what a model's reply is cut down to, which message a session is named after, the request the model gets, and the two endpoints. `SemanticSearchTests.cs` covers semantic search with a model-free stand-in for the embedder: chunking, the index job, search and its scoping, re-indexing only what changed, the locality check, and the agents' tool. `ReviewTests.cs` covers the review job: the plan, its approval (alone, always allowed, and together with a cloud model), the step sequence on the stream, the report, validation and privacy.
