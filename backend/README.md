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
| GET | `/settings/keys` | Bearer | Which providers have a key set (never returns the key) |
| PUT | `/settings/keys` | Bearer | `{ provider, apiKey }` — store a key (encrypted at rest) |
| DELETE | `/settings/keys/{provider}` | Bearer | Remove a stored key |
| POST | `/chat` | Bearer | AI-SDK UI-message stream; CLI-compatible body `{ id, messages, mode, model }`; persists UIMessages |
| WS | `/hub/chat` | — (skeleton) | SignalR streaming hub |

### Per-user API keys

Each user stores their own Anthropic/OpenAI keys via `/settings/keys`. Keys are **encrypted at rest** with ASP.NET Core Data Protection (keyring under `dp-keys/`, or `CERBERUS_KEYRING`), never returned by the API, and decrypted in-memory only to authorize a `/chat` request — taking precedence over the `*_API_KEY` env vars. Ollama models need no key.

### `/chat` protocol

The endpoint speaks the **AI-SDK UI-message stream** wire format, so `@ai-sdk/react`'s `DefaultChatTransport` consumes it unchanged: `text/event-stream` with `x-vercel-ai-ui-message-stream: v1`, chunks framed as `data: {json}\n\n` in the sequence `start` → `start-step` → `text-start` → `text-delta*` → `text-end` → `finish-step` → `finish` → `[DONE]`, with `messageMetadata` carrying mode/model/durationMs. Sessions store and return UIMessages (`{ id, role, parts }`). A dev model id `echo` streams a canned reply with no API key for headless verification.

## Status & follow-ups

- Auth, sessions, the streaming chat pipeline (provider resolver → Anthropic/OpenAI/Ollama → UI-message stream → persistence), and AI-SDK protocol compatibility are implemented and verified (raw SSE checked against the `ai` package format; UIMessage persistence round-trips).
- Per-user **encrypted** API-key storage is implemented (`/settings/keys`, Data Protection) and used by `/chat`.
- **Follow-ups:** retarget the TS CLI at this server (swap the hono client base URL / calls); SignalR hub auth; tool-calling (tool-input/tool-output UI chunks + client execution).
