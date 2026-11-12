import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { cerberusHome } from "./home";

// What the operator chose and expects to find again after a restart: the
// theme, the model. One small file; each setting is written without
// disturbing the others.

export type Preferences = { themeName?: string; model?: string };

const file = () => join(cerberusHome(), "preferences.json");

export function readPreferences(): Preferences {
  try {
    const parsed = JSON.parse(readFileSync(file(), "utf8")) as unknown;
    return parsed && typeof parsed === "object" && !Array.isArray(parsed) ? (parsed as Preferences) : {};
  } catch {
    return {};
  }
}

/** Keeps one setting. Not being able to write it only means choosing again next time. */
export function savePreference<K extends keyof Preferences>(key: K, value: Preferences[K]): void {
  try {
    mkdirSync(cerberusHome(), { recursive: true });
    writeFileSync(file(), JSON.stringify({ ...readPreferences(), [key]: value }, null, 2), "utf8");
  } catch {
    // Ignored on purpose.
  }
}
