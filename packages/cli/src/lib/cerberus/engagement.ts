import { existsSync, mkdirSync, readFileSync, unlinkSync, writeFileSync } from "node:fs";
import { homedir } from "node:os";
import { join } from "node:path";
import { useSyncExternalStore } from "react";

// The engagement the operator is working under. Jobs started from the CLI run
// under it, so the backend checks their target against its scope. Kept in a
// small file next to the sign-in, so it survives a restart of the CLI.

export type CurrentEngagement = { id: string; name: string };

const directory = () => process.env.NIGHTCODE_HOME ?? join(homedir(), ".nightcode");
const file = () => join(directory(), "engagement.json");

let loaded = false;
let current: CurrentEngagement | null = null;
const listeners = new Set<() => void>();

function load(): CurrentEngagement | null {
  try {
    const parsed = JSON.parse(readFileSync(file(), "utf-8")) as Partial<CurrentEngagement>;
    return typeof parsed.id === "string" && typeof parsed.name === "string" ? { id: parsed.id, name: parsed.name } : null;
  } catch {
    return null;
  }
}

export function getCurrentEngagement(): CurrentEngagement | null {
  if (!loaded) {
    current = load();
    loaded = true;
  }
  return current;
}

export function setCurrentEngagement(next: CurrentEngagement | null) {
  current = next;
  loaded = true;
  try {
    if (next) {
      if (!existsSync(directory())) mkdirSync(directory(), { mode: 0o700, recursive: true });
      writeFileSync(file(), JSON.stringify(next), { mode: 0o600 });
    } else {
      unlinkSync(file());
    }
  } catch {
    // Not being able to remember the choice is not worth failing over.
  }
  for (const listener of listeners) listener();
}

/** For tests: forget what was read from disk. */
export function resetCurrentEngagementCache() {
  loaded = false;
  current = null;
}

function subscribe(listener: () => void) {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

export const useCurrentEngagement = () => useSyncExternalStore(subscribe, getCurrentEngagement, getCurrentEngagement);

/**
 * The job as it should be submitted: under the chosen engagement, unless it
 * names one itself. Triage is left alone — it belongs to its scan's engagement,
 * which the server takes from the scan.
 */
export function underEngagement<T extends { type: string; engagementId?: string }>(
  job: T,
  chosen: CurrentEngagement | null,
): T {
  if (job.type === "triage" || job.engagementId || !chosen) return job;
  return { ...job, engagementId: chosen.id };
}

/** Turns the server's refusal of work without an engagement into something the operator can act on. */
export function explainSubmitError(message: string): string {
  return message.startsWith("An engagementId is required")
    ? "This server only runs work under an engagement. Choose or create one with /engagement."
    : message;
}
