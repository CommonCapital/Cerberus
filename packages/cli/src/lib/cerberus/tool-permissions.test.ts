import { describe, expect, test } from "bun:test";
import {
  deniedMessage,
  describeToolCall,
  gateToolCall,
  isSensitiveRead,
  kindOfTool,
  STANDARD_POLICY,
  toolNeedsApproval,
  type GateDecision,
  type Policy,
} from "./tool-permissions";

const READ = ["readFile", "listDirectory", "glob", "grep"];
const CHANGE = ["writeFile", "editFile", "bash"];
const ask: Policy = { mode: "Ask", allowedKinds: [] };

describe("toolNeedsApproval", () => {
  test("by default reading runs and anything that changes things asks", () => {
    for (const tool of READ) expect(toolNeedsApproval(STANDARD_POLICY, tool)).toBe(false);
    for (const tool of CHANGE) expect(toolNeedsApproval(STANDARD_POLICY, tool)).toBe(true);
  });

  test("in ask mode everything asks", () => {
    for (const tool of [...READ, ...CHANGE]) expect(toolNeedsApproval(ask, tool)).toBe(true);
  });

  test("always allow covers its own kind only, in either mode", () => {
    for (const mode of ["Default", "Ask"] as const) {
      const policy: Policy = { mode, allowedKinds: ["edit-files"] };
      expect(toolNeedsApproval(policy, "writeFile")).toBe(false);
      expect(toolNeedsApproval(policy, "editFile")).toBe(false);
      expect(toolNeedsApproval(policy, "bash")).toBe(true);
    }
  });

  test("a tool this client does not know is treated as running a command", () => {
    expect(kindOfTool("launchMissiles")).toBe("run-commands");
    expect(toolNeedsApproval(STANDARD_POLICY, "launchMissiles")).toBe(true);
    expect(toolNeedsApproval({ mode: "Default", allowedKinds: ["edit-files", "read-files"] }, "launchMissiles")).toBe(true);
  });
});

describe("describeToolCall", () => {
  test("a command is shown in full, up to a limit", () => {
    expect(describeToolCall("bash", { command: "rm -rf build && npm test" })).toEqual({
      title: "Run a shell command",
      detail: "rm -rf build && npm test",
    });
    expect(describeToolCall("bash", { command: "x".repeat(1000) }).detail.length).toBeLessThan(410);
  });

  test("a write names the file and its size, not its whole content", () => {
    expect(describeToolCall("writeFile", { path: "src/a.ts", content: "one\ntwo\nthree" })).toEqual({
      title: "Write src/a.ts",
      detail: "13 characters, 3 lines",
    });
  });

  test("an edit shows what is replaced by what", () => {
    const { title, detail } = describeToolCall("editFile", { path: "a.py", oldString: "md5(", newString: "sha256(" });
    expect(title).toBe("Edit a.py");
    expect(detail).toBe("replace: md5(\nwith:    sha256(");
  });

  test("missing or malformed input does not break the prompt", () => {
    expect(describeToolCall("bash", null).detail).toBe("");
    expect(describeToolCall("writeFile", { path: 7 }).title).toBe("Write a file");
    expect(describeToolCall("mystery", { a: 1 }).title).toBe("Run the tool mystery");
  });
});

describe("gateToolCall", () => {
  const asker = (answer: GateDecision) => {
    const asked: string[] = [];
    return { asked, ask: async (request: { toolName: string }) => (asked.push(request.toolName), answer) };
  };

  test("a call the policy allows runs without asking", async () => {
    const { asked, ask } = asker("deny");

    const result = await gateToolCall(STANDARD_POLICY, "readFile", { path: "a" }, ask);

    expect(result).toEqual({ run: true, policy: STANDARD_POLICY, remembered: null });
    expect(asked).toEqual([]);
  });

  test("a call that needs approval is asked about, and a no stops it", async () => {
    const { asked, ask } = asker("deny");

    const result = await gateToolCall(STANDARD_POLICY, "bash", { command: "ls" }, ask);

    expect(result.run).toBe(false);
    expect(asked).toEqual(["bash"]);
  });

  test("yes runs it once and changes nothing else", async () => {
    const result = await gateToolCall(STANDARD_POLICY, "bash", {}, asker("allow").ask);

    expect(result).toEqual({ run: true, policy: STANDARD_POLICY, remembered: null });
  });

  test("always runs it and stops the asking for that kind from then on", async () => {
    const first = await gateToolCall(STANDARD_POLICY, "writeFile", {}, asker("always").ask);
    expect(first.run).toBe(true);
    expect(first.remembered).toBe("edit-files");
    expect(first.policy.allowedKinds).toEqual(["edit-files"]);

    // With the updated policy the next edit is not asked about, but a command still is.
    const next = asker("deny");
    expect((await gateToolCall(first.policy, "editFile", {}, next.ask)).run).toBe(true);
    expect((await gateToolCall(first.policy, "bash", {}, next.ask)).run).toBe(false);
    expect(next.asked).toEqual(["bash"]);
  });
});

