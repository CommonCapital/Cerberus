---
name: platform-documentation
description: Generate end-to-end documentation of a live web platform by driving a real browser through it — sliding page by page, testing flows, capturing screenshots, and writing a description of every screen and feature. Use when the user wants to "document a platform/website/app", "walk through it and describe it", "make a user guide with screenshots", or "test and document" a site. Powered by the interactive browser_action tool.
---

# Platform Documentation

Produce a complete, screenshot-rich documentation of a whole web platform by
actually navigating it with the persistent `browser_action` tool — not by
guessing from its marketing copy. The output is a Markdown document where every
feature/screen has: a real screenshot, a description of what it is, and how to
use it.

## The engine: `browser_action`

`browser_action` drives ONE live headless browser session that stays open across
turns (cookies, current page, and scroll position persist). Every visual action
also streams the screenshot into the chat UI, so the user watches you slide
through the platform live.

Actions: `goto`, `click`, `fill`, `type`, `press`, `scroll`, `back`,
`screenshot`, `get_links`, `get_text`, `close`.

`selector` is any Playwright selector: CSS (`#login`, `input[name=email]`) or the
text engine (`text=Sign in`, `text=Get started`).

> Use `browser_action` for exploration/interaction. `take_screenshot` is only for
> a single one-shot picture of one static URL — don't use it to walk a platform.

## Inputs to confirm before starting

1. **Start URL** of the platform. (Required.)
2. **Scope** — public/marketing pages only, or also the authenticated app?
3. **Login** — if the app is behind auth, the user must provide credentials, OR
   log in themselves first. NEVER invent credentials, and never sign the user up.
4. **Depth** — Surface (top nav pages only), Standard (nav + one level deep), or
   Deep (follow every discovered internal link once).

If any are missing, ask ONCE, then proceed with sensible defaults
(Standard depth, public scope).

## Workflow

### Phase 1 — Plan (write_todos)
Create a TODO list: one task to map the site, one task per major section to
document, and a final task to assemble the document. Assign the section tasks to
named subagents when the platform is large (each subagent documents one section
and writes a `report-<section>.md`), and keep assembly for yourself.

### Phase 2 — Map the platform
1. `browser_action { action: "goto", url: <start_url> }` — land on the entry page.
2. `browser_action { action: "get_links" }` — list the visible nav/links.
3. Build the page inventory: pick the distinct, meaningful destinations
   (dedupe anchors, skip external/social/`mailto:` links). This inventory IS your
   documentation outline.

### Phase 3 — Document each page (repeat per page)
For every page in the inventory:
1. Navigate to it (`goto`, or `click` a nav item to test the real flow).
2. `screenshot` (use `fullPage: true` for long pages) — this is the figure.
3. `get_text` — read the real on-page content so the description is accurate.
4. If the page has an interactive element central to it (search box, filter,
   tabs, an expandable menu), TEST it: `fill`/`type` + `press`, or `click`, then
   `screenshot` the result. Document what happened.
5. Write a section to a report file:
   - **Heading**: the page/feature name.
   - **Screenshot**: `![...](/images/<filename>.png)` using the `screenshot` src.
   - **What it is**: 1–3 sentences from what you actually saw.
   - **How to use it**: the concrete steps / controls on the page.
   - **Notes**: anything broken, slow, or confusing you observed while testing.

### Phase 4 — Test key flows (if in scope)
Walk the primary user journeys end to end (e.g. sign-up → onboarding, search →
result → detail, create → save). Screenshot each step. Only submit forms the user
authorized; never complete a purchase, payment, or destructive/irreversible
action — screenshot the pre-submit state and note it instead.

### Phase 5 — Assemble
Combine the section reports into one `PLATFORM-DOCUMENTATION.md`:
1. Title + one-paragraph overview of what the platform is (synthesized from what
   you saw).
2. A table of contents.
3. The per-page/feature sections in navigation order, each with its screenshot.
4. A short "Flows tested" section summarizing the journeys and their result.
Then `browser_action { action: "close" }` to free the browser.

## Rules

- **Describe only what you actually saw.** Every claim must trace to a real
  screenshot or `get_text`. No inventing features.
- **Screenshots are mandatory** — a section without one is incomplete.
- **Recover, don't stall.** If a `selector` fails, call `get_links`/`get_text` or
  take a `screenshot` to re-orient, then retry with a corrected selector.
- **Safety.** Do not enter credentials the user didn't give you; do not solve
  CAPTCHAs; do not submit payments or irreversible actions. Note the boundary and
  move on.
- **Close the session** with the `close` action when the run finishes.

## Output

A single `PLATFORM-DOCUMENTATION.md` in the workspace (plus per-section
`report-*.md` files if subagents were used), with every screenshot saved under
`/public/images` and referenced by its `/images/<file>.png` URL.
