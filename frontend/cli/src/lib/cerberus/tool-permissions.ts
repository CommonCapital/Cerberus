import { looksSecret } from "./sensitive-files";
import { isCovered, rulesToRemember } from "./shell-commands";

// Which chat tool calls the operator is asked about before the CLI runs them
// on their machine. Mirrors the backend's permission policy
// (Cerberus.Authorization/Permissions.cs) so the CLI and the server agree.

export type PermissionMode = "Default" | "Ask";

/** `allowedCommands` are command rules: prefixes such as "git status" that run without asking (see shell-commands.ts). */
export type Policy = { mode: PermissionMode; allowedKinds: string[]; allowedCommands?: string[] };

export const STANDARD_POLICY: Policy = { mode: "Default", allowedKinds: [] };

/**
 * What "always allow" would remember for this call: for a shell command its
 * command rules ("git status"), for anything else its kind of action. Null
 * when nothing can be remembered — a secrets file, or a command line that
 * always has to be looked at.
 */
export function whatAlwaysRemembers(toolName: string, input: unknown): { kind: ActionKind } | { commands: string[] } | null {
  if (isSensitiveRead(toolName, input)) return null;
  if (toolName !== "bash") return { kind: kindOfTool(toolName) };
  const commands = rulesToRemember(field(input, "command"));
  return commands ? { commands } : null;
}

export type ActionKind = "read-files" | "edit-files" | "run-commands";

const TOOL_KINDS: Record<string, ActionKind> = {
  readFile: "read-files",
  listDirectory: "read-files",
  glob: "read-files",
  grep: "read-files",
  writeFile: "edit-files",
  editFile: "edit-files",
  bash: "run-commands",
};

/** The kind of action a tool performs. A tool this client does not know is treated as the riskiest kind. */
export function kindOfTool(toolName: string): ActionKind {
  return TOOL_KINDS[toolName] ?? "run-commands";
}

const field = (input: unknown, name: string): string => {
  const value = input && typeof input === "object" ? (input as Record<string, unknown>)[name] : undefined;
  return typeof value === "string" ? value : "";
};

/**
 * Whether this call reads a file that probably holds secrets. Such a read is
 * always asked about by name — also when reading files is otherwise allowed —
 * because what is read goes to the model.
 */
export function isSensitiveRead(toolName: string, input: unknown): boolean {
  return toolName === "readFile" && looksSecret(field(input, "path"));
}

/**
 * Whether the operator must approve this tool call first. "Always allow" for
 * the kind wins in either mode; otherwise reading asks only in Ask mode, and
 * editing files or running commands always asks. Reading a file that probably
 * holds secrets asks every time, whatever the policy.
 */
export function toolNeedsApproval(policy: Policy, toolName: string, input?: unknown): boolean {
  if (isSensitiveRead(toolName, input)) return true;
  const kind = kindOfTool(toolName);
  if (policy.allowedKinds.includes(kind)) return false;
  // A command the operator's rules cover runs without asking, in either mode: a rule is an explicit decision.
  if (toolName === "bash" && isCovered(field(input, "command"), policy.allowedCommands ?? [])) return false;
  return kind === "read-files" ? policy.mode === "Ask" : true;
}

export const KIND_LABEL: Record<ActionKind, string> = {
  "read-files": "reading files",
  "edit-files": "editing files",
  "run-commands": "running commands",
};

const clip = (text: string, max: number) => (text.length <= max ? text : text.slice(0, max) + "…");

/** What the operator is shown: one line saying what will happen, and the detail that matters. */
export function describeToolCall(toolName: string, input: unknown): { title: string; detail: string } {
  switch (toolName) {
    case "bash":
      return { title: "Run a shell command", detail: clip(field(input, "command"), 400) };
    case "writeFile": {
      const content = field(input, "content");
      return {
        title: `Write ${field(input, "path") || "a file"}`,
        detail: `${content.length} characters, ${content.split("\n").length} lines`,
      };
    }
    case "editFile":
      return {
        title: `Edit ${field(input, "path") || "a file"}`,
        detail: `replace: ${clip(field(input, "oldString"), 120)}\nwith:    ${clip(field(input, "newString"), 120)}`,
      };
    case "readFile":
      return isSensitiveRead(toolName, input)
        ? { title: `Read ${field(input, "path")}`, detail: "This file probably holds secrets. What is read is sent to the model." }
        : { title: `Read ${field(input, "path") || "a file"}`, detail: "" };
    case "listDirectory":
      return { title: `List ${field(input, "path") || "."}`, detail: "" };
    case "glob":
      return { title: `Find files matching ${field(input, "pattern")}`, detail: field(input, "path") };
    case "grep":
      return { title: `Search for ${clip(field(input, "pattern"), 80)}`, detail: field(input, "path") };
    default:
      return { title: `Run the tool ${toolName}`, detail: clip(JSON.stringify(input ?? {}), 300) };
  }
}

export const DENIED_MESSAGE = "The operator denied this tool call. Do not retry it; ask what they would like instead.";

/** What the model is told about a denied call: the operator's own words when they gave some, so it can carry on their way. */
export const deniedMessage = (feedback?: string) =>
  feedback ? `The operator denied this tool call and said: "${feedback}". Do not retry it as it was; do what they said.` : DENIED_MESSAGE;

/** The operator's answer: yes once, yes always for this kind of action, no — or no with what to do instead. */
export type GateDecision = "allow" | "always" | "deny" | { deny: string };

export type GateResult = {
  /** Whether the tool may run. */
  run: boolean;
  /** What the operator said when denying, if anything. */
  feedback?: string;
  /** The policy to use from now on (changed only by "always"). */
  policy: Policy;
  /** The kind the operator chose to always allow, to be remembered on the server. */
  remembered: ActionKind | null;
  /** The command rules the operator chose to always allow, to be remembered on the server. */
  rememberedCommands?: string[];
};

/**
 * Decides one tool call: runs it straight away if the policy allows, otherwise
 * asks the operator. Asking is the only way a call that needs approval can run.
 */
export async function gateToolCall(
  policy: Policy,
  toolName: string,
  input: unknown,
  ask: (request: { toolName: string; input: unknown }) => Promise<GateDecision>,
): Promise<GateResult> {
  if (!toolNeedsApproval(policy, toolName, input)) return { run: true, policy, remembered: null };

  const decision = await ask({ toolName, input });
  if (typeof decision === "object") return { run: false, policy, remembered: null, feedback: decision.deny.trim() || undefined };
  if (decision === "deny") return { run: false, policy, remembered: null };
  // Where "always" has nothing it could remember (a secrets file, a command line that must be looked at), it allows this once.
  const remember = whatAlwaysRemembers(toolName, input);
  if (decision === "allow" || remember === null) return { run: true, policy, remembered: null };

  if ("commands" in remember) {
    const allowedCommands = [...new Set([...(policy.allowedCommands ?? []), ...remember.commands])];
    return { run: true, policy: { ...policy, allowedCommands }, remembered: null, rememberedCommands: remember.commands };
  }

  const kind = remember.kind;
  return {
    run: true,
    policy: { ...policy, allowedKinds: [...new Set([...policy.allowedKinds, kind])] },
    remembered: kind,
  };
}
