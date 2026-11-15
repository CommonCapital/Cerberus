# Contributing to Cerberus

Thank you for helping. This guide covers how to set up, what a change needs before review, and how to report problems.

By contributing you agree that your contribution is licensed under the [Apache License 2.0](LICENSE), as the licence itself provides (section 5), and you confirm that you have the right to submit it.

## Before you start

- **Bugs and ideas:** open an issue first for anything larger than a small fix, so the approach can be agreed before you spend time on it.
- **Security problems:** do not open an issue. Follow [SECURITY.md](SECURITY.md).
- **Responsible use:** Cerberus scans and, in later phases, tests systems. Contributions that make it act on a target without the authorization and consent steps it already has (engagements, scope, approval of consequential tools) will not be accepted.

## Setting up

| What | Version |
|---|---|
| .NET SDK | 10.0 |
| Bun | 1.3 or later |
| Optional, for the tests that need them | `redis-server`, `rabbitmq-server`, a PostgreSQL server |

```bash
git clone <this repository>
cd cerberus
bun install
cd backend && dotnet build Cerberus.slnx
```

Run the backend with `dotnet run --project backend/src/Cerberus.Api` and the CLI with `bun run dev:cli`. The [README](README.md) explains the settings; none is required to start.

## Tests

Every change comes with tests, and the whole suite must pass before review.

```bash
# Backend: hosts the real API in-process, with stand-ins for models and the web.
cd backend && dotnet test Cerberus.slnx

# The same suite on PostgreSQL (a server you can create databases on; each run makes and drops its own):
CERBERUS_TEST_POSTGRES_URL=postgres://user:password@localhost:5432 dotnet test Cerberus.slnx

# CLI: types and tests.
cd packages/cli && bunx tsc --noEmit && bun test src
```

Tests that need Redis, RabbitMQ or PostgreSQL are skipped, with a reason, when the server is not available; the rest run anywhere.

- **No real model calls in tests.** Use the scripted providers the tests already have. A test must not need an API key, a GPU or the internet.
- **No real targets.** Tests scan the files they create themselves.
- **Timing:** wait for a condition with a deadline rather than sleeping a fixed time; machines in CI are slower than yours.

## Making a change

- Keep a pull request to one subject. Refactoring that is not needed for it goes in its own.
- Follow the style of the code around you: names that say what a thing is, comments that say why, user-facing messages in plain words.
- Never log or return a secret: API keys, passwords, tokens and addresses with credentials stay out of logs, errors, health output and test output.
- Update the README (or `backend/README.md`, `catalogs/README.md`) when behaviour, a setting or an endpoint changes.
- New settings are validated at start-up (`ServiceConfig`) and documented with their default.
- New dependencies need a reason in the pull request, a licence compatible with Apache-2.0, and an exact version.

## Pull requests

Describe what changed and why, how you tested it, and anything left undone. A maintainer reviews it; CI must be green before it is merged.

## Catalogs and skills

- A compliance catalog in `catalogs/` must be your own wording or under a licence that allows redistribution. Do not add a company's internal questionnaire or a standard's copyrighted text; summarize in your own words and link to the source.
- `security-skills/` is third-party material under Apache-2.0. Keep its licence files and attribution; send changes to the skills themselves upstream.
