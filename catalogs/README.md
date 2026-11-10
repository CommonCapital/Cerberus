# Compliance catalogs

Framework control catalogs, stored **as data**, that the Cerberus Compliance agent (Product Phase 2) grades a target against. Each catalog is vendor-neutral and ingestible by the catalog engine; findings from Phase 1 and operator questionnaire answers are mapped to each control to produce a verdict, justification, action plan, and deadline.

## Enterprise Security Assurance Plan (ESAP)

Two files, one catalog:

- **[`enterprise-sap.md`](enterprise-sap.md)** — the **full, human-readable catalog**: the complete grading methodology (response schema, corrective-action timelines, review triggers, RACI, governance) and all 39 controls with their full requirement text. This is the canonical, verbatim version, reproduced vendor-neutrally with the originating company's name and contacts removed.
- **[`enterprise-sap.json`](enterprise-sap.json)** — the **machine-readable form** the Compliance engine ingests: the same controls with concise summaries, evidence hints, `autoEvaluable` flags, framework alignment, and a `methodology` block. Its `fullTextDoc` points back to the Markdown.

A built-in, **representative third-party / vendor security-assurance catalog** modeled on how large enterprises (Fortune-500 procurement and CISO offices) actually evaluate a provider before and throughout a contract — the real structure and rigor used in enterprise vendor-risk assessments, reproduced generically so it names no company.

- **39 controls** in 3 sections: **General Security Governance** (13), **Application Security** (8), **SaaS Security** (18).
- **Aligned to** ISO/IEC 27001/27002, NIST CSF 2.0, OWASP Top 10, SOC 2.
- **Response schema** (per control): verdict (`Compliant` / `Partially compliant` / `Non-compliant` / `Not applicable`), justification/evidence, action plan (required when not compliant), and a time limit to remediate (catalog guidance: 30 days standard, 180 days maximum).
- **`autoEvaluable`** flags which controls Cerberus can assess directly from scan evidence (e.g. TLS version, MFA, at-rest encryption, patch currency, logging) versus the organizational controls that require an operator questionnaire (e.g. insurance, NDAs, audit cooperation, exit/reversibility).

### Why it's here
This catalog captures the *methodology* of enterprise security compliance — the control areas, the compliant/partial/non-compliant grading, and the action-plan-with-deadline remediation loop — so Cerberus can tell a user how their system would fare in a real enterprise onboarding review, not just against open frameworks. It is a generic reference template; adapt the thresholds (RTO/RPO, uptime, retention) to the engagement.

## OWASP Top 10 (2021)

**[`owasp-top10.json`](owasp-top10.json)** — the ten OWASP Top 10 (2021) risk categories as controls `A01`–`A10`, same schema. Requirement text is a short summary written for this catalog; the authoritative descriptions are at <https://owasp.org/Top10/>.

## Scanner mapping (`scannerCategories`)

A control may carry `"scannerCategories": ["secret" | "crypto" | "config" | "injection" | "deserialization" | "dependency", ...]` — the Phase-1 scanner finding categories that signal non-compliance. The deterministic engine judges only controls that have this field: any matching finding → Non-compliant (findings attached as evidence), none → Compliant. Controls without it are Not-assessed, even when `autoEvaluable` is true (the flag says a control *can* be judged from technical evidence; the mapping says the current scanner *does* have a signal). Controls are judged on findings in production code: hits in test or documentation files, and findings the target suppressed, never fail a control (the former are mentioned in its rationale). A control mapped to `dependency` is only Compliant when the dependency (SCA) check actually completed for the scan; otherwise it is Not-assessed. Keep mappings narrow: a "Compliant" verdict only means the scanner found nothing in those categories.

### Planned companions
`owasp-asvs.json`, `iso-27001.json`, `soc2.json`, `nist-csf.json`, `cis.json` — same schema, mapped so a single evidence set scores against multiple frameworks at once.
