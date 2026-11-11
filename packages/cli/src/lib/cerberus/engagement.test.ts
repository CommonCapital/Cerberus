import { afterAll, beforeEach, describe, expect, test } from "bun:test";
import { existsSync, mkdtempSync, readFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

// Never the operator's real settings directory.
const home = mkdtempSync(join(tmpdir(), "nightcode-test-"));
process.env.NIGHTCODE_HOME = home;

const {
  explainSubmitError, getCurrentEngagement, resetCurrentEngagementCache, setCurrentEngagement, underEngagement,
} = await import("./engagement");

beforeEach(() => {
  process.env.NIGHTCODE_HOME = home;
  setCurrentEngagement(null);
});
afterAll(() => rmSync(home, { recursive: true, force: true }));

describe("the current engagement", () => {
  test("is none until one is chosen, and the choice survives a restart", () => {
    expect(getCurrentEngagement()).toBeNull();

    setCurrentEngagement({ id: "e-1", name: "Payments audit" });
    expect(JSON.parse(readFileSync(join(home, "engagement.json"), "utf-8"))).toEqual({ id: "e-1", name: "Payments audit" });

    resetCurrentEngagementCache();   // as a new process would start
    expect(getCurrentEngagement()).toEqual({ id: "e-1", name: "Payments audit" });
  });

  test("choosing none forgets it on disk too", () => {
    setCurrentEngagement({ id: "e-1", name: "Payments audit" });
    setCurrentEngagement(null);

    expect(existsSync(join(home, "engagement.json"))).toBe(false);
    resetCurrentEngagementCache();
    expect(getCurrentEngagement()).toBeNull();
  });
});

describe("submitting a job", () => {
  const chosen = { id: "e-1", name: "Payments audit" };
  type AnyJob = { type: string; engagementId?: string; [key: string]: unknown };

  test("runs under the chosen engagement", () => {
    expect(underEngagement<AnyJob>({ type: "scan", path: "app" }, chosen)).toEqual({ type: "scan", path: "app", engagementId: "e-1" });
    expect(underEngagement<AnyJob>({ type: "sast", path: "app", model: "llama3.1" }, chosen).engagementId).toBe("e-1");
  });

  test("keeps an engagement the job names itself, and leaves triage to its scan", () => {
    expect(underEngagement<AnyJob>({ type: "scan", path: "app", engagementId: "e-2" }, chosen).engagementId).toBe("e-2");
    expect(underEngagement<AnyJob>({ type: "triage", scanId: "s-1", model: "llama3.1" }, chosen)).toEqual({ type: "triage", scanId: "s-1", model: "llama3.1" });
  });

  test("is unchanged when no engagement is chosen", () => {
    expect(underEngagement<AnyJob>({ type: "scan", path: "app" }, null)).toEqual({ type: "scan", path: "app" });
  });

  test("a refusal for lack of an engagement says what to do", () => {
    expect(explainSubmitError("An engagementId is required: this server only runs work under an engagement.")).toContain("/engagement");
    expect(explainSubmitError("Target is out of scope")).toBe("Target is out of scope");
  });
});
