import { mkdirSync, readdirSync, statSync, unlinkSync, writeFileSync } from "fs";
import { mkdir, readFile, readdir, stat, writeFile } from "fs/promises";
import { tmpdir } from "os";
import { dirname, isAbsolute, join, relative, resolve, sep } from "path";
import { toolInputSchemas, Mode, type ModeType } from "@nightcode/shared";

/** Reading: at most this many lines and characters per call; the rest is reached with `offset`. */
const MAX_READ_LINES = 2000;
const MAX_READ_CHARS = 60_000;
const MAX_LINE_LENGTH = 2000;
const MAX_RESULTS = 200;
const MAX_MATCHES = 50;
const MAX_OUTPUT = 20_000;
const DEFAULT_TIMEOUT = 120_000;
const MAX_TIMEOUT = 600_000;

/** Where the full output of a command goes when it is too long to hand to the model. */
export const OUTPUT_DIR = join(tmpdir(), "nightcode-tool-output");
const OUTPUT_KEEP_MS = 7 * 24 * 60 * 60 * 1000;

const isSavedOutput = (path: string) => {
  const full = resolve(path);
  return full.startsWith(OUTPUT_DIR + sep) && !relative(OUTPUT_DIR, full).includes(sep);
};

/**
 * Output short enough is returned as is. Longer output is saved whole to a
 * file and the model gets its beginning and its end — where a command says
 * what it is doing and how it ended — with a pointer to the file.
 */
export function fitOutput(text: string, save: (full: string) => string, limit = MAX_OUTPUT): string {
  if (text.length <= limit) return text;
  const head = Math.floor(limit / 4);
  const tail = limit - head;
  const file = save(text);
  return (
    `${text.slice(0, head)}\n\n[… ${text.length - limit} characters left out; showing the first ${head} and the last ${tail} of ${text.length}. ` +
    `Full output saved to ${file} — read it with readFile if you need the middle.]\n\n${text.slice(-tail)}`
  );
}

function saveOutput(full: string): string {
  mkdirSync(OUTPUT_DIR, { recursive: true, mode: 0o700 });
  // Old outputs are cleared as new ones arrive.
  for (const name of readdirSync(OUTPUT_DIR)) {
    const path = join(OUTPUT_DIR, name);
    try {
      if (Date.now() - statSync(path).mtimeMs > OUTPUT_KEEP_MS) unlinkSync(path);
    } catch {
      // Gone already, or not ours to remove.
    }
  }
  const file = join(OUTPUT_DIR, `output-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}.txt`);
  writeFileSync(file, full, { mode: 0o600 });
  return file;
}

function resolveInsideCwd(path: string) {
  const cwd = process.cwd();
  const resolved = resolve(cwd, path);
  const rel = relative(cwd, resolved);

  if (rel.startsWith("..") || isAbsolute(rel)) {
    throw new Error("Path is outside the project directory");
  }

  return { cwd, resolved };
}

/**
 * A window of a text file with numbered lines, as the model reads it. A file
 * too long for one call says where to continue; a binary file is refused.
 */
export function readNumbered(content: string, offset = 1, limit = MAX_READ_LINES) {
  if (content.includes("\0")) throw new Error("That is a binary file; it cannot be read as text");

  const lines = content.length === 0 ? [] : content.split("\n");
  if (lines.length > 1 && lines[lines.length - 1] === "") lines.pop();   // the final newline is not a line
  const totalLines = lines.length;
  if (totalLines > 0 && offset > totalLines) throw new Error(`The file has only ${totalLines} line(s)`);

  const wanted = Math.min(limit, MAX_READ_LINES);
  const out: string[] = [];
  let chars = 0;
  let line = offset;
  for (; line <= totalLines && out.length < wanted; line++) {
    const text = lines[line - 1]!.replace(/\r$/, "");
    const shown = text.length > MAX_LINE_LENGTH ? `${text.slice(0, MAX_LINE_LENGTH)}… (line cut, ${text.length} chars)` : text;
    if (chars + shown.length > MAX_READ_CHARS && out.length > 0) break;
    out.push(`${line}\t${shown}`);
    chars += shown.length;
  }

  const endLine = line - 1;
  const more = endLine < totalLines;
  return {
    content: out.join("\n"),
    startLine: offset,
    endLine,
    totalLines,
    truncated: more,
    next: more ? `Read on with offset ${endLine + 1}.` : null,
  };
}

