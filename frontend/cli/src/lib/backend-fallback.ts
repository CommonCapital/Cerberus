import { mkdirSync, openSync } from "node:fs";
import { join, resolve } from "node:path";
import { cerberusHome } from "./home";

// When nothing answers at the server address and that address is on this
// machine, the interactive CLI starts the TypeScript backend itself, so that
// signing in, sessions and chat work without the .NET backend. The security
// features (scans, compliance, jobs, agents) still need the .NET backend.
// CERBERUS_AUTOSTART=off turns this off.

export const DEFAULT_API_URL = "http://localhost:3000";

export type BackendStatus =
  | { kind: "answering"; url: string }
  | { kind: "started"; url: string; log: string }
  | { kind: "not-started"; url: string; reason: string };

let current: BackendStatus | null = null;

/** What the CLI is talking to, once ensureBackend has run. */
export function backendStatus(): BackendStatus | null {
  return current;
}

/** Whether the address is this machine: only then may the CLI start a server for it. */
export function isLocalAddress(url: string): boolean {
  try {
    const host = new URL(url).hostname.replace(/^\[|\]$/g, "");
    return host === "localhost" || host === "127.0.0.1" || host === "::1" || host.endsWith(".localhost");
  } catch {
    return false;
  }
}

/** True when anything answers HTTP at the address — any status counts; only no answer does not. */
export async function answers(url: string, timeoutMs = 1500): Promise<boolean> {
  try {
    await fetch(url, { signal: AbortSignal.timeout(timeoutMs) });
    return true;
  } catch {
    return false;
  }
}

export type Starter = (url: string) => Promise<{ log: string; stop: () => void }>;

const frontendDir = resolve(import.meta.dirname, "../../..");

/**
 * Starts the TypeScript backend for the address, on this machine only: brings
 * its SQLite schema up to date, then runs it as a child process that ends with
 * the CLI. Its output goes to a log file, not over the screen.
 */
export const startTypeScriptServer: Starter = async (url) => {
  const port = new URL(url).port || "80";
  const home = cerberusHome();
  mkdirSync(home, { recursive: true, mode: 0o700 });
  const log = join(home, "local-server.log");
  const out = openSync(log, "a");
  const env = { ...process.env, PORT: port, CERBERUS_BIND: "127.0.0.1" };

  const schema = Bun.spawnSync(["bun", "run", "db:push"], { cwd: join(frontendDir, "database"), env, stdout: out, stderr: out });
  if (schema.exitCode !== 0) throw new Error(`the database schema could not be prepared (see ${log})`);

  const child = Bun.spawn(["bun", "run", join(frontendDir, "server", "src", "index.ts")], { env, stdout: out, stderr: out });
  const stop = () => {
    try {
      child.kill();
    } catch {
      // Already gone.
    }
  };
  process.once("exit", stop);

  const deadline = Date.now() + 20_000;
  while (Date.now() < deadline) {
    if (await answers(url, 500)) return { log, stop };
    if (child.exitCode !== null) break;
    await Bun.sleep(200);
  }
  stop();
  throw new Error(`the TypeScript server did not start (see ${log})`);
};

/**
 * Makes sure something serves the configured address: the .NET backend if it
 * answers, otherwise — for an address on this machine — the TypeScript one,
 * started now.
 */
export async function ensureBackend(
  options: { url?: string; autostart?: string; probe?: typeof answers; start?: Starter } = {},
): Promise<BackendStatus> {
  const url = options.url ?? process.env.API_URL ?? DEFAULT_API_URL;
  const probe = options.probe ?? answers;
  const start = options.start ?? startTypeScriptServer;
  const autostart = options.autostart ?? process.env.CERBERUS_AUTOSTART;

  if (await probe(url)) return (current = { kind: "answering", url });
  if (autostart === "off") return (current = { kind: "not-started", url, reason: "nothing answers and CERBERUS_AUTOSTART is off" });
  if (!isLocalAddress(url)) return (current = { kind: "not-started", url, reason: "nothing answers, and the address is not on this machine" });
  try {
    const { log } = await start(url);
    return (current = { kind: "started", url, log });
  } catch (error) {
    return (current = { kind: "not-started", url, reason: error instanceof Error ? error.message : String(error) });
  }
}
