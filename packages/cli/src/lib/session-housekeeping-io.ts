import { writeFileSync } from "node:fs";
import { cerberus } from "./cerberus/api";
import type { TranscriptIo } from "./session-housekeeping";

/** The real thing: the transcript comes from the backend and is written into the directory the CLI runs in. */
export const transcriptIo: TranscriptIo = {
  fetch: (sessionId) => cerberus.exportSession(sessionId),
  write: (path, content) => writeFileSync(path, content),
  directory: () => process.cwd(),
};