/** One line of `grep -n` output: file, line number, text. A carriage return left by a Windows file is dropped. */
export function parseGrepLine(line: string): { file: string; line: number; content: string } | null {
  const match = line.replace(/\r$/, "").match(/^(.+?):(\d+):(.*)$/);
  return match ? { file: match[1]!, line: Number(match[2]), content: match[3]! } : null;
}

export type EditMatch = "exact" | "line-endings" | "typography" | "trailing-whitespace";

/** Characters a model tends to "tidy" when it retypes code: curly quotes, long dashes, non-breaking spaces. One for one, so positions stay put. */
const TYPOGRAPHY: Record<string, string> = {
  "\u2018": "'", "\u2019": "'", "\u201C": '"', "\u201D": '"', "\u2013": "-", "\u2014": "-", "\u00A0": " ",
};
const plain = (text: string) => text.replace(/[\u2018\u2019\u201C\u201D\u2013\u2014\u00A0]/g, (c) => TYPOGRAPHY[c] ?? c);

const occurrencesOf = (text: string, needle: string) => text.split(needle).length - 1;

/**
 * Replaces text in a file's content. The search text must match exactly; if it
 * does not, three near-misses that models commonly produce are tried in turn —
 * different line endings, "tidied" typography, and trailing whitespace — and the
 * result says which one matched. The file keeps its own line endings. One
 * unique occurrence is required unless `replaceAll` is set; never a silent no-op.
 */
export function replaceExact(content: string, oldString: string, newString: string, replaceAll: boolean) {
  if (oldString.length === 0) throw new Error("oldString is empty");
  if (oldString === newString) throw new Error("oldString and newString are the same; nothing would change");

  const crlf = content.includes("\r\n");
  const toFile = (text: string) => (crlf ? text.replace(/\r?\n/g, "\r\n") : text);
  const ambiguous = (count: number) =>
    new Error(`oldString is ambiguous; found ${count} matches. Add surrounding lines to make it unique, or set replaceAll`);
  const done = (result: string, replacements: number, matched: EditMatch) => ({ result, replacements, matched });

  // 1. Exactly as given.
  let count = occurrencesOf(content, oldString);
  if (count > 1 && !replaceAll) throw ambiguous(count);
  // split/join rather than replace(): "$&" and friends in newString must stay literal.
  if (count > 0) return done(content.split(oldString).join(toFile(newString)), count, "exact");

  // 2. The same text with the file's line endings.
  const oldInFile = toFile(oldString);
  count = oldInFile === oldString ? 0 : occurrencesOf(content, oldInFile);
  if (count > 1 && !replaceAll) throw ambiguous(count);
  if (count > 0) return done(content.split(oldInFile).join(toFile(newString)), count, "line-endings");

  // 3. Ignoring typography. The mapping is one character for one, so a match in the plain text is the same range in the file.
  const plainContent = plain(content);
  const plainOld = plain(oldInFile);
  count = occurrencesOf(plainContent, plainOld);
  if (count > 1 && !replaceAll) throw ambiguous(count);
  if (count > 0) {
    let result = "";
    let from = 0;
    for (let at = plainContent.indexOf(plainOld); at !== -1; at = plainContent.indexOf(plainOld, from)) {
      result += content.slice(from, at) + toFile(newString);
      from = at + plainOld.length;
    }
    return done(result + content.slice(from), count, "typography");
  }

  // 4. Whole lines, ignoring whitespace at their ends — and typography too, since near-misses come together.
  const eol = crlf ? "\r\n" : "\n";
  const lines = content.split(eol);
  const loose = (line: string) => plain(line).trimEnd();
  const wanted = oldString.replace(/\r\n/g, "\n").split("\n").map(loose);
  const starts: number[] = [];
  for (let i = 0; i + wanted.length <= lines.length; i++) {
    if (wanted.every((line, j) => loose(lines[i + j]!) === line)) starts.push(i);
  }
  if (starts.length > 1 && !replaceAll) throw ambiguous(starts.length);
  if (starts.length > 0) {
    const replacement = newString.replace(/\r\n/g, "\n").split("\n");
    // From the last match backwards, so earlier positions stay valid.
    for (const start of [...starts].reverse()) lines.splice(start, wanted.length, ...replacement);
    return done(lines.join(eol), starts.length, "trailing-whitespace");
  }

  throw new Error("oldString not found in file. It must match the file's text, including indentation, and without the line-number prefix readFile shows");
}

