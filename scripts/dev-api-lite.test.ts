import { afterEach, expect, test } from "bun:test";
import { copyFileSync, mkdirSync, mkdtempSync, readFileSync, realpathSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

// Exercise the process boundary without an SDK, a build or a running server.
const scratch: string[] = [];
afterEach(() => {
  for (const dir of scratch.splice(0)) rmSync(dir, { recursive: true, force: true });
});

function fixture() {
  const root = realpathSync(mkdtempSync(join(tmpdir(), "cerberus lite ")));
  scratch.push(root);
  const bin = join(root, "stub bin");
  const scripts = join(root, "scripts");
  mkdirSync(bin);
  mkdirSync(scripts);
  const launcher = join(scripts, "dev-api-lite.sh");
  copyFileSync(new URL("./dev-api-lite.sh", import.meta.url), launcher);
  const log = join(root, "calls.log");
  writeFileSync(join(bin, "dotnet"), `#!/bin/sh
printf 'command' >> "$CERBERUS_TEST_LOG"
for arg do printf '|%s' "$arg" >> "$CERBERUS_TEST_LOG"; done
printf '\\n' >> "$CERBERUS_TEST_LOG"
case "$1" in
  --list-sdks) printf '%s [stub]\\n' "\${CERBERUS_TEST_SDK:-10.0.100}" ;;
  build) exit "\${CERBERUS_TEST_BUILD_EXIT:-0}" ;;
  run)
    printf 'runtime|%s|%s|%s|%s|%s|%s|%s|%s|%s|%s|%s|%s\\n' \\
      "$PWD" "$CERBERUS_JOB_CONCURRENCY" "$CERBERUS_JOB_PER_USER" \\
      "$CERBERUS_CHAT_CONCURRENCY" "$CERBERUS_CHAT_PER_USER" "$CERBERUS_AGENT_PARALLELISM" \\
      "$DOTNET_PROCESSOR_COUNT" "$DOTNET_CLI_USE_MSBUILD_SERVER" "$MSBUILDDISABLENODEREUSE" \\
      "$PORT" "$CERBERUS_DB" "$CERBERUS_KEYRING" >> "$CERBERUS_TEST_LOG"
    exit "\${CERBERUS_TEST_RUN_EXIT:-0}" ;;
  *) exit 99 ;;
esac
`, { mode: 0o755 });
  const run = (overrides: Record<string, string> = {}, args: string[] = []) => Bun.spawnSync({
    cmd: ["/bin/sh", launcher, ...args],
    cwd: tmpdir(),
    env: {
      ...process.env,
      PATH: `${bin}:/usr/bin:/bin`,
      CERBERUS_TEST_LOG: log,
      CERBERUS_TEST_SDK: "10.0.100",
      CERBERUS_TEST_BUILD_EXIT: "0",
      CERBERUS_TEST_RUN_EXIT: "0",
      PORT: "3999",
      CERBERUS_DB: join(root, "user data.db"),
      CERBERUS_KEYRING: join(root, "user keys"),
      CERBERUS_JOB_CONCURRENCY: "8",
      CERBERUS_CHAT_CONCURRENCY: "8",
      CERBERUS_JOB_PER_USER: "8",
      CERBERUS_CHAT_PER_USER: "8",
      CERBERUS_AGENT_PARALLELISM: "4",
      ...overrides,
    },
    stdout: "pipe",
    stderr: "pipe",
  });
  return { root, bin, log, run, calls: () => readFileSync(log, "utf8").trim().split("\n") };
}

test("lite launch builds once, then runs in the foreground with small pools and the caller's data paths", () => {
  const f = fixture();
  const result = f.run();
  expect(result.exitCode).toBe(0);
  expect(f.calls()).toEqual([
    "command|--list-sdks",
    "command|build|backend/src/Cerberus.Api/Cerberus.Api.csproj|--disable-build-servers|--nologo|-m:1|-p:UseSharedCompilation=false|-p:BuildInParallel=false|-p:RestoreDisableParallel=true",
    "command|run|--project|backend/src/Cerberus.Api/Cerberus.Api.csproj|--no-build|--no-restore|--no-launch-profile",
    `runtime|${f.root}|1|1|1|1|1|2|0|1|3999|${join(f.root, "user data.db")}|${join(f.root, "user keys")}`,
  ]);
});

test("a failed build never starts the API and its exit status is preserved", () => {
  const f = fixture();
  expect(f.run({ CERBERUS_TEST_BUILD_EXIT: "23" }).exitCode).toBe(23);
  expect(f.calls()).toHaveLength(2);
  expect(f.calls()[1]).toStartWith("command|build|");
});

test("the foreground API's exit status is preserved", () => {
  const f = fixture();
  expect(f.run({ CERBERUS_TEST_RUN_EXIT: "42" }).exitCode).toBe(42);
});

test("an older SDK fails before a build with an actionable message", () => {
  const f = fixture();
  const result = f.run({ CERBERUS_TEST_SDK: "9.0.100" });
  expect(result.exitCode).toBe(1);
  expect(result.stderr.toString()).toContain("No .NET 10 SDK is installed");
  expect(f.calls()).toEqual(["command|--list-sdks"]);
});

test("a missing dotnet executable fails before anything is started", () => {
  const f = fixture();
  const emptyPath = join(f.root, "empty path");
  mkdirSync(emptyPath);
  const result = f.run({ PATH: emptyPath });
  expect(result.exitCode).toBe(127);
  expect(result.stderr.toString()).toContain("Install it");
});

test("unexpected arguments fail rather than being silently ignored", () => {
  const f = fixture();
  const result = f.run({}, ["--watch"]);
  expect(result.exitCode).toBe(64);
  expect(result.stderr.toString()).toContain("Usage:");
});
