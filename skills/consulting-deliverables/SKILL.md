---
name: consulting-deliverables
description: "Use this skill whenever the user wants a polished, executive-grade DELIVERABLE they can download — a slide deck, dashboard, board presentation, market/research report, financial model, one-pager, or 'McKinsey/BCG-style' output with charts and KPIs. Triggers on: 'presentation', 'deck', 'slides', 'dashboard', 'report', 'PDF', 'spreadsheet', 'Excel', 'Word doc', 'pptx', 'consultant-style', 'board-ready', 'investor deck', 'KPIs', 'charts and graphs'. Produces REAL downloadable files (.pptx/.xlsx/.docx/.pdf), not markdown."
license: Apache-2.0
---

# Consulting-Grade Deliverables

Produce **real, downloadable** documents that look like they came from a top-tier strategy
consultancy — clean, data-driven, restrained. Never answer a "make me a deck/dashboard/report"
request with markdown or a text description: generate the actual file.

## Tools (all output appears on the Agent Computer with a Download button)

| Tool | Produces | Use for |
|------|----------|---------|
| `generate_pptx` | `.pptx` | Slide decks, board/investor presentations, dashboards-as-slides |
| `generate_pdf`  | `.pdf`  | Rich reports, one-pagers, KPI dashboards (best visual control) |
| `generate_xlsx` | `.xlsx` | Financial models, data tables, trackers |
| `generate_docx` | `.docx` | Long-form reports, memos, proposals |

## Workflow

1. **Get real data first.** If the deliverable is data-driven, gather actual numbers via
   `web_search` / `read_url`, or use figures the user gave you. NEVER fabricate statistics —
   feed concrete, sourced numbers into charts and tables.
2. **Structure before styling.** Decide the narrative: title → executive summary → 3–5 body
   sections (each ideally one insight + one supporting chart/table) → recommendation/next steps.
   This is the classic "pyramid principle" — lead with the answer, then support it.
3. **Generate the file** with the matching tool.
4. **Confirm** the file was created (the tool returns the filename + it appears on Agent Computer).

## Making it look McKinsey-grade

- **One idea per slide/section.** The title should state the *insight* ("Cloud spend grew 3× while
  headcount stayed flat"), not the topic ("Cloud Spend").
- **Chart > table > bullets > paragraph.** Prefer a single clear chart to a wall of text.
- **Few numbers, big.** Use KPI cards for the 3–5 numbers that matter (in `generate_pdf`:
  `<div class="kpi"><span class="num">42%</span><span class="label">YoY growth</span></div>`).
- **Restraint.** The built-in navy/teal theme is deliberate — don't fight it with rainbow colors.
- **Sourced.** Add a small "Source: …" line under charts built from external data.

## Dashboard recipe (generate_pdf)

For a "dashboard" or "Power BI-style" ask (note: real Power BI `.pbix` can't be generated — deliver
an equivalent visual dashboard as PDF/PPTX instead):
- Top row: 3–4 KPI cards.
- Middle: 1–2 charts (inline `<svg>` for bar/line, or a table styled as a heat-strip).
- Bottom: a compact data table with the underlying figures.
- Keep it to one page; lead with the headline metric.

## Example (deck)

`generate_pptx({ filename, title, subtitle, slides: [
  { title: "Market grew to $566B by 2027", bullets: ["…","…"],
    chart: { type: "bar", categories: ["2021","2024","2027"], series: [{ name: "Market $B", values: [384, 478, 566] }] } },
  { title: "Where the growth concentrates", table: { headers: ["Segment","CAGR"], rows: [["Industrial","12%"],["Consumer","7%"]] } }
]})`
