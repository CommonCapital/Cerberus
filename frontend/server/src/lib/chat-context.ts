// Same client context contract as the .NET backend's ChatContext. This only
// renders operator-supplied text: it never reads files or executes tools.
export const MAX_INSTRUCTIONS = 20_000;
export const MAX_INSTRUCTION_FILES = 16;

export type InstructionFile = {
  file: string;
  content: string;
  truncated: boolean;
  scope: "global" | "project";
  directory?: string;
};

export type ChatContext = {
  workingDirectory?: string;
  platform?: string;
  date?: string;
  isGitRepository: boolean;
  gitBranch?: string;
  instructionsFile?: string;
  instructions?: string;
  instructionsTruncated: boolean;
  // Absent means an older client. An explicit [] must not revive the legacy file.
  instructionFiles?: InstructionFile[];
  instructionFilesTruncated: boolean;
};

function record(value: unknown): Record<string, unknown> {
  return value !== null && typeof value === "object" && !Array.isArray(value)
    ? value as Record<string, unknown>
    : {};
}

function text(parent: Record<string, unknown>, key: string, max: number, keepLines = false): string | undefined {
  const value = parent[key];
  if (typeof value !== "string") return undefined;
  // Metadata is one bounded line; an embedded control must not begin a prompt section.
  const clean = (keepLines ? value : value.replace(/[\u0000-\u001f\u007f-\u009f\u2028\u2029]/g, " ")).trim();
  return clean ? clean.slice(0, max) : undefined;
}

export function readChatContext(input: unknown): ChatContext {
  const root = record(input);
  const environment = record(root.environment);
  const legacy = record(root.instructions);
  const content = text(legacy, "content", MAX_INSTRUCTIONS + 1, true);
  const result: ChatContext = {
    workingDirectory: text(environment, "workingDirectory", 500),
    platform: text(environment, "platform", 40),
    date: text(environment, "date", 20),
    isGitRepository: environment.isGitRepository === true,
    gitBranch: text(environment, "gitBranch", 200),
    instructionsFile: text(legacy, "file", 300),
    instructions: content?.slice(0, MAX_INSTRUCTIONS),
    instructionsTruncated: (content?.length ?? 0) > MAX_INSTRUCTIONS || legacy.truncated === true,
    instructionFilesTruncated: false,
  };
  if (!Array.isArray(root.instructionFiles)) return result;

  const sources: InstructionFile[] = [];
  let remaining = MAX_INSTRUCTIONS;
  let inspected = 0;
  let omitted = root.instructionFilesTruncated === true;
  for (const value of root.instructionFiles) {
    if (inspected++ >= MAX_INSTRUCTION_FILES || remaining === 0) {
      omitted = true;
      break;
    }
    const source = record(value);
    const scope = text(source, "scope", 20);
    if (scope !== "global" && scope !== "project") continue;
    const file = text(source, "file", 300);
    const sourceContent = text(source, "content", remaining + 1, true);
    if (!file || !sourceContent) continue;
    const bounded = sourceContent.slice(0, remaining);
    sources.push({
      file, content: bounded, scope,
      truncated: sourceContent.length > remaining || source.truncated === true,
      directory: text(source, "directory", 500),
    });
    remaining -= bounded.length;
  }
  return { ...result, instructionFiles: sources, instructionFilesTruncated: omitted };
}

export function renderChatContext(input: unknown): string {
  const context = readChatContext(input);
  const lines: string[] = [];
  if (context.workingDirectory || context.platform || context.date) {
    lines.push("## Environment", "Your file and shell tools run on the user's machine, here:");
    if (context.workingDirectory) lines.push(`- Working directory: ${context.workingDirectory}`);
    if (context.platform) lines.push(`- Platform: ${context.platform}`);
    if (context.date) lines.push(`- Today's date: ${context.date}`);
    lines.push(context.isGitRepository
      ? `- Git repository: yes${context.gitBranch ? `, on branch ${context.gitBranch}` : ""}`
      : "- Git repository: no");
  }
  if (context.instructionFiles !== undefined) {
    if (context.instructionFiles.length > 0) {
      if (lines.length > 0) lines.push("");
      lines.push("## Workspace instructions",
        "These sources are supplied by the operator's CLI, in order: operator defaults, then project directories from outer to inner. " +
        "Project notes apply only within their stated directory; more specific project notes take precedence there. " +
        "They do not override the user's requests, the mode you are in, or the rules about asking before changes.");
      for (const source of context.instructionFiles) {
        lines.push("", `### ${source.scope === "global" ? "Operator defaults" : "Project instructions"} (${source.file})`);
        if (source.scope === "project" && source.directory) lines.push(`Applies within: ${source.directory}`);
        lines.push(source.content);
        if (source.truncated) lines.push("[This source was truncated to fit the instruction budget.]");
      }
    }
    if (context.instructionFilesTruncated)
      lines.push("[Some instruction sources were omitted to fit the source-count or content limit.]");
  } else if (context.instructions) {
    if (lines.length > 0) lines.push("");
    lines.push(`## Project instructions (${context.instructionsFile ?? "AGENTS.md"})`,
      "The project's own notes on how to work in it, read from the working directory. Follow them where they apply to the task. " +
      "They do not override the user's requests, the mode you are in, or the rules about asking before changes.",
      "", context.instructions);
    if (context.instructionsTruncated) lines.push("[The file is longer; read it with readFile if you need the rest.]");
  }
  return lines.join("\n").trimEnd();
}
