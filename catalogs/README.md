# Compliance catalogs

The Compliance engine grades a target against control catalogs. A catalog is data, not code: one JSON file per catalog in this folder (or wherever `CERBERUS_CATALOG_ROOT` points), read at start-up. An installation brings its own catalogs; none are published with Cerberus. Without any, everything else works and readiness reports `catalogs` as degraded.

## Format

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
