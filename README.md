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
| De-clouded TypeScript baseline (CLI + server) | ✅ local SQLite, own auth, Ollama + user API keys — no Clerk / Polar / Neon |
| .NET backend (ASP.NET Core + EF Core + SignalR) | 🚧 auth, sessions, AI-SDK-compatible streaming chat — see [`backend/README.md`](backend/README.md) |
| Sandbox, agent fleet, compliance engine, Azure deploy | 📋 planned — see [PLAN.md](PLAN.md) |

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

### 2. TypeScript app (CLI + server)

```bash
bun install
```

```bash
bun run --cwd packages/database db:push
```

```bash
bun run dev:server
```

```bash
bun run dev:cli
```

The CLI signs in to the local server automatically (a local account is created on first `/login`).

### 3. .NET backend

```bash
cd backend && dotnet run --project src/Cerberus.Api
```

Runs on `http://localhost:3000` (configurable via `PORT`), creates its SQLite schema on first run, and exposes the same local-auth / sessions / streaming-chat contract. See [`backend/README.md`](backend/README.md).

## Local-first by design

No Clerk, no Polar, no hosted Postgres. Authorization is self-hosted (email/password sessions), the database is local SQLite, models run via your own keys or local Ollama, and (per the roadmap) tool execution runs in a self-hosted sandbox. Azure is an optional production deploy target behind the same interfaces, never a requirement to run.

## License

The vendored `security-skills/` library is Apache-2.0 (see `security-skills/LICENSE`); its attribution is preserved.
