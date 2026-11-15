# General skills

Skills for work around a security engagement: documents and reports, browser automation and web testing, research, sandboxed code, MCP servers, and writing new skills. They sit next to the security library in `security-skills/`; agents search both and load only what fits the task.

Each folder holds a `SKILL.md`: front matter with `name` and `description`, then the instructions. The backend reads this folder and `security-skills/skills` by default; `CERBERUS_SKILLS_ROOT` takes a list of folders separated like `PATH`, and when two folders hold a skill of the same name, the earlier folder's is used.

| Group | Skills |
|---|---|
| Documents and reports | `docx`, `pdf`, `pptx`, `xlsx`, `doc-coauthoring`, `consulting-deliverables`, `internal-comms`, `blog-post-writer` |
| Browser and web testing | `webapp-testing`, `playwright-skill`, `playwright-python-skill`, `platform-documentation`, `web-research` |
| Sandboxed code and apps | `sandbox`, `sandbox-output`, `nodejs-sandbox`, `flask-app-builder`, `static-website-asset-paths`, `project-organization`, `workspace-cleaner` |
| Building tools and skills | `mcp-builder`, `skill-creator`, `agent-md-creator`, `claude-api` |
| Design | `frontend-design`, `web-artifacts-builder`, `theme-factory`, `canvas-design`, `algorithmic-art`, `brand-guidelines`, `slack-gif-creator` |

## Licences

- Skills with a `LICENSE.txt` in their folder come from [anthropics/skills](https://github.com/anthropics/skills) and are licensed by Anthropic, PBC under the Apache License 2.0; the file is the full licence. `brand-guidelines` describes Anthropic's own brand.
- `agent-md-creator` and `web-research` are MIT-licensed, as their front matter states.
- The others are part of Cerberus and licensed with it under the Apache License 2.0.

Several skills refer to helper scripts that ship with their original packages; only the instructions are included here.
