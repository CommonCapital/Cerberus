// How a tool call is shown in the conversation: one line saying what was
// asked for, and — once it has run — the few lines of its result that tell
// the operator what happened (a diff for an edit, the exit code of a command).

const text = (input: unknown, name: string): string => {
  const value = input && typeof input === "object" ? (input as Record<string, unknown>)[name] : undefined;
  return typeof value === "string" ? value : "";
};

const number = (source: unknown, name: string): number | null => {
  const value = source && typeof source === "object" ? (source as Record<string, unknown>)[name] : undefined;
  return typeof value === "number" ? value : null;
};

const clip = (value: string, max: number) => {
  const oneLine = value.replace(/\s+/g, " ").trim();
  return oneLine.length <= max ? oneLine : `${oneLine.slice(0, max)}…`;
};

/** The call in a few words: the argument that says what it is about, never a file's whole content. */
export function summarizeToolCall(toolName: string, input: unknown): string {
  switch (toolName) {
    case "readFile": {
      const offset = number(input, "offset");
      return `${text(input, "path")}${offset && offset > 1 ? ` from line ${offset}` : ""}`;
    }
    case "listDirectory":
      return text(input, "path") || ".";
    case "glob":
      return `${text(input, "pattern")}${text(input, "path") && text(input, "path") !== "." ? ` in ${text(input, "path")}` : ""}`;
    case "grep":
      return `${clip(text(input, "pattern"), 60)}${text(input, "path") && text(input, "path") !== "." ? ` in ${text(input, "path")}` : ""}`;
    case "writeFile":
    case "editFile":
      return text(input, "path");
    case "bash":
      return clip(text(input, "command"), 100);
    case "searchSkills":
    case "searchCode":
      return clip(text(input, "query"), 60);
    case "loadSkill":
      return text(input, "name");
    case "task":
      return clip(text(input, "description"), 80);
    case "dispatch": {
      // A lead's plan: who is sent. What each is asked to look at is in the plan shown for approval.
      const assignments = input && typeof input === "object" ? (input as { assignments?: unknown }).assignments : undefined;
      return Array.isArray(assignments) ? clip(assignments.map((assignment) => text(assignment, "specialist")).filter(Boolean).join(", "), 80) : "";
    }
    case "updateTasks": {
      const tasks = input && typeof input === "object" ? (input as { tasks?: unknown }).tasks : undefined;
      return Array.isArray(tasks) ? `${tasks.length} task${tasks.length === 1 ? "" : "s"}` : "";
    }
    default:
      return input && typeof input === "object" ? clip(Object.values(input).map(String).join(" "), 80) : "";
  }
}

export type ResultLine = { text: string; tone: "added" | "removed" | "plain" | "problem" };

const MAX_DIFF_SHOWN = 14;
const MAX_REPORT_SHOWN = 4;

/** What to show under a finished call. Empty when the one-line summary already says it all. */
export function toolResultLines(toolName: string, output: unknown): ResultLine[] {
  if (!output || typeof output !== "object") return [];
  const result = output as Record<string, unknown>;

  if (toolName === "editFile" || toolName === "writeFile") {
    const lines: ResultLine[] = [];
    const added = number(result, "added") ?? 0;
    const removed = number(result, "removed") ?? 0;
    const what = toolName === "writeFile" ? (result.created === true ? "created" : "overwritten") : `${number(result, "replacements") ?? 1} replacement(s)`;
    lines.push({ text: `${what} · +${added} −${removed}`, tone: "plain" });
    if (typeof result.note === "string") lines.push({ text: result.note, tone: "problem" });
    if (typeof result.diff === "string") {
      const diff = result.diff.split("\n");
      for (const line of diff.slice(0, MAX_DIFF_SHOWN)) {
        lines.push({ text: line, tone: line.startsWith("+") ? "added" : line.startsWith("-") ? "removed" : "plain" });
      }
      if (diff.length > MAX_DIFF_SHOWN) lines.push({ text: `… ${diff.length - MAX_DIFF_SHOWN} more line(s) of the change`, tone: "plain" });
    }
    return lines;
  }

  if (toolName === "bash") {
    const exit = number(result, "exitCode");
    const lines: ResultLine[] = [];
    if (result.timedOut === true) lines.push({ text: typeof result.note === "string" ? result.note : "Timed out.", tone: "problem" });
    else if (exit !== null && exit !== 0) lines.push({ text: `exit ${exit}`, tone: "problem" });
    // The last lines of what it printed: where a command says how it went.
    const printed = `${typeof result.stdout === "string" ? result.stdout : ""}${typeof result.stderr === "string" ? `\n${result.stderr}` : ""}`
      .split("\n").map((line) => line.trimEnd()).filter((line) => line.length > 0);
    for (const line of printed.slice(-3)) lines.push({ text: line.length <= 160 ? line : `${line.slice(0, 160)}…`, tone: "plain" });
    return lines;
  }

  if (toolName === "readFile") {
    const total = number(result, "totalLines");
    const end = number(result, "endLine");
    const start = number(result, "startLine");
    return result.truncated === true && total !== null && end !== null && start !== null
      ? [{ text: `lines ${start}–${end} of ${total}`, tone: "plain" }]
      : [];
  }

  if (toolName === "grep" && Array.isArray(result.matches)) {
    const shown = result.matches.length;
    const total = number(result, "totalMatches");
    return [{ text: shown === 0 ? "no matches" : `${shown} match${shown === 1 ? "" : "es"}${total && total > shown ? ` of ${total}` : ""}`, tone: "plain" }];
  }

  // A helper's report goes to the agent; here, how the helper's work went and how its report begins.
  if (toolName === "task") {
    const calls = number(result, "toolCalls") ?? 0;
    const lines: ResultLine[] = [{ text: `helper ${result.complete === true ? "finished" : "stopped"} · ${calls} tool call${calls === 1 ? "" : "s"}`, tone: "plain" }];
    if (typeof result.note === "string") lines.push({ text: result.note, tone: "problem" });
    const report = (typeof result.report === "string" ? result.report : "").split("\n").map((line) => line.trimEnd()).filter((line) => line.length > 0);
    for (const line of report.slice(0, MAX_REPORT_SHOWN)) lines.push({ text: line.length <= 160 ? line : `${line.slice(0, 160)}…`, tone: "plain" });
    if (report.length > MAX_REPORT_SHOWN) lines.push({ text: `… ${report.length - MAX_REPORT_SHOWN} more line(s) in its report`, tone: "plain" });
    return lines;
  }

  // The list itself is shown in the task strip; here, where it stands.
  if (toolName === "updateTasks" && typeof result.summary === "string") return [{ text: result.summary, tone: "plain" }];

  if (toolName === "glob" && Array.isArray(result.files)) {
    return [{ text: `${result.files.length} file${result.files.length === 1 ? "" : "s"}${result.truncated === true ? " (more exist)" : ""}`, tone: "plain" }];
  }

  return [];
}
