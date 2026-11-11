import { join } from "node:path";
import type { Job } from "./api";

// Saving a run's findings as a SARIF file, for code-scanning dashboards and
// other tools. The server builds the file; the CLI writes it where it runs.
// What touches the network and the disk is passed in (see export-io.ts).

export type SarifSource = { kind: "scans" | "reviews" | "agent-runs"; id: string };

/** Where a finished run's findings can be exported from, or null if it has none to export. */
export function sarifSourceOf(job: Pick<Job, "type" | "state" | "resultId"> | null): SarifSource | null {
  if (!job || job.state !== "Succeeded" || !job.resultId) return null;
  const kind = job.type === "scan" ? "scans" : job.type === "review" ? "reviews" : job.type === "sast" ? "agent-runs" : null;
  return kind ? { kind, id: job.resultId } : null;
}

export const sarifFileName = (job: Pick<Job, "type">, source: SarifSource) =>
  `cerberus-${job.type}-${source.id.slice(0, 8)}.sarif`;

export type SarifIo = {
  fetch: (source: SarifSource) => Promise<unknown>;
  write: (path: string, content: string) => void;
  directory: () => string;
};

/** Fetches the run's SARIF log and writes it into the current directory. Returns the path written. */
export async function saveSarif(job: Job, deps: SarifIo): Promise<string> {
  const source = sarifSourceOf(job);
  if (!source) throw new Error("This run has no findings to export.");
  const log = await deps.fetch(source);
  const path = join(deps.directory(), sarifFileName(job, source));
  deps.write(path, `${JSON.stringify(log, null, 2)}\n`);
  return path;
}
