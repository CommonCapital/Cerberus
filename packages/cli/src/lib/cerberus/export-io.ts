import { writeFileSync } from "node:fs";
import { cerberus } from "./api";
import type { SarifIo } from "./export";

/** The real thing: the SARIF log comes from the backend and is written into the directory the CLI runs in. */
export const sarifIo: SarifIo = {
  fetch: (source) => cerberus.sarif(source.kind, source.id),
  write: (path, content) => writeFileSync(path, content),
  directory: () => process.cwd(),
};