const MAX_DIFF_LINES = 60;

/**
 * What an edit changed, as the lines removed and added around the first
 * difference — enough for the operator to see the change at a glance.
 */
export function changeSummary(before: string, after: string): { diff: string; added: number; removed: number } {
  const a = before.split(/\r?\n/);
  const b = after.split(/\r?\n/);
  let head = 0;
  while (head < a.length && head < b.length && a[head] === b[head]) head++;
  let tail = 0;
  while (tail < a.length - head && tail < b.length - head && a[a.length - 1 - tail] === b[b.length - 1 - tail]) tail++;

  const removed = a.slice(head, a.length - tail);
  const added = b.slice(head, b.length - tail);
  const lines = [`@@ line ${head + 1} @@`, ...removed.map((line) => `-${line}`), ...added.map((line) => `+${line}`)];
  const shown = lines.length > MAX_DIFF_LINES ? [...lines.slice(0, MAX_DIFF_LINES), `… ${lines.length - MAX_DIFF_LINES} more line(s)`] : lines;
  return { diff: shown.join("\n"), added: added.length, removed: removed.length };
}

export type CommandResult = { stdout: string; stderr: string; exitCode: number; timedOut?: true; note?: string };

/**
 * Runs a shell command in the project directory and returns what it printed
 * (long output is cut in the middle and saved whole to a file) and how it
 * ended. Used for the agent's `bash` tool and for a command the operator
 * types themself with "!".
 */
export async function runCommand(command: string, options: { timeout?: number; byAgent: boolean }): Promise<CommandResult> {
  const timeout = Math.min(Math.max(options.timeout ?? DEFAULT_TIMEOUT, 1000), MAX_TIMEOUT);
  const proc = Bun.spawn(["bash", "-c", command], {
    cwd: resolveInsideCwd(".").resolved,
    stdout: "pipe",
    stderr: "pipe",
    // AGENT=1 lets scripts tell they are being run by an agent and not by a person.
    env: { ...process.env, TERM: "dumb", ...(options.byAgent ? { AGENT: "1" } : {}) },
  });
  let timedOut = false;
  const timer = setTimeout(() => {
    timedOut = true;
    proc.kill();
  }, timeout);
  const [stdout, stderr] = await Promise.all([new Response(proc.stdout).text(), new Response(proc.stderr).text()]);
  const exitCode = await proc.exited;
  clearTimeout(timer);
  return {
    stdout: fitOutput(stdout, saveOutput),
    stderr: fitOutput(stderr, saveOutput),
    exitCode,
    ...(timedOut
      ? { timedOut: true as const, note: `Stopped after ${Math.round(timeout / 1000)} s. Pass a longer timeout (up to ${MAX_TIMEOUT / 1000} s) if the command needs it.` }
      : {}),
  };
}

/**
 * @param onChange Told about every file the call wrote, with its content before
 * (null if it did not exist) and after — what an undo needs.
 */
