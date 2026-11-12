import { existsSync, mkdirSync, readFileSync, writeFileSync, unlinkSync } from "node:fs";
import { homedir } from "node:os";
import { join } from "node:path";

type AuthData = {
  token: string;
};

// Where the CLI keeps its own files; NIGHTCODE_HOME moves it (tests, scripts), as for the other files kept there.
const authDir = () => process.env.NIGHTCODE_HOME ?? join(homedir(), ".nightcode");
const authFile = () => join(authDir(), "auth.json");


/**
 * A sign-in handed over by the environment, for pipelines: a secret the CI
 * system injects. It wins over the saved one and is never written to disk.
 */
const tokenFromEnvironment = () => process.env.CERBERUS_TOKEN?.trim() || null;

export function getAuth(): AuthData | null {
  const handedOver = tokenFromEnvironment();
  if (handedOver) return { token: handedOver };
  try {
    const data = readFileSync(authFile(), "utf-8");
    const parsed = JSON.parse(data) as Partial<AuthData>;
    return typeof parsed.token === "string" ? { token: parsed.token } : null;
  } catch {
    return null;
  }
};

export function saveAuth(data: AuthData) {
  if (!existsSync(authDir())) {
    // Owner-only permissions (rwx------) so other users on the machine can't read tokens
    mkdirSync(authDir(), { mode: 0o700, recursive: true });
  }
  writeFileSync(authFile(), JSON.stringify(data), { mode: 0o600 });
}

export function clearAuth() {
  // A token from the environment being refused says nothing about the saved sign-in: leave that alone.
  if (tokenFromEnvironment()) return;
  try {
    unlinkSync(authFile());
  } catch {
    // File doesn't exist
  }
}