describe("files that hold secrets", () => {
  const read = (path: string) => isSensitiveRead("readFile", { path });

  test("env files, key files and credential files are recognised; their templates and ordinary files are not", () => {
    for (const path of [".env", "app/.env.local", ".env.production", "~/.netrc", ".npmrc", "keys/server.pem", "id_rsa", "deploy/id_ed25519", "tls/site.key"]) {
      expect(read(path)).toBe(true);
    }
    for (const path of [".env.example", ".env.sample", "config/.env.template", "environment.ts", "src/keyboard.ts", "README.md", "monkey.py"]) {
      expect(read(path)).toBe(false);
    }
    expect(isSensitiveRead("grep", { path: ".env" })).toBe(false);   // only a direct read of the file
  });

  test("reading one asks every time, whatever is always allowed", async () => {
    const generous: Policy = { mode: "Default", allowedKinds: ["read-files", "edit-files", "run-commands"] };
    expect(toolNeedsApproval(generous, "readFile", { path: ".env" })).toBe(true);
    expect(toolNeedsApproval(generous, "readFile", { path: "main.py" })).toBe(false);

    // "Always" allows this one read and remembers nothing.
    const result = await gateToolCall(STANDARD_POLICY, "readFile", { path: ".env" }, async () => "always");
    expect(result).toEqual({ run: true, policy: STANDARD_POLICY, remembered: null });
    expect(describeToolCall("readFile", { path: ".env" }).detail).toContain("probably holds secrets");
  });
});

describe("denying with a reason", () => {
  test("the operator's words go back with the refusal, and nothing is remembered", async () => {
    const result = await gateToolCall(STANDARD_POLICY, "bash", { command: "npm publish" }, async () => ({ deny: "  run the tests first  " }));

    expect(result).toEqual({ run: false, policy: STANDARD_POLICY, remembered: null, feedback: "run the tests first" });
    expect(deniedMessage(result.feedback)).toBe('The operator denied this tool call and said: "run the tests first". Do not retry it as it was; do what they said.');
  });

  test("an empty reason is a plain refusal", async () => {
    const result = await gateToolCall(STANDARD_POLICY, "bash", { command: "x" }, async () => ({ deny: "   " }));
    expect(result.feedback).toBeUndefined();
    expect(deniedMessage(result.feedback)).toContain("Do not retry it; ask what they would like instead.");
  });
});

describe("command rules", () => {
  const withRules = (allowedCommands: string[]): Policy => ({ mode: "Default", allowedKinds: [], allowedCommands });

  test("a command the rules cover runs without asking, in either mode; any other still asks", () => {
    expect(toolNeedsApproval(withRules(["git status"]), "bash", { command: "git status -s" })).toBe(false);
    expect(toolNeedsApproval({ ...withRules(["git status"]), mode: "Ask" }, "bash", { command: "git status" })).toBe(false);
    expect(toolNeedsApproval(withRules(["git status"]), "bash", { command: "git push" })).toBe(true);
    expect(toolNeedsApproval(withRules(["git status"]), "bash", { command: "git status && rm -rf ." })).toBe(true);
    expect(toolNeedsApproval(STANDARD_POLICY, "bash", { command: "git status" })).toBe(true);
    // Rules are about commands: they do not allow edits.
    expect(toolNeedsApproval(withRules(["git status"]), "writeFile", { path: "a", content: "" })).toBe(true);
  });

  test("always remembers the command, not every command", async () => {
    const result = await gateToolCall(withRules(["ls"]), "bash", { command: "cd app && npm test" }, async () => "always");

    expect(result.run).toBe(true);
    expect(result.remembered).toBeNull();                       // not the whole kind
    expect(result.rememberedCommands).toEqual(["cd", "npm test"]);
    expect(result.policy.allowedCommands).toEqual(["ls", "cd", "npm test"]);
    expect(result.policy.allowedKinds).toEqual([]);
    expect(toolNeedsApproval(result.policy, "bash", { command: "npm test -- --watch" })).toBe(false);
  });

  test("a command that must always be looked at is allowed once and nothing is remembered", async () => {
    const result = await gateToolCall(STANDARD_POLICY, "bash", { command: "rm -rf build" }, async () => "always");
    expect(result).toEqual({ run: true, policy: STANDARD_POLICY, remembered: null });
  });

  test("with every command always allowed, nothing asks — except what names a secrets file", () => {
    const all: Policy = { mode: "Default", allowedKinds: ["run-commands"] };
    expect(toolNeedsApproval(all, "bash", { command: "rm -rf build" })).toBe(false);
    expect(toolNeedsApproval(withRules(["cat"]), "bash", { command: "cat .env" })).toBe(true);
  });
});
