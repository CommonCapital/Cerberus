import { expect, test } from "bun:test";
import type { Job } from "./api";

// Imported for its pure parts only; nothing here reaches the network or the disk.
const { saveSarif, sarifFileName, sarifSourceOf } = await import("./export");

const job = (type: string, state: Job["state"], resultId: string | null): Job => ({
  id: "job-1", type, state, tier: "T0", engagementId: null, targetPath: "svc", resultId, approvalId: null,
  error: null, createdAt: "2026-11-12T10:00:00Z", startedAt: null, finishedAt: null,
});

test("scans, reviews and code reviews by a model have findings to export, once they have succeeded", () => {
  expect(sarifSourceOf(job("scan", "Succeeded", "s-1"))).toEqual({ kind: "scans", id: "s-1" });
  expect(sarifSourceOf(job("review", "Succeeded", "r-1"))).toEqual({ kind: "reviews", id: "r-1" });
  expect(sarifSourceOf(job("sast", "Succeeded", "a-1"))).toEqual({ kind: "agent-runs", id: "a-1" });

  expect(sarifSourceOf(job("scan", "Running", null))).toBeNull();
  expect(sarifSourceOf(job("scan", "Failed", null))).toBeNull();
  expect(sarifSourceOf(job("threat-model", "Succeeded", "a-2"))).toBeNull();
  expect(sarifSourceOf(job("assess", "Succeeded", "c-1"))).toBeNull();
  expect(sarifSourceOf(null)).toBeNull();
});

test("the file is named after the kind of run and written, formatted, into the current directory", async () => {
  const written: { path: string; content: string }[] = [];
  const fetched: unknown[] = [];
  const finished = job("review", "Succeeded", "0123456789abcdef");

  const path = await saveSarif(finished, {
    fetch: async (source) => { fetched.push(source); return { version: "2.1.0", runs: [] }; },
    write: (p, content) => { written.push({ path: p, content }); },
    directory: () => "/work/project",
  });

  expect(sarifFileName(finished, { kind: "reviews", id: "0123456789abcdef" })).toBe("cerberus-review-01234567.sarif");
  expect(path).toBe("/work/project/cerberus-review-01234567.sarif");
  expect(fetched).toEqual([{ kind: "reviews", id: "0123456789abcdef" }]);
  expect(written).toEqual([{ path, content: '{\n  "version": "2.1.0",\n  "runs": []\n}\n' }]);
});

test("a run with nothing to export is refused before anything is fetched or written", async () => {
  let touched = false;
  const deps = { fetch: async () => { touched = true; return {}; }, write: () => { touched = true; }, directory: () => "/x" };

  await expect(saveSarif(job("threat-model", "Succeeded", "a-2"), deps)).rejects.toThrow("no findings to export");
  expect(touched).toBe(false);
});
