import { afterEach, beforeEach, expect, test } from "bun:test";
import { existsSync, mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { clearAuth, getAuth, saveAuth } from "./auth";
import { createApiClient } from "./api-client";

let home = "";
const before = { home: process.env.CERBERUS_HOME, token: process.env.CERBERUS_TOKEN };

beforeEach(() => {
  home = mkdtempSync(join(tmpdir(), "cerberus-auth-"));
  process.env.CERBERUS_HOME = home;
  delete process.env.CERBERUS_TOKEN;
});

afterEach(() => {
  rmSync(home, { recursive: true, force: true });
  if (before.home === undefined) delete process.env.CERBERUS_HOME;
  else process.env.CERBERUS_HOME = before.home;
  if (before.token === undefined) delete process.env.CERBERUS_TOKEN;
  else process.env.CERBERUS_TOKEN = before.token;
});

test("a saved sign-in is read back, and cleared on sign-out", () => {
  expect(getAuth()).toBeNull();
  saveAuth({ token: "saved" });
  expect(getAuth()).toEqual({ token: "saved" });
  clearAuth();
  expect(getAuth()).toBeNull();
});

test("a token from the environment wins over the saved one", () => {
  saveAuth({ token: "saved" });
  process.env.CERBERUS_TOKEN = "  from-the-pipeline\n";
  expect(getAuth()).toEqual({ token: "from-the-pipeline" });
});

test("a refused token from the environment does not cost the saved sign-in", () => {
  saveAuth({ token: "saved" });
  process.env.CERBERUS_TOKEN = "expired";

  clearAuth();   // what a 401 from the server leads to

  expect(existsSync(join(home, "auth.json"))).toBe(true);
  delete process.env.CERBERUS_TOKEN;
  expect(getAuth()).toEqual({ token: "saved" });
});

const refusingClient = () => createApiClient({
  baseUrl: () => "http://test.invalid",
  getAuth,
  clearAuth,
  fetch: async () => Response.json({ error: "Unauthorized" }, { status: 401 }),
});

test("a 401 through the session client removes an expired saved sign-in", async () => {
  saveAuth({ token: "expired" });
  await expect(refusingClient().sessions()).rejects.toThrow("Unauthorized");
  expect(getAuth()).toBeNull();
  expect(existsSync(join(home, "auth.json"))).toBe(false);
});

test("a 401 through the session client leaves a saved sign-in alone when the environment token was used", async () => {
  saveAuth({ token: "saved" });
  process.env.CERBERUS_TOKEN = "expired-pipeline-token";
  await expect(refusingClient().sessions()).rejects.toThrow("Unauthorized");
  delete process.env.CERBERUS_TOKEN;
  expect(getAuth()).toEqual({ token: "saved" });
});
