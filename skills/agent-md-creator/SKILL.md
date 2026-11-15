---
name: agent-md-creator
description: >-
  Creates AGENT.md files for AI coding agent compatibility. 
  Use this skill whenever creating a new project, setting up AI agent documentation,
  or when the user mentions AGENT.md, agent configuration, or AI coding agent setup.
  Works with Claude Code, Cursor, GitHub Copilot, OpenAI Codex, Gemini CLI, Windsurf, and other AI coding tools.
version: 1.0.0
license: MIT
---

# AGENT.md Creator Skill

This skill creates AGENT.md files - the industry-standard configuration file for AI coding agents.

## What is AGENT.md?

AGENT.md is a markdown file placed at the project root that provides AI coding agents with project-specific instructions. Think of it as a "README for robots" - it tells AI agents:

- How to build, test, and run the project
- What coding standards to follow
- What the architecture looks like
- What boundaries to respect (what to never do)

**Adopted by 60,000+ repositories** and supported by all major AI coding tools:
- Claude Code, Cursor, GitHub Copilot, OpenAI Codex, Gemini CLI, Google Jules, Windsurf, Aider, Devin, and more.

**Governance**: Stewarded by the Agentic AI Foundation under the Linux Foundation.

---

## Workflow

### Step 1: Capture Intent

Ask the user these questions to understand the project:

1. **Project Type**: What kind of project is this? (web app, API, CLI tool, library, etc.)
2. **Tech Stack**: What technologies and versions? (e.g., React 18, TypeScript 5, Node.js 20, Python 3.11)
3. **Build Commands**: How do you install, build, test, and lint?
4. **Code Style**: Any specific conventions? (naming, formatting, patterns)
5. **Architecture**: Key architectural decisions or patterns?
6. **Boundaries**: What should the AI agent NEVER do? (e.g., never modify database migrations, never touch .env files)

### Step 2: Generate AGENT.md

Create the AGENT.md file with these standard sections:

```markdown
# AGENT.md

## Project Overview
<Brief description and tech stack with versions>

## Commands
<Install, build, test, lint commands>

## Code Style
<Naming conventions, formatting rules, patterns>

## Architecture
<Key architectural decisions, patterns, folder structure>

## Testing
<Test requirements, coverage expectations>

## Git Workflow
<Branch naming, commit conventions, PR requirements>

## Boundaries
### Always Do
<Things the agent must always do>

### Ask First
<Things requiring user confirmation>

### Never Do
<Things the agent must never do>
```

### Step 3: Validate

Ensure the AGENT.md follows best practices:
- [ ] Under 150-300 lines (LLMs reliably follow ~150-200 instructions)
- [ ] Specific commands, not vague instructions
- [ ] Clear three-tier boundary system
- [ ] Tech stack includes versions
- [ ] No secrets or sensitive data included

---

## Best Practices

### DO:
- **Be specific**: "Run `npm test -- --coverage`" not "Run tests"
- **Include versions**: "React 18, TypeScript 5.0" not "React and TypeScript"
- **Set clear boundaries**: Explicitly state what the agent should never touch
- **Use real commands**: Actual executable commands, not descriptions
- **Keep it current**: Update when tooling changes

### DON'T:
- **Be vague**: Avoid "follow best practices" - be specific
- **Over-document**: Don't include things linters/formatters handle
- **Auto-generate**: Human-written files work better
- **Include secrets**: Never put API keys, tokens, or sensitive data
- **Duplicate README**: README is for humans, AGENT.md is for agents

---

## Template Examples

### Web App (React/TypeScript)

```markdown
# AGENT.md

## Project Overview
E-commerce web application built with React 18, TypeScript 5.0, Vite, and Tailwind CSS.

## Commands
- Install: `npm install`
- Dev: `npm run dev`
- Build: `npm run build`
- Test: `npm test`
- Lint: `npm run lint`

## Code Style
- Use functional components with hooks
- Follow Tailwind CSS utility-first approach
- Use kebab-case for file names
- Components in `src/components/`, pages in `src/pages/`

## Architecture
- Feature-based folder structure
- React Router for navigation
- Zustand for state management
- React Query for API calls

## Testing
- Vitest for unit tests
- Testing Library for component tests
- Aim for 80% coverage on new features

## Boundaries
### Always Do
- Write tests for new components
- Follow existing component patterns
- Use TypeScript strict mode

### Ask First
- Adding new dependencies
- Changing folder structure
- Modifying build configuration

### Never Do
- Modify .env files
- Change database migrations
- Delete existing tests without replacement
- Push directly to main branch
```

### Python API (FastAPI)

```markdown
# AGENT.md

## Project Overview
REST API built with FastAPI, Python 3.11, SQLAlchemy, and PostgreSQL.

## Commands
- Install: `pip install -r requirements.txt`
- Dev: `uvicorn app.main:dev --reload`
- Test: `pytest -v`
- Lint: `ruff check .`
- Format: `black .`

## Code Style
- Follow PEP 8
- Use type hints everywhere
- Async functions for database operations
- Pydantic models for validation

## Architecture
- Layered architecture: routes → services → repositories
- Dependency injection via FastAPI Depends
- Alembic for migrations

## Testing
- pytest with pytest-asyncio
- Test coverage minimum 70%
- Factory Boy for test data

## Boundaries
### Always Do
- Add tests for new endpoints
- Create migrations for schema changes
- Use async database sessions

### Ask First
- Adding new dependencies
- Changing API versioning
- Modifying authentication logic

### Never Do
- Run migrations on production directly
- Hardcode credentials
- Skip input validation
- Delete migrations
```

---

## When to Use Nested AGENT.md

For monorepos, place AGENT.md files in subdirectories:

```
my-monorepo/
├── AGENTS.md          # Root-level shared instructions
├── frontend/
│   └── AGENTS.md      # Frontend-specific instructions
├── backend/
│   └── AGENTS.md      # Backend-specific instructions
└── shared/
    └── AGENTS.md      # Shared library instructions
```

Agents read the nearest AGENT.md in the directory tree.

---

## Output

After creating AGENT.md:
1. Save it to the project root
2. Confirm with the user that it matches their expectations
3. Suggest adding it to version control

---

## References

- [AGENTS.md Official](https://agentsmd.online/)
- [GitHub Blog: How to write a great agents.md](https://github.blog/ai-and-ml/github-copilot/how-to-write-a-great-agents-md-lessons-from-over-2500-repositories/)
- [Agentic AI Foundation](https://aaif.io)