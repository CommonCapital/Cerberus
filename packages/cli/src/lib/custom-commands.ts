import { readdirSync, readFileSync, statSync } from "node:fs";
import { homedir } from "node:os";
import { dirname, join } from "node:path";
import { findGitRoot } from "./project-context";

// Custom slash commands: a prompt kept in a file, sent by typing its name.
// `.cerberus/commands/fix-tests.md` becomes `/fix-tests`; what is typed after
// the name fills `$ARGUMENTS` (or `$1`, `$2`, …) in the file's text. A command
// only ever produces a message from the operator — it runs nothing by itself.

/** Directories searched in a project, nearest first; `.opencode` and `.claude` so existing command files work as they are. */
export const PROJECT_COMMAND_DIRS = [".cerberus/commands", ".opencode/commands", ".claude/commands"];

const MAX_COMMANDS = 100;
const MAX_TEMPLATE = 20_000;
const NAME = /^[a-z0-9][a-z0-9_-]{0,39}$/;

export type CustomCommand = {
  name: string;
  description: string;
  /** The prompt, with its placeholders. */
  template: string;
  /** Where it came from, for the operator: "project" or "user". */
  source: "project" | "user";
};

/** A command file: optional `---` frontmatter with `description:`, then the prompt. */
export function parseCommandFile(text: string): { description: string; template: string } {
  let body = text.replace(/\r\n/g, "\n");
  let description = "";
  const frontmatter = body.match(/^---\n([\s\S]*?)\n---\n?/);
  if (frontmatter) {
    body = body.slice(frontmatter[0].length);
    const line = frontmatter[1]!.split("\n").find((l) => /^description\s*:/.test(l));
    if (line) description = line.replace(/^description\s*:\s*/, "").trim().replace(/^["']|["']$/g, "");
  }
  const template = body.trim().slice(0, MAX_TEMPLATE);
  if (!description) description = template.split("\n")[0]!.replace(/^#+\s*/, "").slice(0, 80);
  return { description, template };
}

function readDirectory(directory: string, source: CustomCommand["source"]): CustomCommand[] {
  let names: string[];
  try {
    names = readdirSync(directory).filter((name) => name.endsWith(".md")).sort();
  } catch {
    return [];   // no such directory
  }
  const commands: CustomCommand[] = [];
  for (const file of names) {
    const name = file.slice(0, -3).toLowerCase();
    if (!NAME.test(name)) continue;
    try {
      const path = join(directory, file);
      if (!statSync(path).isFile()) continue;
      const { description, template } = parseCommandFile(readFileSync(path, "utf-8"));
      if (template.length > 0) commands.push({ name, description, template, source });
    } catch {
      // Unreadable: skipped.
    }
  }
  return commands;
}

/**
 * The custom commands available in `cwd`: the project's (from the working
 * directory up to the repository root, nearest first) and then the user's own.
 * A nearer command hides a farther one of the same name, and `reserved` names
 * — the built-in commands — cannot be taken at all.
 */
export function loadCustomCommands(
  cwd: string = process.cwd(),
  userDirectory: string = join(process.env.NIGHTCODE_HOME ?? join(homedir(), ".nightcode"), "commands"),
  reserved: ReadonlySet<string> = new Set(),
): CustomCommand[] {
  const found: CustomCommand[] = [];
  const stop = findGitRoot(cwd) ?? cwd;
  for (let dir = cwd; ; dir = dirname(dir)) {
    for (const relative of PROJECT_COMMAND_DIRS) found.push(...readDirectory(join(dir, relative), "project"));
    if (dir === stop || dirname(dir) === dir) break;
  }
  found.push(...readDirectory(userDirectory, "user"));

  const byName = new Map<string, CustomCommand>();
  for (const command of found) {
    if (!reserved.has(command.name) && !byName.has(command.name)) byName.set(command.name, command);
  }
  return [...byName.values()].slice(0, MAX_COMMANDS);
}

/** Splits what was typed after the command's name into arguments; quotes keep words together. */
export function splitArguments(text: string): string[] {
  const args: string[] = [];
  for (const match of text.matchAll(/"([^"]*)"|'([^']*)'|(\S+)/g)) args.push(match[1] ?? match[2] ?? match[3]!);
  return args;
}

/**
 * Fills a command's template: `$ARGUMENTS` is everything typed after the
 * name, `$1`…`$9` the single arguments — the highest-numbered one used takes
 * all that is left, so a sentence can be the last argument. A template with
 * no placeholder gets the arguments appended.
 */
export function expandTemplate(template: string, typed: string): string {
  const all = typed.trim();
  const args = splitArguments(all);
  const numbers = [...template.matchAll(/\$([1-9])/g)].map((m) => Number(m[1]));
  const last = numbers.length > 0 ? Math.max(...numbers) : 0;
  const usesPlaceholder = last > 0 || template.includes("$ARGUMENTS");

  // One pass, so that text put in by an argument is never itself read as a placeholder.
  const filled = template.replace(/\$ARGUMENTS|\$([1-9])/g, (_, digit: string | undefined) => {
    if (digit === undefined) return all;
    const n = Number(digit);
    return n === last ? args.slice(n - 1).join(" ") : (args[n - 1] ?? "");
  });
  return usesPlaceholder || all.length === 0 ? filled : `${filled}\n\n${all}`;
}

/**
 * What a typed line should be sent as: for `/name arguments` where `name` is
 * a custom command, its filled template; otherwise null (send the line as it is).
 */
export function expandSubmission(text: string, commands: readonly CustomCommand[]): string | null {
  const match = text.match(/^\/([A-Za-z0-9_-]+)(?:\s+([\s\S]*))?$/);
  if (!match) return null;
  const command = commands.find((c) => c.name === match[1]!.toLowerCase());
  return command ? expandTemplate(command.template, match[2] ?? "") : null;
}
