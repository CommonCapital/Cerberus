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
| TypeScript CLI | ✅ chat, plus `/scan`, `/findings`, `/review`, `/sast`, `/threatmodel`, `/index`, `/assess`, `/assessments` and `/runs` against the .NET backend |
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

The CLI signs in to the backend automatically (a local account is created on first `/login`). Chat runs its file and shell tools on your machine, in the directory the CLI was started in — and **asks first** before a tool that edits files or runs a command: `y` allows it once, `a` always allows it, `n` refuses (the model is told). For a file edit, "always" means that kind of action; for a shell command it means **that command** — `a` on `git status -s` remembers `git status`, not every command. A command line runs without asking only if every command in it is one you allowed; anything with redirection, substitution or a subshell, anything that names a secrets file, and commands such as `rm`, `sudo`, `curl` or `python` are asked about every time and are never offered an "always". `/permissions` shows the current policy, switches between *default* and *ask* (everything asks, including reads), and withdraws an "always allow" — a kind of action or a single command rule.

**What the chat agent knows and how its tools behave** (several of these follow a comparison with opencode — PLAN.md §19):

- **Your project.** With every message the CLI tells the agent the working directory, platform, date and git branch, and sends the nearest `AGENTS.md` (or `CERBERUS.md`, `CLAUDE.md`) — from the directory the CLI runs in, else from a parent up to the repository root. Put how to build, test and behave in your project there. Do not start the CLI inside a repository you do not trust: its instructions file would be read too.
- **Reading.** Files come back with line numbers, 2000 lines at a time; the agent reads on with an offset. Reading a file that probably holds secrets (`.env`, key files) asks you every time, and searches skip `.env` files.
- **Editing.** An edit must match the file, but near-misses a model commonly makes — other line endings, "tidied" quotes and dashes, trailing spaces — still match, and the file keeps its own line endings. The conversation shows what changed as a diff.
- **Commands.** A command is stopped after 2 minutes unless the agent asks for longer. Long output is shown as its beginning and end; all of it is saved to a temporary file the agent can read.
- **Undo.** `/undo` puts back the files the agent wrote or edited in its last turn (and removes the ones it created); `/redo` re-applies them; repeat `/undo` to go back turn by turn. A file you have changed yourself since is left alone and named. The agent is told what you undid with your next message. Only the agent's write and edit tools are covered — what a shell command changed is not, so keep using git for that.
- **Your own commands.** Start a line with `!` to run a command yourself — `!git status`, `!npm test`. It runs in the project directory without asking (it is yours), its output appears in the chat, and the agent is told what you ran and what came of it with your next message. Its output goes to the model like anything else you say, so do not `!cat` a secrets file.
- **Typing ahead.** While the agent is replying or working with a tool, what you type and send is queued and goes out when it has finished. Esc interrupts the reply and drops what was queued.
- **Tasks.** For work of several steps the agent keeps a task list, shown above the input as it changes: what is done, what is under way, what waits for what. It is the agent's own plan — it updates it as it goes, and it survives a summarised conversation.
- **Your own slash commands.** A Markdown file `.cerberus/commands/fix-tests.md` in your project (or in `~/.nightcode/commands/` for all projects) becomes `/fix-tests`. Its text is the prompt; `$ARGUMENTS` is replaced by what you type after the name, `$1`, `$2` by single words (the last one takes the rest). An optional first block `---` / `description: …` / `---` sets what the menu shows. Existing `.opencode/commands` and `.claude/commands` files work too. A command only sends a message as you; it runs nothing by itself.
- **Long conversations.** When a chat nears the model's limit, the earlier part is summarised for the model so the conversation can go on; you are told, and nothing disappears from your screen. `/compact` does it on demand. Local models are assumed to have a small window (8k tokens) unless the backend is started with `CERBERUS_CONTEXT_TOKENS`.
- **Helpers.** For a question that needs a lot of looking around, the agent can hand it to a helper: a second conversation with the same model that reads and searches on its own and reports back, so the main conversation stays short. You see "Task: …" with what the helper is doing, then how its report begins. A helper cannot change files or run commands, and it asks you before reading a file that probably holds secrets, just as the agent does. Esc stops it.
- **Readable replies.** A reply's markdown is shown as headings, lists, tables and highlighted code. When the agent looks around — several reads, searches and listings in a row — those calls are folded into one line that counts them; `ctrl+o` shows each one and folds them again. What it changed or ran is always shown in full.
- **Named conversations.** After its first reply a new conversation is given a short name by the model, so `/sessions` lists what each one was about rather than the first words you typed. `/rename` gives it a name of your own.
- **Fork.** `/fork` copies the conversation you are in — all of it, or up to the reply to an earlier prompt — and puts you in the copy, so you can try another approach without losing the first. The original stays as it is; `/sessions` lists both.
- **Export.** `/export` saves the conversation as a Markdown file (`cerberus-chat-<id>.md`) in the directory the CLI was started in: what was asked, what the agent said, and what it did. Values that look like credentials are masked.
- **A busy provider.** If the model provider is overloaded or rate-limiting, the reply waits and tries again (up to 5 times) and says so, instead of failing.
- **Saying no.** In the permission prompt, `t` denies and lets you tell the agent what to do instead.

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
- **`/review`** — choose *Rules only* (scan, compliance assessments against every catalog, report) or *With <model>* (the model chosen in `/models` also reviews the code, builds a threat model and looks for compliance evidence); type a path first if it is not the whole scan root. Cerberus shows its plan and waits. `a` approves, `r` approves and stops asking about plans, `d` denies. The run then shows each step as it goes and the report at the end.
- **`/sast`** — type a path; the model chosen with `/models` reads that code (read-only) and reports vulnerabilities. You see each file it opens and each search it makes as it goes. With a cloud model the run first asks for approval, because the code it reads is sent to the provider. Its findings are a model's candidates, kept apart from scans. While it works, press `m` to tell it something ("look at the upload handler", "stop here"); it reads your message before its next step.
- **`/threatmodel`** — type a path; the model chosen with `/models` maps that code's components and the threats against them (STRIDE, with likelihood and impact) and writes a threat model, shown when the run ends. It is a model's hypothesis to review with people who know the system, not a finding of fact.
- **`/index`** — type a path; a local embedding model (Ollama's `nomic-embed-text`, which must be pulled once) reads that code so it can be searched by what it does rather than by the words it uses. Nothing leaves your machine. The agents started with `/sast`, `/threatmodel` and the evidence run then get a search-by-meaning tool for that code; run `/index` again after changes — only changed files are processed.
- **`/assess`** — check code against a compliance catalog: pick the catalog (type a path first if it is not the whole scan root). The run opens; when it has finished, `o` opens the assessment.
- **`/assessments`** — open a past assessment. You see where it stands and every control with the answer that counts (yours, else the scanner's). For the highlighted control: `e` asks the model chosen in `/models` to look for evidence (opens that run), `a` accepts the model's suggestion as your answer — it asks for an action plan first when the suggestion is short of compliant — and `x` dismisses it. A model's "Cannot tell" cannot be accepted.
- **`/engagement`** — choose the engagement your runs belong to, or type a name to create one. Everything you start afterwards (`/scan`, `/review`, `/sast`) runs under it, and the backend refuses a target that is not in its scope. The choice is remembered and shown in the status bar; "No engagement" goes back to unscoped runs (unless the server requires one).
- **`/scope`** — the directories the current engagement may be run on. Type a path to add it — doing so is your statement that you own that code or are authorized to test it. With nothing typed, Enter removes the highlighted one.
- **`/abort`** — the kill switch: after one confirmation, stops every queued and running job under the current engagement and ends it for good.
- **`/memory`** — what Cerberus remembers between runs: your standing instructions, your notes, and notes agents left (marked as a model's). Type to add one and choose note or instruction; with nothing typed, Enter forgets the highlighted entry.
- **`/findings`** — open a scan's findings (also `f` on a finished scan or review). Go through them with ↑↓: `r` accepts the risk, `p` marks a false positive — each asks for the reason — and `x` withdraws a decision. A decision stays with the finding in later scans, until the flagged code changes.
- **Export** — on a finished scan, review or model code review, `s` saves its findings as a SARIF file (`cerberus-<kind>-<id>.sarif`) in the directory the CLI was started in, ready for a code-scanning dashboard. Credential-shaped values in snippets are masked.
- **Notices** — wherever you are in the CLI, a notice appears when one of your runs starts waiting for your approval or answer, and when one ends; the status bar shows how many are waiting. The run you are looking at is not announced.
- **`/runs`** — recent runs; those waiting on you are marked. Opening one replays it and follows it live.

On a run's screen you see its state, task list, progress and findings as they arrive (and, for triage, the model's output and verdicts). When the run needs you, the prompt appears in place: `a` / `d` to approve or deny, `↑` `↓` and `enter` to answer a question. `c` cancels a running job, `esc` goes back (the run carries on).

```bash
bun run --cwd packages/cli test
```

Runs on `http://localhost:3000` (configurable via `PORT`), creates its SQLite schema on first run, and exposes the same local-auth / sessions / streaming-chat contract. See [`backend/README.md`](backend/README.md).

### 5. In a pipeline (no screen)

`nightcode run` starts one job, waits for it and exits with a code a pipeline can act on. Progress goes to stderr, the result to stdout.

```bash
nightcode run scan . --sarif cerberus.sarif --fail-on high
```

- **Jobs:** `scan`, `review`, `sast`, `threat-model`, `index`. The path is the target as the server sees it (under its scan root), so the server needs the code: run it on the build machine or give it the checkout.
- **The gate:** `--fail-on critical|high|medium|low|info|none` (default `high`). What counts is what the review report counts: open findings in production code. A line the code marks with `cerberus:ignore`, a finding an operator decided on (accepted risk, false positive), and findings in tests or documentation are reported but do not fail the build; `--include-tests` counts the last group too. A model's findings (`sast`, a review with agents) count like the scanner's and are marked `[model]`.
- **Files:** `--sarif <file>` writes the findings for a code-scanning dashboard, pass or fail; `--report <file>` writes the review or threat model as Markdown. `--json` prints the result as one JSON object instead of text.
- **Nobody to ask:** a job that needs an approval stops with exit code 3 and is cancelled, and the message names what it needed. Approve in advance by name — `--approve run-plan` for a review, `--approve send-code-to-cloud-model` for an agent on a cloud model — or allow it for good in `/permissions`. There is deliberately no "approve everything". The target's own ignore marks are honoured without asking (`--exclusions ignore` counts everything).
- **Sign-in:** the token in `CERBERUS_TOKEN` (a pipeline secret), else the one saved by `/login`. The server's address is `API_URL` or `--server`. A token is the one a sign-in returns and lasts 30 days; named, revocable tokens for pipelines are not built yet.
- **Exit codes:** `0` nothing at or above the threshold · `1` findings at or above it · `2` the job did not finish (failed, cancelled, timed out after `--timeout` minutes, server not reachable, not signed in) · `3` it needs a person · `64` the command line is wrong. Options that would do nothing for the chosen job are refused, not ignored.

In GitHub Actions, for example:

```yaml
- name: Cerberus scan
  env:
    API_URL: ${{ vars.CERBERUS_URL }}
    CERBERUS_TOKEN: ${{ secrets.CERBERUS_TOKEN }}
  run: nightcode run scan . --sarif cerberus.sarif --fail-on high
- uses: github/codeql-action/upload-sarif@v3
  if: always()
  with:
    sarif_file: cerberus.sarif
```

`nightcode run --help` lists every option.

## Local-first by design

No Clerk, no Polar, no hosted Postgres. Authorization is self-hosted (email/password sessions), the database is local SQLite, models run via your own keys or local Ollama, and (per the roadmap) tool execution runs in a self-hosted sandbox. Azure is an optional production deploy target behind the same interfaces, never a requirement to run.

## License

The vendored `security-skills/` library is Apache-2.0 (see `security-skills/LICENSE`); its attribution is preserved.
