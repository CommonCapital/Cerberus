import { afterAll, describe, expect, test } from "bun:test";
import { mkdtempSync, rmSync } from "node:fs";
import { createServer } from "node:net";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { answers, ensureBackend, isLocalAddress, startTypeScriptServer, type Starter } from "./backend-fallback";

const never: Starter = async () => {
  throw new Error("must not be started");
};

describe("which server the CLI talks to", () => {
  test("a server that answers is used as it is", async () => {
    const status = await ensureBackend({ url: "http://localhost:3000", probe: async () => true, start: never });
    expect(status).toEqual({ kind: "answering", url: "http://localhost:3000" });
  });

  test("with nothing answering on this machine, the TypeScript server is started", async () => {
    let started = "";
    const status = await ensureBackend({
      url: "http://127.0.0.1:3999",
      probe: async () => false,
      start: async (url) => {
        started = url;
        return { log: "/tmp/log", stop: () => {} };
      },
    });
    expect(started).toBe("http://127.0.0.1:3999");
    expect(status.kind).toBe("started");
  });

  test("a server on another machine is never started for it", async () => {
    const status = await ensureBackend({ url: "https://cerberus.example.com", probe: async () => false, start: never });
    expect(status).toMatchObject({ kind: "not-started", reason: "nothing answers, and the address is not on this machine" });
  });

  test("CERBERUS_AUTOSTART=off leaves it to the operator", async () => {
    const status = await ensureBackend({ url: "http://localhost:3000", autostart: "off", probe: async () => false, start: never });
    expect(status).toMatchObject({ kind: "not-started", reason: "nothing answers and CERBERUS_AUTOSTART is off" });
  });

  test("a start that fails is said, not thrown", async () => {
    const status = await ensureBackend({
      url: "http://localhost:3000",
      probe: async () => false,
      start: async () => {
        throw new Error("the TypeScript server did not start (see log)");
      },
    });
    expect(status).toMatchObject({ kind: "not-started", reason: "the TypeScript server did not start (see log)" });
  });

  test("only addresses on this machine count as local", () => {
    for (const url of ["http://localhost:3000", "http://127.0.0.1:3000", "http://[::1]:3000", "http://api.localhost"]) expect(isLocalAddress(url)).toBe(true);
    for (const url of ["http://10.0.0.5:3000", "https://cerberus.example.com", "not a url"]) expect(isLocalAddress(url)).toBe(false);
  });
});

describe("the TypeScript server, really started", () => {
  const scratch = mkdtempSync(join(tmpdir(), "cerberus-fallback-"));
  const before = { home: process.env.CERBERUS_HOME, db: process.env.DATABASE_URL };
  let stop = () => {};
  afterAll(() => {
    stop();
    process.env.CERBERUS_HOME = before.home;
    if (before.db === undefined) delete process.env.DATABASE_URL;
    else process.env.DATABASE_URL = before.db;
    rmSync(scratch, { recursive: true, force: true });
  });

  async function freePort(): Promise<number> {
    return await new Promise((resolve) => {
      const server = createServer().listen(0, "127.0.0.1", () => {
        const { port } = server.address() as { port: number };
        server.close(() => resolve(port));
      });
    });
  }

  test("it starts on the address, on its own database, and lets someone sign up", async () => {
    process.env.CERBERUS_HOME = join(scratch, "home");
    process.env.DATABASE_URL = `file:${join(scratch, "fallback.db")}`;
    const url = `http://127.0.0.1:${await freePort()}`;
    expect(await answers(url, 300)).toBe(false);

    const server = await startTypeScriptServer(url);
    stop = server.stop;

    const response = await fetch(`${url}/auth/register`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ email: "fallback@test.dev", password: "secret123" }),
    });
    expect(response.status).toBe(201);
    expect(typeof ((await response.json()) as { token?: unknown }).token).toBe("string");
    expect(server.log).toBe(join(scratch, "home", "local-server.log"));

    server.stop();
    await Bun.sleep(300);
    expect(await answers(url, 300)).toBe(false);
  }, 60_000);
});