export async function executeLocalTool(
  toolName: string,
  input: unknown,
  mode: ModeType,
  onChange?: (change: { path: string; before: string | null; after: string }) => void,
) {
  if (mode === Mode.PLAN && !["readFile", "listDirectory", "glob", "grep"].includes(toolName)) {
    throw new Error(`Tool ${toolName} is not available in PLAN mode`);
  }

  switch (toolName) {
    case "readFile": {
      const { path, offset, limit } = toolInputSchemas.readFile.parse(input);
      // Besides the project, the full output of an earlier command that was too long to show.
      const resolved = isSavedOutput(path) ? resolve(path) : resolveInsideCwd(path).resolved;
      return readNumbered(await readFile(resolved, "utf-8"), offset, limit);
    }
    case "listDirectory": {
      const { path } = toolInputSchemas.listDirectory.parse(input);
      const { cwd, resolved } = resolveInsideCwd(path);
      const entries = await readdir(resolved);
      const results: { name: string; type: "file" | "directory" }[] = [];

      for (const entry of entries) {
        if (entry.startsWith(".") || entry === "node_modules") continue;
        const info = await stat(join(resolved, entry));
        results.push({ name: entry, type: info.isDirectory() ? "directory" : "file" });
      }

      results.sort((a, b) =>
        a.type !== b.type ? (a.type === "directory" ? -1 : 1) : a.name.localeCompare(b.name),
      );
      return { path: relative(cwd, resolved) || ".", entries: results };
    }
    case "glob": {
      const { pattern, path } = toolInputSchemas.glob.parse(input);
      const { cwd, resolved } = resolveInsideCwd(path);
      const glob = new Bun.Glob(pattern);
      const files: string[] = [];
      let truncated = false;

      for await (const match of glob.scan({ cwd: resolved, dot: false, onlyFiles: true })) {
        if (match.includes("node_modules")) continue;
        if (files.length >= MAX_RESULTS) {
          truncated = true;
          break;
        }
        files.push(relative(cwd, resolve(resolved, match)));
      }

      files.sort();
      return { files, ...(truncated ? { truncated: true } : {}) };
    }
    case "grep": {
      const { pattern, path, include } = toolInputSchemas.grep.parse(input);
      const { cwd, resolved } = resolveInsideCwd(path);
      const args = [
        "-rn",
        "--color=never",
        "--exclude-dir=node_modules",
        "--exclude-dir=.git",
        // Files that hold secrets are not swept up by a search; reading one is asked about by name.
        "--exclude=.env",
        "--exclude=.env.*",
        "-E",
      ];
      if (include) args.push(`--include=${include}`);
      args.push(pattern, resolved);

      const proc = Bun.spawn(["grep", ...args], { cwd, stdout: "pipe", stderr: "pipe" });
      const [stdout, stderr] = await Promise.all([
        new Response(proc.stdout).text(),
        new Response(proc.stderr).text(),
      ]);
      const exitCode = await proc.exited;

      if (exitCode !== 0 && exitCode !== 1) throw new Error(`grep failed: ${stderr.trim()}`);
      if (!stdout.trim()) return { matches: [], message: "No matches found" };

      const lines = stdout.trim().split("\n");
      const matches: { file: string; line: number; content: string }[] = [];
      let truncated = false;

      for (const line of lines) {
        if (matches.length >= MAX_MATCHES) {
          truncated = true;
          break;
        }
        const match = parseGrepLine(line);
        if (match) matches.push({ ...match, file: relative(cwd, match.file) });
      }

      return { matches, ...(truncated ? { truncated: true, totalMatches: lines.length } : {}) };
    }
    case "writeFile": {
      const { path, content } = toolInputSchemas.writeFile.parse(input);
      const { cwd, resolved } = resolveInsideCwd(path);
      const previous = await readFile(resolved, "utf-8").catch(() => null);
      await mkdir(dirname(resolved), { recursive: true });
      await writeFile(resolved, content, "utf-8");
      onChange?.({ path: relative(cwd, resolved), before: previous, after: content });
      return {
        success: true as const,
        path: relative(cwd, resolved),
        bytesWritten: Buffer.byteLength(content, "utf-8"),
        created: previous === null,
        // For an overwrite: what changed, so the operator sees it was not a blind replacement.
        ...(previous === null ? { added: content.split("\n").length, removed: 0 } : changeSummary(previous, content)),
      };
    }
    case "editFile": {
      const { path, oldString, newString, replaceAll } = toolInputSchemas.editFile.parse(input);
      const { cwd, resolved } = resolveInsideCwd(path);
      const content = await readFile(resolved, "utf-8");
      const { result, replacements, matched } = replaceExact(content, oldString, newString, replaceAll ?? false);

      await writeFile(resolved, result, "utf-8");
      onChange?.({ path: relative(cwd, resolved), before: content, after: result });
      return {
        success: true as const,
        path: relative(cwd, resolved),
        replacements,
        ...changeSummary(content, result),
        // Said when the match was not exact, so the model knows what the file really holds.
        ...(matched === "exact" ? {} : { note: `oldString did not match exactly; it matched ignoring ${matched.replace("-", " ")}.` }),
      };
    }
    case "bash": {
      const parsed = toolInputSchemas.bash.parse(input);
      return runCommand(parsed.command, { timeout: parsed.timeout, byAgent: true });
    }
    default:
      throw new Error(`Unknown tool: ${toolName}`);
  }
};
