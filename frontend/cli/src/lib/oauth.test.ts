import { expect, test } from "bun:test";
import { performLogin, type LoginDeps } from "./oauth";
import type { Credentials } from "./api-client";

function harness(replies: Response[], configured: { email?: string; password?: string } = {}) {
  const calls: { action: string; credentials: Credentials }[] = [];
  const saved: { token: string }[] = [];
  const reply = async (action: string, credentials: Credentials): Promise<Response> => {
    calls.push({ action, credentials });
    const next = replies.shift();
    if (!next) throw new Error("No reply arranged");
    return next;
  };
  const deps: LoginDeps = {
    login: (credentials) => reply("login", credentials),
    register: (credentials) => reply("register", credentials),
    saveAuth: (auth) => { saved.push(auth); },
    email: () => configured.email,
    password: () => configured.password,
  };
  return { deps, calls, saved };
}

const refused = () => Response.json({ error: "Invalid email or password" }, { status: 401 });

test("sign-in persists the returned token and uses the operator's credentials", async () => {
  const h = harness([Response.json({ token: "current" })], { email: "owner@example.com", password: "own-password" });
  expect(await performLogin(h.deps)).toEqual({ token: "current" });
  expect(h.saved).toEqual([{ token: "current" }]);
  expect(h.calls).toEqual([{ action: "login", credentials: { email: "owner@example.com", password: "own-password" } }]);
});

test("a first default account is registered only after sign-in is refused", async () => {
  const h = harness([refused(), Response.json({ token: "first-account" }, { status: 201 })]);
  expect(await performLogin(h.deps)).toEqual({ token: "first-account" });
  expect(h.calls.map((call) => call.action)).toEqual(["login", "register"]);
  expect(h.calls[1]!.credentials).toEqual({ email: "local@cerberus.local", password: "local-dev-password" });
  expect(h.saved).toEqual([{ token: "first-account" }]);
});

test("an explicit account is used for registration too", async () => {
  const h = harness([refused(), Response.json({ token: "new-explicit" }, { status: 201 })], { email: "explicit@example.com" });
  await performLogin(h.deps);
  expect(h.calls.map((call) => [call.action, call.credentials.email])).toEqual([
    ["login", "explicit@example.com"], ["register", "explicit@example.com"],
  ]);
});

for (const status of [400, 429, 500]) {
  test(`a primary login failure (${status}) never registers an account or tries another`, async () => {
    const h = harness([Response.json({ error: `Login unavailable ${status}` }, { status })]);
    await expect(performLogin(h.deps)).rejects.toThrow(`Login unavailable ${status}`);
    expect(h.calls.map((call) => call.action)).toEqual(["login"]);
    expect(h.saved).toEqual([]);
  });
}

test("registration errors reach the operator without saving anything", async () => {
  const h = harness([refused(), Response.json({ error: "Email is already registered" }, { status: 409 })], { email: "owner@example.com" });
  await expect(performLogin(h.deps)).rejects.toThrow("Email is already registered");
  expect(h.saved).toEqual([]);
});

for (const body of [null, {}, { token: null }, { token: 1 }, { token: "" }, { token: "  " }]) {
  test(`an invalid successful login never saves credentials (${JSON.stringify(body)})`, async () => {
    const h = harness([Response.json(body)]);
    await expect(performLogin(h.deps)).rejects.toThrow("Login response did not include a token");
    expect(h.calls.map((call) => call.action)).toEqual(["login"]);
    expect(h.saved).toEqual([]);
  });
}

test("a registration response must contain a token too", async () => {
  const h = harness([refused(), Response.json({ success: true }, { status: 201 })], { email: "owner@example.com" });
  await expect(performLogin(h.deps)).rejects.toThrow("Login response did not include a token");
  expect(h.saved).toEqual([]);
});

test("network failures do not trigger account creation", async () => {
  const h = harness([]);
  await expect(performLogin(h.deps)).rejects.toThrow("No reply arranged");
  expect(h.calls.map((call) => call.action)).toEqual(["login"]);
  expect(h.saved).toEqual([]);
});
