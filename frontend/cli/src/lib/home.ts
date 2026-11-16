import { homedir } from "node:os";
import { join } from "node:path";

// Where the CLI keeps its own files: the sign-in, the chosen engagement and
// theme, the undo history, the operator's own slash commands.
// CERBERUS_HOME moves it (tests, scripts, a pipeline's scratch directory).

export function cerberusHome(): string {
  return process.env.CERBERUS_HOME || join(homedir(), ".cerberus");
}
