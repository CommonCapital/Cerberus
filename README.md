<div align="center">

<br />

<h1>Cerberus</h1>

<p><strong>An autonomous, multi-agent security assurance platform.</strong></p>

<p>Cerberus scans a target system for vulnerabilities in code <em>and</em> system design, grades it against enterprise-grade security frameworks, and — under strict authorization — actively attempts to break it ("aggressive hacking: break &amp; solve") and proposes fixes.</p>

<p>Fully local and self-hostable: no proprietary SaaS. Local database, local sandbox, own authorization, and your choice of cloud LLMs (with your own API keys) or local models via Ollama.</p>

<br />

</div>

> **Status: active rewrite.** Cerberus is being built on a de-clouded terminal-agent chassis. See **[PLAN.md](PLAN.md)** for the full architecture and roadmap. This README reflects what exists today.

---

## What it is

Cerberus runs an **engagement** against a **target** (a repo, a running service, an infrastructure description) through three phases, all bound by an enforced authorization & safety spine (scope, verified target ownership, capability tiers, rate caps, kill switch, audit):

1. **Scan & Detect** — SAST, dependency/CVE analysis, secret detection, and LLM-driven system-design/threat-model review (CodeQL-class).
2. **Enterprise Compliance** — grade the target against rigid control catalogs and produce an auditable compliance matrix with action plans and deadlines. A built-in, vendor-neutral catalog ships at [`catalogs/enterprise-sap.json`](catalogs/enterprise-sap.json).
3. **Aggressive Hacking ("break & solve")** — authorized, scoped exploitation and resilience testing, then automated remediation. Built last, gated by the authorization spine.

Agents are powered by a vendored library of **818 framework-mapped cybersecurity skills** under [`security-skills/`](security-skills/) (MITRE ATT&CK / NIST CSF / D3FEND / OWASP, Apache-2.0).

> **Scope & authorization:** Cerberus is for systems you own or are explicitly authorized to test. Active and destructive capabilities are tied to verified target ownership and in-scope engagements, enforced in code — not a tool for attacking third parties.

## Current state

| Area | Status |
|---|---|
| TypeScript CLI | ✅ chat, plus `/scan`, `/review`, `/sast` and `/runs` against the .NET backend |
| TypeScript server (`packages/server`) | 🗄 superseded by the .NET backend, which has all of its features; kept for reference |
| .NET backend (ASP.NET Core + EF Core + SignalR) | ✅ **the backend**: auth, sessions, the chat agent with tool calling, scans, compliance, jobs, approvals, questions, engagements, audit, skills — see [`backend/README.md`](backend/README.md) |
| Sandbox, agent fleet, web frontend | 📋 planned — see [PLAN.md](PLAN.md) |

## Repository layout

```
packages/        # TypeScript baseline (de-clouded)
  cli/           #   OpenTUI + React terminal client; runs project tools locally
  server/        #   Hono API: local auth, sessions, AI streaming
  database/      #   Prisma schema + client (local SQLite)
  shared/        #   Shared Zod schemas, tool contracts, model registry
backend/         # .NET backend (the target server; replaces packages/server)
catalogs/        # Compliance control catalogs as data (ESAP, + planned OWASP/ISO/SOC2/NIST)
security-skills/ # Vendored 818 cybersecurity skills + framework mappings (Apache-2.0)
PLAN.md          # Full architecture and build roadmap
```

## Getting started

### Prerequisites

