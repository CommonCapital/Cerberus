import { expect, test } from "bun:test";
import { createApiClient, type ApiClientDeps, type SessionData } from "./api-client";
import { ApiError } from "./http-transport";

function harness(replies: Response[]) {
  let address = "http://localhost:3000///";
  let token: string | null = "first-token";
  let cleared = 0;
  const calls: { url: string; init: RequestInit }[] = [];
  const deps: ApiClientDeps = {
    baseUrl: () => address,
    getAuth: () => token === null ? null : { token },
    clearAuth: () => { cleared++; token = null; },
    fetch: async (url, init) => {
      calls.push({ url, init });
      const reply = replies.shift();
      if (!reply) throw new Error("No reply arranged");
      return reply;
    },
  };
  return {
    client: createApiClient(deps),
    calls,
    cleared: () => cleared,
    change: (server: string, auth: string | null) => { address = server; token = auth; },
  };
}

const credentials = { email: "local@cerberus.local", password: "local-dev-password" };
const session: SessionData = {
  id: "s1",
  userId: "u1",
  title: "Review the code",
  createdAt: "2026-11-15T12:00:00Z",
  updatedAt: "2026-11-15T12:00:01Z",
  messages: [],
  parentId: null,
};

test("auth posts credentials as JSON and keeps the login/registration status and body", async () => {
  const h = harness([
    Response.json({ token: "signed-in" }),
    Response.json({ token: "registered" }, { status: 201 }),
  ]);
  const login = await h.client.login(credentials);
  expect(login.status).toBe(200);
  expect(await login.json()).toEqual({ token: "signed-in" });
  const register = await h.client.register(credentials);
  expect(register.status).toBe(201);
  expect(await register.json()).toEqual({ token: "registered" });
  expect(h.calls.map((call) => call.url)).toEqual([
    "http://localhost:3000/auth/login", "http://localhost:3000/auth/register",
  ]);
  for (const call of h.calls) {
    expect(call.init.method).toBe("POST");
    expect(new Headers(call.init.headers).get("content-type")).toBe("application/json");
    expect(JSON.parse(call.init.body as string)).toEqual(credentials);
  }
});

test("the address, token and chat URL are read again after a server or account change", async () => {
  const h = harness([Response.json([]), Response.json([])]);
  await h.client.sessions();
  expect(h.client.chatUrl()).toBe("http://localhost:3000/chat");
  h.change("https://second.example/prefix/", "second-token");
  await h.client.sessions();
  expect(h.client.chatUrl()).toBe("https://second.example/prefix/chat");
  expect(h.calls.map((call) => call.url)).toEqual([
    "http://localhost:3000/sessions", "https://second.example/prefix/sessions",
  ]);
  expect(h.calls.map((call) => new Headers(call.init.headers).get("authorization"))).toEqual([
    "Bearer first-token", "Bearer second-token",
  ]);
});

test("signed-out GETs send neither an authorization header nor a JSON body", async () => {
  const h = harness([Response.json([])]);
  h.change("http://localhost:3000", null);
  expect(await h.client.sessions()).toEqual([]);
  const init = h.calls[0]!.init;
  expect(init.method).toBe("GET");
  expect(init.body).toBeUndefined();
  expect(new Headers(init.headers).has("authorization")).toBe(false);
  expect(new Headers(init.headers).has("content-type")).toBe(false);
});

test("session lists preserve the .NET summary shape and timestamps", async () => {
  const summaries = [{ id: session.id, title: session.title, createdAt: session.createdAt }];
  const h = harness([Response.json(summaries)]);
  expect(await h.client.sessions()).toEqual(summaries);
});

test("loaded sessions keep messages, task data, and nullable fields without reshaping them", async () => {
  const stored: SessionData = {
    ...session,
    summarizedThrough: null,
    tasks: [{ id: "t1", content: "Read auth", status: "pending", dependsOn: [] }],
    messages: [{ id: "m1", role: "assistant", parts: [{ type: "tool-readFile", output: { text: "code" } }] }],
  };
  const h = harness([Response.json(stored)]);
  expect(await h.client.session("s1")).toEqual(stored);
});

test("session ids cannot turn into another path or a query string", async () => {
  const h = harness([Response.json(session)]);
  await h.client.session("folder/name?query#fragment");
  expect(h.calls[0]!.url).toBe("http://localhost:3000/sessions/folder%2Fname%3Fquery%23fragment");
});

test("creating ordinary and helper sessions keeps parentId and 201 responses", async () => {
  const h = harness([
    Response.json(session, { status: 201 }),
    Response.json({ ...session, id: "child", parentId: "s1" }, { status: 201 }),
  ]);
  expect(await h.client.createSession({ title: session.title })).toEqual(session);
  expect(await h.client.createSession({ title: "Check auth", parentId: "s1" })).toMatchObject({ id: "child", parentId: "s1" });
  expect(h.calls.map((call) => JSON.parse(call.init.body as string))).toEqual([
    { title: session.title }, { title: "Check auth", parentId: "s1" },
  ]);
});

test("a refused sign-in remains readable and clears stale auth once", async () => {
  const h = harness([Response.json({ error: "Invalid email or password" }, { status: 401 })]);
  const reply = await h.client.login(credentials);
  expect(reply.status).toBe(401);
  expect(await reply.json()).toEqual({ error: "Invalid email or password" });
  expect(h.cleared()).toBe(1);
});

test("a protected endpoint's 401 preserves the backend message and invalidates auth", async () => {
  const h = harness([Response.json({ error: "Unauthorized" }, { status: 401 })]);
  try {
    await h.client.sessions();
    throw new Error("Expected a refusal");
  } catch (error) {
    expect(error).toBeInstanceOf(ApiError);
    expect(error).toMatchObject({ status: 401, message: "Unauthorized" });
  }
  expect(h.cleared()).toBe(1);
});

test("missing sessions and throttled requests keep their error messages without signing out", async () => {
  const h = harness([
    Response.json({ error: "Session not found" }, { status: 404 }),
    Response.json({ error: "Too many requests" }, { status: 429 }),
  ]);
  await expect(h.client.session("missing")).rejects.toThrow("Session not found");
  await expect(h.client.sessions()).rejects.toThrow("Too many requests");
  expect(h.cleared()).toBe(0);
});

test("non-JSON and empty error bodies fall back to the HTTP status", async () => {
  const h = harness([
    new Response("<h1>Bad gateway</h1>", { status: 502, statusText: "Bad Gateway" }),
    new Response(null, { status: 503 }),
    Response.json(null, { status: 500 }),
  ]);
  await expect(h.client.sessions()).rejects.toThrow("Bad Gateway");
  await expect(h.client.sessions()).rejects.toThrow("503");
  await expect(h.client.sessions()).rejects.toThrow("500");
});

test("session endpoints never pretend an empty or malformed success body is a session", async () => {
  const h = harness([new Response("not-json"), new Response(null, { status: 204 })]);
  await expect(h.client.session("s1")).rejects.toThrow("invalid JSON response");
  await expect(h.client.createSession({ title: "new" })).rejects.toThrow("invalid JSON response");
});

test("an unreachable server has a useful error and does not invalidate the sign-in", async () => {
  const h = harness([]);
  await expect(h.client.sessions()).rejects.toThrow("Cannot reach the server");
  expect(h.cleared()).toBe(0);
});
