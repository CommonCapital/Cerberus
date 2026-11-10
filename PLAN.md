# Project Plan — Autonomous Security Assurance Platform

> **Working codename:** `Cerberus`.
> **What it is:** a multi-agent, AI-driven security platform that scans a target system for vulnerabilities, grades it against enterprise-grade security frameworks, and — under strict authorization — actively attempts to break it ("aggressive hacking: break & solve") and then proposes fixes.
> **What it is NOT:** a general coding assistant, and not a tool for attacking systems the operator does not own or is not authorized to test. The current `nightcode` baseline (a Claude-Code-style coding agent) is the *chassis* we strip down and repurpose.

---

## 0. Table of contents

1. [Product vision & the three operating phases](#1-product-vision--the-three-operating-phases)
2. [Core engineering principles](#2-core-engineering-principles)
3. [The authorization & safety spine (non-negotiable)](#3-the-authorization--safety-spine-non-negotiable)
4. [Target architecture](#4-target-architecture)
5. [Portable seams: local-first, Azure in production](#5-portable-seams-local-first-azure-in-production)
6. [Multi-agent fleet ("multi-agency")](#6-multi-agent-fleet-multi-agency)
7. [LLM model abstraction (Ollama + user API keys)](#7-llm-model-abstraction-ollama--user-api-keys)
8. [Sandbox design (local Docker → Azure AKS)](#8-sandbox-design-local-docker--azure-aks)
9. [Phase 1 engine — Scan & Detect (CodeQL-class)](#9-phase-1-engine--scan--detect-codeql-class)
10. [Phase 2 engine — Enterprise compliance checks (ESAP/ISO/OWASP)](#10-phase-2-engine--enterprise-compliance-checks-esapisoowasp)
11. [Phase 3 engine — Aggressive hacking mode (break & solve)](#11-phase-3-engine--aggressive-hacking-mode-break--solve)
12. [Data model](#12-data-model)
13. [Repository structure](#13-repository-structure)
14. [Build roadmap (engineering milestones)](#14-build-roadmap-engineering-milestones)
15. [Azure production topology](#15-azure-production-topology)
16. [Tech stack summary](#16-tech-stack-summary)
17. [Dogfooding & test targets](#17-dogfooding--test-targets)
18. [Open decisions & risks](#18-open-decisions--risks)

---

## 1. Product vision & the three operating phases

The platform runs an **engagement** against a **target** (a repo, a running service, an infrastructure description, or all three). An engagement progresses through three product phases, each gated by the previous one's results and by authorization level.

### Phase 1 — Scan & Detect (static + architectural)
Find vulnerabilities in **code and system design**, in the spirit of CodeQL / semantic code analysis.
- SAST over the codebase (dataflow/taint, vulnerable patterns).
- Dependency / supply-chain CVE analysis (SCA).
- Secret & credential detection (code + git history).
- **System-design / threat-model analysis** — reasons over architecture, trust boundaries, auth flows, misconfiguration, exposed infra. This is where the LLM beats traditional scanners.
- Output: a normalized findings set (CWE/OWASP-tagged, CVSS-scored).
- Example target class: a large real-world server such as **rabbitmq/rabbitmq-server** (Erlang/OTP + Elixir + config + deployment) — used as a scanning fixture, see §17.

### Phase 2 — Enterprise-grade compliance checks
Evaluate the target against **rigid, structured enterprise security frameworks** to guarantee enterprise compatibility.
- Ingest control catalogs as data. The built-in reference is a generic **Enterprise Security Assurance Plan (ESAP)** — 39 controls across General / Application / SaaS governance, modeled on real Fortune-500 vendor-security assurance (shipped at `catalogs/enterprise-sap.json`).
- For each control, gather evidence (from Phase 1 findings, repo artifacts, config, IaC, operator answers) and produce a verdict: **Compliant / Partially compliant / Non-compliant**, with a justification, an **action plan**, and a **time limit for compliance** — matching the ESAP response schema exactly.
- Also map to ISO 27001/27002, SOC 2, NIST, OWASP ASVS/Top 10, CIS.
- Output: an auditable compliance matrix + remediation roadmap (exportable to the reference spreadsheet shape).

### Phase 3 — Aggressive hacking mode ("break & solve")
Actively attempt to **break and penetrate** the system, then solve what it found.
- Generate and run real commands, payloads, and files that try to **evade the code's defenses and actually penetrate** it (injection, auth bypass, deserialization, SSRF, path traversal, logic abuse, etc.).
- Fuzz and crash-test; attempt to force faults and observe failure modes.
- **Authorized resilience / load testing** (stress & flood against *owned, in-scope* targets only) to measure how much the system withstands — bounded by rate caps, duration caps, and a kill switch.
- Every validated break becomes a finding with a reproduction, then the **Remediation agent** proposes a fix as a diff and re-tests ("solve").
- **This phase is hard-gated** by the authorization spine in §3. It runs only against targets whose ownership/authorization is verified, inside the sandbox, with full audit and an operator kill switch.

> **The line that is enforced in code, not prompts:** aggressive/destructive capability is tied to verified target ownership + explicit engagement scope. The platform is a pentest/red-team tool for systems you are authorized to test. It must be impossible to point the destructive phases at an arbitrary third party.

---

## 2. Core engineering principles

1. **Local-first, zero SaaS lock-in.** The whole platform runs on a laptop with no proprietary cloud service required. No Clerk, no Polar, no Neon, no hosted-only dependency. Cloud (Azure) is an *optional deploy target*, never a runtime requirement.
2. **Portable seams.** Every external concern (DB, cache, queue, realtime, sandbox, auth, LLM) sits behind an interface with a **local** implementation and an **Azure** implementation, selected by configuration + DI. See §5.
3. **Backend in .NET, frontends in TypeScript.** The agent orchestration, APIs, and job pipeline are .NET (first-class SignalR, EF Core, AKS, Azure SDKs). The CLI and web UI are TypeScript.
4. **Tools/agents execute in a sandbox, never on the raw host.** The baseline runs tools directly in the CLI cwd — unacceptable for a security tool. All execution moves into `ISandboxProvider`.
5. **Everything is a job.** Scans and attacks are long-running jobs: enqueue → sandbox worker → stream progress/results over realtime. This is the backbone that makes multi-agent + scope-enforcement + kill-switch possible.
6. **Authorization is a first-class subsystem, not a flag.** See §3.
7. **Deterministic, auditable output.** Findings and compliance verdicts are structured data mapped to recognized frameworks, not free text.

---

## 3. The authorization & safety spine (non-negotiable)

Built **before** any active/offensive agent. This is what separates a pentest platform from an attack tool, and it is also what every enterprise buyer requires.

### Engagement & scope model
- Every run belongs to an **Engagement** owning: target list (repos, hosts, domains, IP ranges), allowed techniques, intensity caps (max RPS, max concurrency, max duration), and time windows.
- **Target authorization verification** before any active phase:
  - Code targets: operator owns/supplied the repo.
  - Live targets: proof-of-control challenge (DNS TXT token, well-known file on host, or signed authorization record) **must pass** before Phase 3 can touch the target.
- Out-of-scope targets are rejected at job-dispatch time, not trusted to the agent.

### Capability tiers (enforced at the queue/worker boundary)
| Tier | Capability | Gate |
|---|---|---|
| T0 Passive | SAST, SCA, secrets, threat-model, compliance read | In-scope target only |
| T1 Active-safe | Light probing, unauthenticated recon, config checks | In-scope + logged |
| T2 Active-intrusive | Exploit attempts, fuzzing, auth-bypass, payload delivery | Verified ownership + explicit operator authorization step |
| T3 Destructive | Crash/fault injection, resilience/load/flood testing | T2 gates + hard rate/duration/blast-radius caps + live kill switch |

### Controls
- **Kill switch:** operator can abort any running job/engagement instantly; workers honor a cooperative cancel + hard timeout.
- **Rate & blast-radius limiters** enforced in the worker, independent of what the agent "decides".
- **Immutable audit log:** every action, target, command, payload, and result — append-only, queryable, exportable.
- **Dry-run / simulation mode** for T2/T3 so operators can preview an attack plan before authorizing execution.

---

## 4. Target architecture

```
┌────────────────────────────────────────────────────────────────┐
│  Frontends (TypeScript)                                         │
│   • CLI  (OpenTUI + React — evolved from current packages/cli)  │
│   • Web  (React/Next — new)                                     │
└───────────────┬────────────────────────────────────────────────┘
                │ HTTPS REST + SignalR (streaming findings/logs)
┌───────────────▼────────────────────────────────────────────────┐
│  Backend API (.NET / ASP.NET Core)                             │
│   • Auth service (own, Better-Auth-style sessions — no Clerk)  │
│   • Engagements / Targets / Scope  (authorization spine §3)   │
│   • Orchestrator (plans engagement, dispatches agents)        │
│   • SignalR hubs (live job/finding streams)                   │
│   • IModelProvider  (Anthropic / OpenAI / Ollama — user keys) │
└───────┬───────────────────────────────┬────────────────────────┘
        │ IMessageBus (enqueue jobs)     │ EF Core
┌───────▼─────────────┐        ┌─────────▼───────────┐
│  Message queue      │        │  Database           │
│  local: RabbitMQ/   │        │  local: SQLite/PG   │
│  in-proc channel    │        │  prod: Azure SQL/PG │
│  prod: RabbitMQ@AKS │        └─────────────────────┘
└───────┬─────────────┘
        │ dispatch
┌───────▼──────────────────────────────────────────────┐
│  Agent workers (.NET)  →  ISandboxProvider           │
│   run each agent's tools inside an OpenSandbox box   │
│   local: OpenSandbox + Docker runtime                │
│   prod:  OpenSandbox + Kubernetes runtime on AKS     │
│   egress FQDN allowlist = engagement scope (§3)      │
│   Agents: Recon · SAST · SCA · Secrets · ThreatModel │
│           · Compliance · Exploit · Resilience · Fix  │
└───────────────────────────────────────────────────────┘
        │ IDistributedCache (dedupe, rate state)
        ▼  local: in-memory/Redis   prod: Azure Cache for Redis
```

### Shared contracts
A shared schema package defines tool I/O, finding shape, compliance control shape, and agent job contracts. Canonical source lives once; the TS frontends consume generated/typed equivalents so CLI, web, and backend never drift.

---

## 5. Portable seams: local-first, Azure in production

Nothing talks to a vendor directly. Each concern is an interface with two implementations, chosen by config.

| Concern | Interface | Local (dev / self-host) | Production (Azure) |
|---|---|---|---|
| Database | EF Core `DbContext` | SQLite or local Postgres | Azure SQL / Postgres Flexible Server |
| Auth | `IAuthService` | Own sessions in local DB (Better-Auth-style) | Same code, Azure DB-backed |
| Cache | `IDistributedCache` | In-memory / local Redis | Azure Cache for Redis |
| Queue / jobs | `IMessageBus` | In-proc channel / local RabbitMQ | RabbitMQ on AKS (or Azure Service Bus) |
| Realtime | SignalR | Self-hosted SignalR | Azure SignalR Service |
| Sandbox | `ISandboxProvider` | **OpenSandbox** server + Docker runtime | **OpenSandbox** server + Kubernetes runtime on AKS |
| LLM | `IModelProvider` | Ollama **or** user API key | User keys / Ollama / Azure OpenAI |
| Secrets | `ISecretStore` | OS keychain / encrypted local file | Azure Key Vault |
| Blob/artifacts | `IArtifactStore` | Local filesystem | Azure Blob Storage |

**Rule:** business/agent logic depends only on the interfaces. Swapping local → Azure is DI registration + deployment manifests, never a code rewrite.

---

## 6. Multi-agent fleet ("multi-agency")

An **Orchestrator** reads the engagement, builds a plan, and dispatches specialist agents as jobs. Each agent has its own **cybersecurity system prompt** (versioned in the repo, composed from a shared base ROE/output-schema preamble), its own toolset, and its own sandbox profile. Agents run in parallel and stream findings live.

| Agent | Role | Phase | Tier |
|---|---|---|---|
| **Orchestrator** | Plans engagement, sequences agents, dedupes & correlates findings | all | — |
| **Recon** | Passive + active discovery: surface, ports, services, endpoints, stack | 1 | T0/T1 |
| **SAST / Code** | Static analysis, taint/dataflow, vulnerable patterns (uses file/grep tools) | 1 | T0 |
| **SCA / Dependency** | Known-CVE deps, supply-chain, license risk | 1 | T0 |
| **Secrets** | Leaked keys/credentials in code, config, git history | 1 | T0 |
| **Threat-Model / System-Design** | Reasons over architecture: trust boundaries, auth flows, IDOR/priv-esc paths, misconfig, exposed infra | 1 | T0 |
| **Compliance** | Maps evidence to control catalogs (ESAP/ISO/SOC2/NIST/OWASP), scores verdicts + action plans | 2 | T0 |
| **Exploit / Active** | Validates findings: generates payloads/files to evade & penetrate; auth bypass, injection, SSRF, deserialization | 3 | T2 |
| **Resilience / Load** | Crash/fault injection, authorized stress/flood to measure withstand threshold | 3 | T3 |
| **Remediation / Report** | Proposes fixes as diffs, re-tests ("solve"), writes the final report | 2/3 | T0 |

The **Threat-Model agent** is the direct answer to "loopholes in system design, not only code". The **Exploit agent** is the heart of "aggressive hacking: break & solve" — it generates real artifacts that attempt to bypass defenses, runs them in the sandbox against authorized targets, and feeds confirmed breaks to Remediation.

**Agents are powered by a curated skills library** (see §7.5) — each agent loads the subset of the 818 vendored cybersecurity skills that matches its subdomain, giving it concrete, framework-mapped procedures rather than only free-form prompting.

---

## 7. LLM model abstraction (Ollama + user API keys)

`IModelProvider` with implementations:
- `AnthropicProvider`, `OpenAIProvider` — use **user-supplied API keys**, never baked-in. Keys are entered by the user, stored per-user **encrypted** (`ISecretStore`: OS keychain for CLI / Key Vault in prod), passed at request time.
- `OllamaProvider` — local models via HTTP (`localhost:11434` locally, or a serving pod in prod). Makes "fully local & independent" real: a user can run with zero external API.
- `AzureOpenAIProvider` — optional prod convenience.

A **model registry** advertises available models + capabilities (context window, tool-calling, reasoning) so the orchestrator can route (e.g. cheap local model for triage, stronger model for exploit reasoning). Streaming unified across providers → surfaced to frontends over SignalR.

---

## 7.5 Security skills library (vendored capabilities)

The repo vendors **`security-skills/`** — **818 Apache-2.0 cybersecurity skills** (from `mukul975/Anthropic-Cybersecurity-Skills`), each a `SKILL.md` with YAML frontmatter and, often, reference files/scripts. Every skill is mapped to **6 frameworks**: MITRE ATT&CK, NIST CSF 2.0, MITRE ATLAS, D3FEND, NIST AI RMF, and MITRE F3. Their frontmatter consistently scopes them to *"authorized security testing"* — aligned with our §3 spine.

**Subdomain distribution (maps skills → agents):**

| Agent | Primary skill subdomains (count) |
|---|---|
| Recon / Pentest | penetration-testing (21), network-security (43), wireless (2) |
| SAST / Code / AppSec | web-application-security (42), api-security (28), application-security (4), devsecops (18) |
| SCA / Supply-chain | supply-chain-security (8) |
| Secrets / Crypto | cryptography (16) |
| Threat-Model / System-Design | zero-trust-architecture (17), identity-access-management (37), cloud-security (66), container-security (33), ot-ics-security (28) |
| Compliance | compliance-governance (10), privacy-compliance (2), governance-risk-compliance (1) |
| Exploit / Active (T2) | red-teaming (33), offensive-security (2), mobile-security (13) |
| Resilience / Blue-team context | threat-hunting (58), incident-response (26), SOC-operations (35), malware-analysis (39), DFIR (41) |
| Cross-cutting | ai-security (14), ransomware-defense (13), vulnerability-management (25), endpoint-security (17), phishing-defense (15) |

- **How agents use them:** at dispatch the orchestrator selects skills by `subdomain`/`tags`/framework mapping and injects the relevant `SKILL.md` procedures into the agent's context. `security-skills/mappings/` (MITRE/NIST/OWASP) drives selection and lets findings cite the exact technique/control.
- **Also vendored:** `security-skills/references/daytona-cloud-skill/` — the `daytona/skills` sandbox-SDK skill, kept as an **interface-design reference only** (it targets Daytona Cloud and needs an API key, so we do not depend on it; our sandbox is OpenSandbox, §8).
- **Licensing:** both skill sets are Apache-2.0; `LICENSE`/`NOTICE` are preserved under `security-skills/`. Keep attribution when redistributing.

---

## 8. Sandbox design — Alibaba OpenSandbox (local Docker → Azure AKS)

**Decision:** the sandbox provider is **Alibaba OpenSandbox** (`github.com/alibaba/OpenSandbox`, Apache-2.0, CNCF Landscape), self-hosted. It replaces the earlier Daytona idea — Daytona's self-hostable OSS was frozen in June 2026 (last tag `v0.190.0`; core moved private), and `daytona/skills` targets Daytona **Cloud** (`app.daytona.io`, needs `DAYTONA_API_KEY`), which conflicts with our no-cloud-dependency goal. OpenSandbox is purpose-built for exactly this: isolated runtimes for AI agents, Docker **and** Kubernetes runtimes from one control plane, and — decisively — a native **C#/.NET SDK** plus an **FQDN egress sidecar** that becomes our scope-enforcement mechanism.

### Why it fits our architecture point-for-point
- **Control plane = OpenSandbox Server** (FastAPI): standardized REST lifecycle APIs — create / start / pause / resume / delete — with pluggable **Docker (local)** and **Kubernetes (prod)** runtimes, both production-grade. This *is* the `ISandboxProvider` backend; our .NET provider calls it.
- **Native .NET SDK** (`sdks/sandbox/csharp`, `sdks/code-interpreter/csharp`) → the .NET backend drives sandboxes without hand-rolling a client.
- **OpenAPI contract** we build against: `specs/sandbox-lifecycle.yml`, `specs/execd-api.yaml` (in-sandbox execution), `specs/egress-api.yaml`, `specs/diagnostic-api.yml`.
- **Built-in environments:** Command, Filesystem, and Code Interpreter — our Phase-1 file/bash/grep tools map onto these directly (examples even include running Claude Code inside a sandbox).
- **Strong isolation for the destructive phase:** secure runtimes **gVisor, Kata Containers, Firecracker microVM** — the right blast-radius control for Phase 3 exploit execution.
- **Resource quotas, TTL cleanup, async provisioning, API-key access control** (can be disabled for local/dev).

### Scope enforcement lives in the sandbox egress (ties to §3)
OpenSandbox's **Egress sidecar** shares the sandbox network namespace and enforces **FQDN-based allowlists** (wildcards, transparent DNS proxy, default-deny + domain-allow via nftables, optional HTTPS MITM). We drive that allowlist from the **engagement scope**: an agent's sandbox can reach *only* the domains/hosts its engagement authorized. Out-of-scope egress is blocked at the network layer, not by prompt discipline — this is the concrete mechanism behind the capability tiers and the "can't point it at a third party" guarantee.

### `ISandboxProvider` (our thin .NET wrapper over the OpenSandbox SDK)
`CreateSandbox(profile) → handle`, `Exec(handle, command) → stream`, `PutFile/GetFile`, `SetEgressPolicy(handle, allowlist)`, `Dispose(handle)`.
- **Profiles** map to OpenSandbox: base image, runtime (Docker/K8s), isolation class (standard / gVisor / Kata / Firecracker), workspace mount (read-only for passive agents, writable scratch for exploit artifacts), resource quota, egress allowlist, capability tier.
- **Local:** OpenSandbox server with the Docker runtime; passive agents get no egress, active agents get the engagement's FQDN allowlist.
- **Production:** same server + API, Kubernetes runtime on **AKS**; the egress sidecar + K8s NetworkPolicies encode scope. Identical job contract — local → Azure is a runtime config swap.
- All tool execution (baseline `readFile`/`bash`/etc. + security tools) runs **inside** the sandbox. The host is never the execution surface.

---

## 9. Phase 1 engine — Scan & Detect (CodeQL-class)

**Goal:** semantic, not just regex. Combine deterministic tooling with LLM reasoning.

- **Ingest:** clone/import target repo into sandbox workspace; build a file/AST/dependency inventory.
- **Deterministic layer:** wrap/invoke best-in-class OSS scanners inside the sandbox (e.g. Semgrep/CodeQL-style queries, dependency CVE databases, secret scanners, IaC scanners). Normalize their output.
- **LLM layer:** agents reason over code + scanner output to find logic flaws, chained vulnerabilities, and design weaknesses deterministic tools miss; reduce false positives by validating context.
- **Correlation:** Orchestrator merges, dedupes, and ranks findings; assigns CWE/OWASP tags and CVSS.
- **Output schema (per finding):** id, title, severity, CVSS, CWE/OWASP, location (file/line or component), evidence, exploitability note, remediation hint, confidence.
- Findings feed Phase 2 (as compliance evidence) and Phase 3 (as hypotheses to actively validate).

---

## 10. Phase 2 engine — Enterprise compliance checks (ESAP/ISO/OWASP)

**Goal:** guarantee enterprise compatibility by grading the target against rigid control catalogs — reference sample = a generic **Enterprise Security Assurance Plan (ESAP)**.

### Control-catalog model (frameworks are data, not code)
```
Catalog { id, name, version, sections[] }
Section { name, controls[] }
Control {
  reqNo, section, requirement,              // e.g. ESAP Req 7 "Vulnerability and Patch Management"
  evidenceHints[],                           // what satisfies it
  verdict: Compliant | Partially | NonCompliant | NA,
  justification, actionPlan, timeLimitDays   // mirrors ESAP response schema
}
```

- **Seed catalogs:** the **ESAP catalog ships in-repo at [`catalogs/enterprise-sap.json`](catalogs/enterprise-sap.json)** — 39 vendor-neutral controls modeled on real Fortune-500 vendor-security assurance, in 3 sections (General Governance 13, Application 8, SaaS 18), each with `requirement`, `evidenceHints`, and an `autoEvaluable` flag. Planned companions (same schema): OWASP ASVS + Top 10, ISO 27001/27002, SOC 2, NIST CSF/800-53, CIS.
- **Evaluation:** the Compliance agent gathers evidence (Phase 1 findings, repo artifacts, config/IaC, operator-answered org questions that can't be auto-detected — e.g. the catalog's controls on insurance, audit cooperation, backup rotation, exit/reversibility) and produces the verdict + action plan + deadline per control.
- **Auto vs. operator-answered:** technical controls (encryption in transit/at rest, TLS version, MFA presence, secrets handling, OWASP Top 10, patch currency) can be largely auto-evaluated from scan evidence; organizational controls (liability insurance, committees, NDAs) are operator questionnaires — the platform tracks both.
- **Output:** a compliance matrix + remediation roadmap, **exportable to a vendor-assurance spreadsheet** (Verdict + Detailed Response + Action Plan + Time limit — the schema enterprises expect back), plus mapped scores for the other frameworks.

---

## 11. Phase 3 engine — Aggressive hacking mode (break & solve)

**Goal:** actually try to break and penetrate — generate commands/files that evade defenses and penetrate — then solve. **Hard-gated by §3.**

### Flow
1. **Plan (dry-run first).** Exploit agent turns Phase 1 hypotheses into a concrete attack plan. For T2/T3 the plan is shown to the operator for explicit authorization before anything runs.
2. **Verify authorization & scope.** Ownership challenge must pass; engagement caps loaded into the worker.
3. **Execute in sandbox.** Agent generates real payloads/scripts/files, runs them against the authorized target from inside the sandbox:
   - Injection (SQL/command/template), auth/session bypass, SSRF, path traversal, deserialization, access-control/IDOR, business-logic abuse.
   - Evasion artifacts: inputs crafted to bypass validation/WAF-style defenses.
   - Fuzzing & crash/fault injection to force failure modes.
4. **Resilience/load (T3).** Authorized stress/flood against the owned target to measure the withstand threshold — strictly rate-capped, duration-capped, kill-switchable. Reframed and enforced as *your-own-system resilience testing*, never third-party flooding.
5. **Confirm & capture.** Each successful break → finding with full reproduction + captured evidence (append-only audit).
6. **Solve.** Remediation agent proposes a fix as a diff, applies it in a scratch branch, and **re-runs the same break** to prove it's closed. Loop until fixed or escalated.

### Guardrails recap (enforced, not advisory)
- Only verified-ownership, in-scope targets.
- Worker-level rate/blast-radius/duration caps independent of the agent.
- Live kill switch; cooperative cancel + hard timeout.
- Full immutable audit of every command and payload.
- Dry-run/simulation available for every T2/T3 action.

---

## 12. Data model

Core entities (EF Core; local SQLite/PG → Azure SQL/PG):

- **User** — identity, credentials (hashed), encrypted model API keys.
- **Session** (auth) — Better-Auth-style token/session, stored server-side; CLI keeps token in `~/.cerberus/auth.json` (baseline already has this pattern).
- **Engagement** — owner, status, scope (targets, allowed tiers, caps, windows).
- **Target** — type (repo/host/domain/infra), locator, ownership-verification status + proof.
- **Job** — agent type, tier, engagement, state, sandbox profile, timing, cancel token.
- **Finding** — schema from §9 (severity, CVSS, CWE/OWASP, evidence, remediation, confidence, status).
- **ControlResult** — catalog/control ref, verdict, justification, action plan, deadline (§10).
- **AuditEvent** — append-only: actor, action, target, payload hash, result, timestamp.
- **ChatMessage / AgentTranscript** — per-session agent reasoning + tool calls (evolves the baseline `Session.messages`).

---

## 13. Repository structure

```
cerberus/
├─ backend/                       # .NET solution
│  ├─ Cerberus.Api/                  # ASP.NET Core: REST + SignalR hubs
│  ├─ Cerberus.Core/                 # domain: engagements, scope, findings, controls
│  ├─ Cerberus.Auth/                 # own auth (sessions, no Clerk)
│  ├─ Cerberus.Agents/               # orchestrator + agent definitions + prompts
│  ├─ Cerberus.Models/               # IModelProvider: Anthropic/OpenAI/Ollama/AzureOpenAI
│  ├─ Cerberus.Sandbox/              # ISandboxProvider: thin wrapper over OpenSandbox C# SDK (Docker local / K8s prod)
│  ├─ Cerberus.Messaging/            # IMessageBus: RabbitMQ / in-proc
│  ├─ Cerberus.Workers/              # job workers that run agents in sandboxes
│  ├─ Cerberus.Compliance/           # catalog engine + ESAP/ISO/OWASP catalogs + xlsx import/export
│  ├─ Cerberus.Data/                 # EF Core contexts + providers (SQLite/PG/Azure)
│  └─ Cerberus.Infrastructure/       # cache, secrets, artifact store adapters
├─ frontends/
│  ├─ cli/                        # TypeScript — evolved from current packages/cli
│  └─ web/                        # TypeScript React/Next
├─ shared/
│  └─ contracts/                  # canonical schemas (findings, controls, tools, jobs) → TS types
├─ catalogs/                      # framework catalogs as data (ESAP, OWASP, ISO, SOC2, NIST, CIS)
├─ prompts/                       # versioned agent system prompts (security ROE + output schemas)
├─ security-skills/               # VENDORED (Apache-2.0): 818 cyber skills + framework mappings
│  ├─ skills/                     #   <skill-name>/SKILL.md (+ references) — loaded per agent
│  ├─ mappings/                   #   MITRE ATT&CK / NIST CSF / OWASP skill maps
│  ├─ references/daytona-cloud-skill/  # sandbox-SDK interface reference only (not a dependency)
│  └─ LICENSE
├─ third_party/
│  └─ opensandbox/                # Alibaba OpenSandbox (server + C# SDK) — self-hosted sandbox runtime
├─ deploy/
│  ├─ local/                      # docker-compose: db, redis, rabbitmq, ollama, opensandbox-server
│  └─ azure/                      # Bicep/Terraform + AKS manifests, Key Vault, SignalR, opensandbox-on-k8s
└─ PLAN.md
```

> **Transition note:** the existing `packages/cli` (OpenTUI + React, with client-side local tools) is kept and retargeted to the new backend. `packages/server`, `packages/database`, `packages/shared` are superseded by the `.NET` backend + `shared/contracts`, but their logic (model registry, tool contracts, system-prompt structure, session shape) is ported, not discarded.
>
> **Note on tool execution:** once the sandbox lands (M3), the CLI stops executing tools on the host — tool calls are dispatched to the OpenSandbox-backed worker instead. The baseline's client-side execution survives only as a dev fallback.

---

## 14. Build roadmap (engineering milestones)

These are *engineering* phases (how we build), distinct from the three *product* phases (what it does). Recommendation: keep it runnable at every milestone, and build abstractions with **local implementations only** — defer all Azure bindings to M7.

Status legend: ✅ done · 🟡 in progress / partial · ⬜ not started.

| Status | Milestone | Goal | Key outcomes |
|:--:|---|---|---|
| ✅ | **M0 — Decisions** | Lock open items (§18) | Sandbox provider (OpenSandbox), product name (Cerberus), skills library confirmed |
| ✅ | **M1 — De-cloud the baseline (still TS)** | Remove all SaaS | Polar/billing removed; Clerk replaced with local session auth; Prisma → local SQLite; Ollama + user-key model support. Fully local baseline, still works. |
| 🟡 | **M2 — .NET backend (local mode)** | Rebuild the chassis in .NET | ✅ ASP.NET Core + SignalR skeleton, EF Core + SQLite, own auth, `IModelProvider` (Anthropic/OpenAI/Ollama), streaming `/chat` in the AI-SDK UI-message protocol, per-user encrypted API keys. ⬜ Retarget the TS CLI at the .NET server; tool-calling. |
| 🟡 | **M3 — Sandbox + job pipeline** | Execution isolation + jobs | ✅ `IMessageBus` with an in-process implementation (`Cerberus.Jobs`), a persisted `Job` model and a background worker: scans can be submitted as jobs (`POST /jobs`), are re-authorized against the engagement scope when they start, and run under platform controls — operator cancel (`POST /jobs/{id}/cancel`), a time limit, and a hard stop that abandons work ignoring the stop signal. Queued jobs survive a restart; jobs that were running are failed. ⬜ **OpenSandbox server** (Docker runtime) + `ISandboxProvider` over its C# SDK; all tools run in sandbox; egress policy wired to scope; RabbitMQ bus; job progress / findings streamed over SignalR; compliance assessments as jobs. |
| 🟡 | **M4 — Authorization & safety spine** | §3 before any offense | ✅ Engagements with in-scope targets, a max capability tier and an optional time window (`/engagements`, `Cerberus.Authorization`). ✅ Dispatch-time scope gate (`ScopePolicy`): a scan or assessment run under an engagement is rejected (403) if the target is out of scope, the engagement is closed/aborted or outside its window, or the tier is too high. ✅ Kill switch (`POST /engagements/{id}/abort`): stops any further runs being authorized and cancels the engagement's queued and running jobs (synchronous `POST /scan` requests already in flight are not interrupted). ✅ Append-only, hash-chained audit log (`/audit`, `/audit/verify`; UPDATE/DELETE blocked by database triggers). ✅ Code targets require an operator ownership attestation. ⬜ Make an engagement mandatory for every run (still optional for passive T0 scans); proof-of-control verification for host/domain targets (they stay `Pending`); T2/T3 tiers and the explicit authorization step (not grantable yet); rate/blast-radius caps; audit export. |
| 🟡 | **M5 — Product Phase 1 (Scan & Detect)** | Passive agents | 🟡 Deterministic scanner shipped on the .NET backend (`/scan`): regex secret detection + insecure-pattern rules → normalized CWE-tagged findings, path-confined, and **persisted** (`/scans`, `/scans/{id}`). ✅ SCA/CVE analysis: parses npm/PyPI/Go/NuGet manifests, resolves known vulns via **OSV.dev** (keyless, read-only), emits dependency findings (CVE, fixed version, CWE, CVSS-bucketed severity) merged into the same scan. ⬜ LLM SAST + Threat-Model agents, sandbox integration, dogfood on RabbitMQ repo (§17). |
| 🟡 | **M6 — Product Phase 2 (Compliance)** | Catalog engine | ✅ ESAP catalog shipped as data (`catalogs/enterprise-sap.{md,json}`: full methodology + 39 controls). ✅ Deterministic catalog engine on the .NET backend (`Cerberus.Compliance`): loads catalogs from `catalogs/`, judges each control that declares `scannerCategories` (the control→finding mapping is catalog data) against Phase-1 scanner findings, and produces a **persisted compliance matrix** with evidence + score (`/compliance/catalogs`, `POST /compliance/assess` from a scan id or path, `/compliance`, `/compliance/{id}`); 8 of 39 ESAP controls are auto-judged today, the rest are flagged for manual review. ✅ OWASP Top 10 (2021) catalog (`catalogs/owasp-top10.json`, 5 of 10 auto-judged). ✅ Dependency (SCA) findings mapped to controls (ESAP GSG-07, OWASP A06); scans record whether the dependency check completed, and a dependency-mapped control is only Compliant when it did. ✅ Spreadsheet export (`GET /compliance/{id}/export` → .xlsx: Summary, Controls with Verdict / Detailed Response / Action Plan / Time limit, Evidence); controls the engine could not judge are left blank for the operator. ✅ Backend unit tests (`backend/tests/Cerberus.Tests`: engine, catalogs, export). ✅ Operator answers: per-control verdict / justification / action plan / time limit validated against the catalog's response schema (`PUT`/`DELETE /compliance/{id}/responses/{controlId}`), which fill in unjudged controls or overrule the scanner; `GET /compliance/{id}` reports an `effective` summary. ✅ Spreadsheet import (`POST /compliance/{id}/import`): reads a completed export back in, skipping untouched rows and reporting invalid ones per row. ⬜ LLM Compliance agent (manual-control evidence), OWASP ASVS/ISO/SOC 2 catalogs. |
| ⬜ | **M7 — Azure implementations** | Prod swap | Azure SQL/PG, Azure Cache, Azure SignalR, AKS sandbox pods, RabbitMQ@AKS, Key Vault, Blob. DI + manifests only. |
| ⬜ | **M8 — Product Phase 3 (Aggressive mode)** | Gated offense | Exploit agent (T2), Resilience/Load (T3), Remediation/Report; dry-run → authorize → execute → solve loop. Built last, after the spine is proven. |
| ⬜ | **M9 — Web frontend** | Second client | React/Next UI against the same API + SignalR. |
| ⬜ | **M10 — Hardening & reporting polish** | Enterprise-ready | Audit exports, framework reports, RBAC, multi-tenant separation. |

---

## 15. Azure production topology

- **Compute:** AKS (API, workers, sandbox pods, RabbitMQ). Sandbox pods scheduled per job with NetworkPolicies encoding scope; ephemeral.
- **Data:** Azure SQL or Azure Database for PostgreSQL Flexible Server.
- **Cache:** Azure Cache for Redis (dedupe, rate-limit state, session cache).
- **Messaging:** RabbitMQ on AKS (or Azure Service Bus) behind `IMessageBus`.
- **Realtime:** Azure SignalR Service.
- **Secrets:** Azure Key Vault (`ISecretStore`) — user model API keys, platform secrets.
- **Artifacts:** Azure Blob Storage (scan reports, evidence, exploit artifacts).
- **Identity/deploy:** Managed identities, Bicep/Terraform in `deploy/azure`, GitHub Actions CI/CD.
- **Observability:** Azure Monitor / App Insights; audit log shipped to immutable storage.

All of the above are **implementations of the §5 seams** — the same build runs locally with Docker/Compose equivalents.

---

## 16. Tech stack summary

| Layer | Choice |
|---|---|
| Backend | .NET 8/9, ASP.NET Core, EF Core, SignalR |
| Auth | Own implementation (Better-Auth-style sessions), no Clerk |
| Frontends | TypeScript — CLI (OpenTUI + React), Web (React/Next) |
| LLM | `IModelProvider`: Anthropic / OpenAI (user keys) + Ollama (local) + Azure OpenAI |
| DB | SQLite / Postgres (local) → Azure SQL / PG (prod) |
| Cache | Redis (local) → Azure Cache for Redis |
| Queue | RabbitMQ / in-proc (local) → RabbitMQ@AKS / Service Bus |
| Realtime | SignalR (self-host) → Azure SignalR |
| Sandbox | **Alibaba OpenSandbox** (self-hosted): Docker runtime local → Kubernetes runtime on AKS; gVisor/Kata/Firecracker isolation; FQDN egress sidecar for scope |
| Secrets | OS keychain / encrypted file → Azure Key Vault |
| Agent skills | Vendored 818-skill Apache-2.0 library (MITRE/NIST/OWASP-mapped) in `security-skills/` |
| Scanners | OSS SAST/SCA/secret/IaC tools wrapped in sandbox + LLM reasoning layer |
| Deploy | Docker Compose (local) / Bicep+Terraform+AKS (Azure) |

---

## 17. Dogfooding & test targets

- **Primary scanning fixture:** `rabbitmq/rabbitmq-server` (https://github.com/rabbitmq/rabbitmq-server.git) — a large, real, multi-language codebase (~1300 Erlang/OTP `.erl`, ~260 Elixir `.ex`, plus Python, shell, configs). Rich, relevant attack surface in `deps/`: multiple **auth backends** (`ldap`, `oauth2`, `http`, `ssl`, `internal_loopback`, `cache`), **protocol plugins** (`mqtt`, `web_mqtt`, `stomp`, `web_stomp`, `stream`, `amqp10`), **peer-discovery** (`k8s`, `aws`, `consul`, `etcd`), and `management`/`prometheus` HTTP surfaces — ideal for exercising the Threat-Model, SAST, and (on an **owned** instance) Exploit agents. Use it to validate Phase 1 scanning at scale and finding normalization. **Scanning only (T0)** against the upstream repo; stand up our **own** isolated RabbitMQ instance before any Phase 3 activity — never point the destructive tiers at upstream infrastructure.
- **Self-test:** point passive agents at this very platform's own repo each milestone.
- **Owned victim stack:** stand up intentionally vulnerable, self-owned targets (e.g. a deliberately insecure app we control) in the sandbox for Phase 3 exploit/resilience validation — the only safe way to test the destructive tiers.

---

## 18. Open decisions & risks

**Decisions settled:**
- ✅ **Sandbox provider:** Alibaba **OpenSandbox** (self-hosted; Docker local / K8s on AKS). Daytona dropped — its self-hostable OSS froze June 2026 and `daytona/skills` is Cloud-only.
- ✅ **Skills library:** vendor the 818-skill Apache-2.0 set under `security-skills/`, select per agent by subdomain/framework.
- ✅ **Product name:** **Cerberus** (repo `CommonCapital/Cerberus`; package/project prefix `Cerberus.*`).

**Decisions needed (M0):**
1. **Catalog priority:** ESAP + OWASP first, or broader ISO/SOC2/NIST set in M6?
2. **DB default for local:** SQLite (zero-config) vs local Postgres (parity with prod).
3. **Web framework:** plain React SPA vs Next.js.
4. **OpenSandbox isolation class for Phase 3:** gVisor (lighter) vs Kata/Firecracker (stronger, heavier) as the default for destructive-tier sandboxes.
5. **OpenSandbox pinning:** track upstream `main`, or pin a release and vendor under `third_party/` for reproducibility.

**Risks / watch-items:**
- **LLM is still an external call** unless Ollama is used — "fully independent" is only literally true in Ollama-only mode. Abstraction covers both; set user expectation.
- **Phase 3 is the legal/ethical hotspot.** The authorization spine (§3) must be complete and proven (M4) before M8. No destructive capability ships without verified ownership + caps + kill switch + audit.
- **.NET rewrite cost:** M2 re-implements the server from scratch. Mitigated by doing M1 first (clean, de-clouded, working baseline to port from) and keeping the TS CLI.
- **Scanner integration:** wrapping heavyweight OSS scanners (CodeQL etc.) in sandboxes has real build/runtime weight — budget for it in M5.
- **Compliance auto-evaluation limits:** many ESAP controls are organizational (insurance, committees, NDAs) and can't be auto-detected — the questionnaire path must exist alongside auto-evaluation.
- **OpenSandbox dependency:** it's Apache-2.0 and CNCF-listed (healthy), but it's a young project and a core runtime dependency. Pin a version, keep the `ISandboxProvider` seam thin so it's replaceable, and budget time to operate its server (control plane) in both local and AKS modes.
- **Skills-library provenance:** the 818 skills are community-authored (Apache-2.0). Treat their contents as *capability references, not vetted doctrine* — review before wiring any skill into an active/destructive agent, and keep LICENSE/NOTICE attribution on redistribution.

---

*End of plan. This document captures the full scope discussed: de-clouding (no Clerk/Polar/Neon), local DB + **Alibaba OpenSandbox** sandbox + own authorization, dual LLM support (user API keys + Ollama), .NET backend with TypeScript CLI + web frontends, Azure production (DB, cache, RabbitMQ, Kubernetes, SignalR, OpenSandbox-on-AKS), the multi-agent security fleet powered by a vendored **818-skill** framework-mapped library, and the three product phases — Scan & Detect, Enterprise Compliance (enterprise-grade), and Aggressive Hacking "break & solve" — all bound by an enforced authorization & safety spine whose network-level enforcement is the OpenSandbox FQDN egress allowlist.*
