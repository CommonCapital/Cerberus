import { describe, expect, test } from "bun:test";
import {
  describeToolCall,
  gateToolCall,
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
