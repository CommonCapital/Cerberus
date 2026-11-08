# Compliance catalogs

Framework control catalogs, stored **as data**, that the Cerberus Compliance agent (Product Phase 2) grades a target against. Each catalog is vendor-neutral and ingestible by the catalog engine; findings from Phase 1 and operator questionnaire answers are mapped to each control to produce a verdict, justification, action plan, and deadline.

## `enterprise-sap.json` — Enterprise Security Assurance Plan (ESAP)

A built-in, **representative third-party / vendor security-assurance catalog** modeled on how large enterprises (Fortune-500 procurement and CISO offices) actually evaluate a provider before and throughout a contract — the real structure and rigor used in enterprise vendor-risk assessments, reworded generically so it names no company.

- **39 controls** in 3 sections: **General Security Governance** (13), **Application Security** (8), **SaaS Security** (18).
- **Aligned to** ISO/IEC 27001/27002, NIST CSF 2.0, OWASP Top 10, SOC 2.
- **Response schema** (per control): verdict (`Compliant` / `Partially compliant` / `Non-compliant` / `Not applicable`), justification/evidence, action plan (required when not compliant), and a time limit to remediate (catalog guidance: 30 days standard, 180 days maximum).
- **`autoEvaluable`** flags which controls Cerberus can assess directly from scan evidence (e.g. TLS version, MFA, at-rest encryption, patch currency, logging) versus the organizational controls that require an operator questionnaire (e.g. insurance, NDAs, audit cooperation, exit/reversibility).

### Why it's here
This catalog captures the *methodology* of enterprise security compliance — the control areas, the compliant/partial/non-compliant grading, and the action-plan-with-deadline remediation loop — so Cerberus can tell a user how their system would fare in a real enterprise onboarding review, not just against open frameworks. It is a generic reference template; adapt the thresholds (RTO/RPO, uptime, retention) to the engagement.

### Planned companions
`owasp-asvs.json`, `iso-27001.json`, `soc2.json`, `nist-csf.json`, `cis.json` — same schema, mapped so a single evidence set scores against multiple frameworks at once.
