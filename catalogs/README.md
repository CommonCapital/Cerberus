# Compliance catalogs

Framework control catalogs, stored **as data**, that the Cerberus Compliance agent (Product Phase 2) grades a target against. Each catalog is vendor-neutral and ingestible by the catalog engine; findings from Phase 1 and operator questionnaire answers are mapped to each control to produce a verdict, justification, action plan, and deadline.

## Vendor Security Assessment

- **[`vendor-security.md`](vendor-security.md)** — the catalog for people: how it is answered, and every control with the evidence that supports it.
- **[`vendor-security.json`](vendor-security.json)** — the same catalog as the Compliance engine reads it.

What a customer usually needs to know about a software or SaaS provider's security before signing and while the contract runs, written for Cerberus as an open, generic catalog (it reproduces no company's questionnaire):

- **32 controls** in 6 sections: governance and people, identity and access, data protection, secure development, operations, incidents and resilience.
- **Aligned to** ISO/IEC 27001, NIST CSF 2.0, OWASP Top 10, SOC 2.
- **Answered** per control with a verdict (`Compliant` / `Partially compliant` / `Non-compliant` / `Not applicable`), the evidence behind it, and an action plan with a deadline for anything not met (30 days by default, at most 90).
- **`autoEvaluable`** marks the controls that technical evidence can settle (encryption, MFA, secrets, patching, logging); the others need the provider's answer.

## OWASP Top 10 (2021)

**[`owasp-top10.json`](owasp-top10.json)** — the ten OWASP Top 10 (2021) risk categories as controls `A01`–`A10`, same schema. Requirement text is a short summary written for this catalog; the authoritative descriptions are at <https://owasp.org/Top10/>.

## Scanner mapping (`scannerCategories`)

A control may carry `"scannerCategories": ["secret" | "crypto" | "config" | "injection" | "deserialization" | "dependency", ...]` — the Phase-1 scanner finding categories that signal non-compliance. The deterministic engine judges only controls that have this field: any matching finding → Non-compliant (findings attached as evidence), none → Compliant. Controls without it are Not-assessed, even when `autoEvaluable` is true (the flag says a control *can* be judged from technical evidence; the mapping says the current scanner *does* have a signal). Controls are judged on findings in production code: hits in test or documentation files, and findings the target suppressed, never fail a control (the former are mentioned in its rationale). A control mapped to `dependency` is only Compliant when the dependency (SCA) check actually completed for the scan; otherwise it is Not-assessed. Keep mappings narrow: a "Compliant" verdict only means the scanner found nothing in those categories.

## Planned companions
`owasp-asvs.json`, `iso-27001.json`, `soc2.json`, `nist-csf.json`, `cis.json` — same schema, mapped so a single evidence set scores against multiple frameworks at once.

## Adding a catalog

Put one JSON file per catalog in this folder (or wherever `CERBERUS_CATALOG_ROOT` points); it is read at start-up.

```json
{
  "id": "my-catalog",
  "name": "My catalog",
  "version": "1.0",
  "description": "What it covers.",
  "frameworkAlignment": ["ISO/IEC 27001"],
  "responseScale": ["Compliant", "Partially compliant", "Non-compliant", "Not applicable"],
  "methodology": { "correctiveActions": { "standardDays": 30, "maxDays": 90 } },
  "sections": [
    {
      "id": "DATA",
      "name": "Data protection",
      "controls": [
        {
          "id": "DATA-01",
          "title": "Encryption in transit",
          "requirement": "What the control asks for, in a sentence or two.",
          "evidenceHints": ["TLS configuration"],
          "autoEvaluable": true,
          "scannerCategories": ["crypto"]
        }
      ]
    }
  ]
}
```

- **`id`** names the catalog in requests (`catalogId`, `catalogs` in a review). An assessment that names none uses `vendor-security` if installed, otherwise the first catalog by id.
- **`methodology.correctiveActions`** (optional): the default and longest deadline, in days, for an action plan.
- **`autoEvaluable`** says that technical evidence can settle a control; the others need an operator's answer.
- **`scannerCategories`** (optional) are the scanner finding categories that signal non-compliance: `secret`, `crypto`, `config`, `injection`, `deserialization`, `dependency`. Only controls with this field are judged by the deterministic engine: any matching finding in production code makes the control Non-compliant (the findings are attached as evidence), none makes it Compliant. Findings in test or documentation files, and findings the target suppressed, never fail a control. A control mapped to `dependency` is Compliant only when the dependency check actually ran. Keep mappings narrow: "Compliant" only means the scanner found nothing in those categories.

Write requirement text in your own words, or use a catalog whose licence allows it: do not copy a company's internal questionnaire or a standard's copyrighted text.
