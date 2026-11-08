# Cerberus backend (.NET)

The .NET port of the Cerberus server (milestone **M2**). Fully local: ASP.NET Core + EF Core (SQLite) + SignalR, with no hosted services. This runs alongside the TypeScript baseline during the transition and will eventually replace `packages/server`.

## Projects

| Project | Role |
|---|---|
| `Cerberus.Api` | ASP.NET Core minimal API + SignalR hub; auth, sessions, chat endpoints |
| `Cerberus.Core` | Domain abstractions: model registry, `IModelProvider`, password hashing |
| `Cerberus.Data` | EF Core `DbContext` + entities (`User`, `AuthSession`, `Session`), SQLite |
| `Cerberus.Models` | `IModelProvider` implementations: Anthropic, OpenAI, Ollama (+ resolver) |

## Run

```bash
cd backend
dotnet run --project src/Cerberus.Api
```

Environment:

- `PORT` — HTTP port (default `3000`).
- `CERBERUS_DB` — SQLite file path (default `cerberus.db` under the API content root). The schema is created automatically on first run.
- `ANTHROPIC_API_KEY` / `OPENAI_API_KEY` — user-supplied keys for cloud models (optional).
- `OLLAMA_BASE_URL` — local Ollama endpoint (default `http://localhost:11434/v1`). Use Ollama for fully offline operation.

## Endpoints

| Method | Path | Auth | Notes |
|---|---|---|---|
| GET | `/health` | — | Liveness |
| POST | `/auth/register` | — | `{ email, password }` → `{ token }` |
| POST | `/auth/login` | — | `{ email, password }` → `{ token }` |
| POST | `/auth/logout` | Bearer | Revokes the token |
| GET | `/sessions` | Bearer | List sessions |
| GET | `/sessions/{id}` | Bearer | Session + parsed messages |
| POST | `/sessions` | Bearer | `{ title }` → created session |
| POST | `/chat` | Bearer | SSE stream of `{ delta }` then `[DONE]`; persists messages |
| WS | `/hub/chat` | — (skeleton) | SignalR streaming hub |

## Status & follow-ups

- Auth, sessions, and the streaming chat pipeline (provider resolver → Anthropic/OpenAI/Ollama → SSE → persistence) are implemented and smoke-tested.
- **Follow-ups:** emit the exact AI-SDK UI-message stream protocol (so the current TS CLI's transport works unchanged) and retarget the CLI; add per-user encrypted API-key storage; add SignalR hub auth; wrap tool-calling.