- [Bun](https://bun.sh) (for the TypeScript CLI + server)
- [.NET 10 SDK](https://dotnet.microsoft.com) (for the backend)
- Optional: [Ollama](https://ollama.com) for fully-local models, and/or your own Anthropic/OpenAI API keys

### 1. Configure

```bash
cp .env.example .env
```

Everything defaults to local. `DATABASE_URL` points at a local SQLite file; add `ANTHROPIC_API_KEY` / `OPENAI_API_KEY` only if you want cloud models (otherwise use Ollama).

### 2. The backend (.NET)

```bash
cd backend && dotnet run --project src/Cerberus.Api
```

It listens on `http://localhost:3000` and creates its SQLite database on first run. This is the one backend: the CLI needs nothing else.

### 3. The CLI

```bash
bun install
```

```bash
bun run dev:cli
```

The CLI signs in to the backend automatically (a local account is created on first `/login`). Chat runs its file and shell tools on your machine, in the directory the CLI was started in — and **asks first** before a tool that edits files or runs a command: `y` allows it once, `a` always allows that kind of action, `n` refuses (the model is told). `/permissions` shows the current policy, switches between *default* and *ask* (everything asks, including reads), and withdraws an "always allow".

> **Which server is the backend?** The .NET one. The CLI uses a single address (`API_URL`, default `http://localhost:3000`). The original TypeScript server in `packages/server` speaks the same auth, session and chat API and still runs (`bun run --cwd packages/database db:push`, then `bun run dev:server`), but it is superseded: the .NET backend has every one of its features (see the parity table in [`backend/README.md`](backend/README.md)) and all the security features exist only there. Do not run both on port 3000.

### Or: everything in containers

```bash
docker compose up -d
```

```bash
docker compose run --rm cli
```

`docker-compose.yml` runs the backend, with the CLI, Ollama, RabbitMQ and Redis as opt-in profiles. **These files were written without starting Docker** — expect small fixes on the first run (PLAN.md §15).

### 4. Security runs from the CLI

With the backend and the CLI running, `/login`, and:

- **`/scan`** — type a path under the backend's scan root; the scan starts as a background job and its run opens.
- **`/review`** — type a path; Cerberus shows its plan (scan, compliance assessments against every catalog, report) and waits. `a` approves, `r` approves and stops asking about plans, `d` denies. The run then shows each step as it goes and the report at the end.
- **`/sast`** — type a path; the model chosen with `/models` reads that code (read-only) and reports vulnerabilities. You see each file it opens and each search it makes as it goes. With a cloud model the run first asks for approval, because the code it reads is sent to the provider. Its findings are a model's candidates, kept apart from scans. While it works, press `m` to tell it something ("look at the upload handler", "stop here"); it reads your message before its next step.
- **`/engagement`** — choose the engagement your runs belong to, or type a name to create one. Everything you start afterwards (`/scan`, `/review`, `/sast`) runs under it, and the backend refuses a target that is not in its scope. The choice is remembered and shown in the status bar; "No engagement" goes back to unscoped runs (unless the server requires one).
- **`/scope`** — the directories the current engagement may be run on. Type a path to add it — doing so is your statement that you own that code or are authorized to test it. With nothing typed, Enter removes the highlighted one.
- **`/abort`** — the kill switch: after one confirmation, stops every queued and running job under the current engagement and ends it for good.
- **`/memory`** — what Cerberus remembers between runs: your standing instructions, your notes, and notes agents left (marked as a model's). Type to add one and choose note or instruction; with nothing typed, Enter forgets the highlighted entry.
- **`/runs`** — recent runs; those waiting on you are marked. Opening one replays it and follows it live.

On a run's screen you see its state, task list, progress and findings as they arrive (and, for triage, the model's output and verdicts). When the run needs you, the prompt appears in place: `a` / `d` to approve or deny, `↑` `↓` and `enter` to answer a question. `c` cancels a running job, `esc` goes back (the run carries on).

```bash
bun run --cwd packages/cli test
```

Runs on `http://localhost:3000` (configurable via `PORT`), creates its SQLite schema on first run, and exposes the same local-auth / sessions / streaming-chat contract. See [`backend/README.md`](backend/README.md).

## Local-first by design

No Clerk, no Polar, no hosted Postgres. Authorization is self-hosted (email/password sessions), the database is local SQLite, models run via your own keys or local Ollama, and (per the roadmap) tool execution runs in a self-hosted sandbox. Azure is an optional production deploy target behind the same interfaces, never a requirement to run.

## License

The vendored `security-skills/` library is Apache-2.0 (see `security-skills/LICENSE`); its attribution is preserved.
