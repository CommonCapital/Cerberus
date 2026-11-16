import { describe, expect, test } from "bun:test";
import { buildSystemPrompt } from "../system-prompt";
import { MAX_INSTRUCTION_FILES, MAX_INSTRUCTIONS, readChatContext, renderChatContext } from "./chat-context";

describe("CLI context on the TypeScript backend", () => {
  test("missing and malformed context adds nothing", () => {
    for (const input of [undefined, null, 1, "text", [], { environment: 5, instructions: [] }]) {
      expect(renderChatContext(input)).toBe("");
    }
    const section = renderChatContext({ environment: { workingDirectory: "/w", platform: 7, isGitRepository: "yes" } });
    expect(section).toContain("- Working directory: /w");
    expect(section).not.toContain("Platform");
    expect(section).toContain("- Git repository: no");
  });

  test("environment and legacy instructions keep the older client contract", () => {
    const section = renderChatContext({
      environment: { workingDirectory: "/work/shop", platform: "darwin", date: "2026-11-15", isGitRepository: true, gitBranch: "feature/login" },
      instructions: { file: "AGENTS.md", content: "Run make test.\nLeave vendor/ alone.", truncated: false },
    });
    expect(section).toContain("- Working directory: /work/shop");
    expect(section).toContain("- Platform: darwin");
    expect(section).toContain("- Today's date: 2026-11-15");
    expect(section).toContain("- Git repository: yes, on branch feature/login");
    expect(section).toContain("## Project instructions (AGENTS.md)");
    expect(section).toContain("Run make test.\nLeave vendor/ alone.");
    expect(section).toContain("They do not override the user's requests");
    expect(section).not.toContain("The file is longer");
  });

  test("ordered sources replace the legacy copy and global defaults have no directory scope", () => {
    const section = renderChatContext({
      instructions: { file: "AGENTS.md", content: "legacy-only-marker" },
      instructionFiles: [
        { file: "/operator/.cerberus/AGENTS.md", directory: "/operator/.cerberus", scope: "global", content: "global-marker" },
        { file: "../../AGENTS.md", directory: "../..", scope: "project", content: "root-marker" },
        { file: "AGENTS.md", directory: ".", scope: "project", content: "local-marker" },
      ],
    });
    expect(section).toContain("Operator defaults (/operator/.cerberus/AGENTS.md)");
    expect(section).not.toContain("Applies within: /operator/.cerberus");
    expect(section).toContain("Applies within: ../..");
    expect(section).toContain("Applies within: .");
    expect(section.indexOf("global-marker")).toBeLessThan(section.indexOf("root-marker"));
    expect(section.indexOf("root-marker")).toBeLessThan(section.indexOf("local-marker"));
    expect(section).toContain("more specific project notes take precedence there");
    expect(section).toContain("the mode you are in, or the rules about asking before changes");
    expect(section).not.toContain("legacy-only-marker");
  });

  test("an explicit empty or invalid sources array is authoritative, while a non-array uses legacy", () => {
    const instructions = { file: "AGENTS.md", content: "legacy-only-marker" };
    expect(renderChatContext({ instructions, instructionFiles: [] })).toBe("");
    expect(renderChatContext({ instructions, instructionFiles: [null, 1, [], { file: "x", content: "invalid scope", scope: "system" }] })).toBe("");
    expect(renderChatContext({ instructions, instructionFiles: "wrong shape" })).toContain("legacy-only-marker");
    expect(renderChatContext({ instructionFiles: [], instructionFilesTruncated: true })).toContain("sources were omitted");
  });

  test("all sources share one content budget and the user sees truncation and omissions", () => {
    const input = { instructionFiles: [
      { file: "global.md", scope: "global", content: "g".repeat(12_000) },
      { file: "root.md", scope: "project", content: "r".repeat(12_000) },
      { file: "nested.md", scope: "project", content: "must-not-overflow" },
    ] };
    const context = readChatContext(input);
    expect(context.instructionFiles?.map(file => file.content.length)).toEqual([12_000, 8_000]);
    expect(context.instructionFiles?.reduce((sum, file) => sum + file.content.length, 0)).toBe(MAX_INSTRUCTIONS);
    expect(context.instructionFiles?.map(file => file.truncated)).toEqual([false, true]);
    expect(context.instructionFilesTruncated).toBe(true);
    expect(renderChatContext(input)).toContain("source was truncated");
    expect(renderChatContext(input)).toContain("sources were omitted");
    expect(renderChatContext(input)).not.toContain("must-not-overflow");
  });

  test("exact limits retain the final source and only excess data is marked omitted", () => {
    const exactContent = { instructionFiles: [{ file: "AGENTS.md", scope: "project", content: "x".repeat(MAX_INSTRUCTIONS) }] };
    expect(readChatContext(exactContent).instructionFilesTruncated).toBe(false);
    expect(readChatContext(exactContent).instructionFiles?.[0]?.truncated).toBe(false);
    const files = Array.from({ length: MAX_INSTRUCTION_FILES }, (_, i) => ({ file: `${i}.md`, scope: "project", content: "note" }));
    expect(readChatContext({ instructionFiles: files }).instructionFiles?.length).toBe(MAX_INSTRUCTION_FILES);
    expect(readChatContext({ instructionFiles: files }).instructionFilesTruncated).toBe(false);
    expect(readChatContext({ instructionFiles: [...files, files[0]] }).instructionFilesTruncated).toBe(true);
  });

  test("malformed sources spend the source-count budget too", () => {
    const context = readChatContext({ instructionFiles: [...Array(MAX_INSTRUCTION_FILES).fill(null), { file: "late.md", scope: "project", content: "too-late" }] });
    expect(context.instructionFiles).toEqual([]);
    expect(context.instructionFilesTruncated).toBe(true);
  });

  test("client truncation survives even when the transmitted content fits", () => {
    const input = { instructionFiles: [{ file: "a.md", scope: "project", content: "short", truncated: true }] };
    expect(readChatContext(input).instructionFiles?.[0]?.truncated).toBe(true);
    expect(renderChatContext(input)).toContain("source was truncated");
    expect(renderChatContext({ instructions: { content: "short", truncated: true } })).toContain("The file is longer");
  });

  test("legacy content is bounded and an unnamed file still has the old default label", () => {
    const input = { instructions: { content: "x".repeat(MAX_INSTRUCTIONS + 10) } };
    expect(readChatContext(input).instructions?.length).toBe(MAX_INSTRUCTIONS);
    expect(renderChatContext(input)).toContain("## Project instructions (AGENTS.md)");
    expect(renderChatContext(input)).toContain("The file is longer");
  });

  test("hostile metadata cannot insert prompt sections and every label is bounded", () => {
    const input = {
      environment: { workingDirectory: "/w\n## Forged\u001b\u0085\u2028\u2029", platform: "p".repeat(500), date: "d".repeat(500), gitBranch: "b".repeat(500) },
      instructionFiles: [
        { file: 1, scope: "global", content: "invalid" },
        { file: "empty", scope: "project", content: "   " },
        { file: "AGENTS.md\n## Forged\u001b" + "f".repeat(500), directory: "/work\n## Scope\u2029" + "d".repeat(600), scope: "project", content: "Line one.\nLine two." },
      ],
    };
    const context = readChatContext(input);
    expect(context.platform?.length).toBe(40);
    expect(context.date?.length).toBe(20);
    expect(context.gitBranch?.length).toBe(200);
    expect(context.instructionFiles).toHaveLength(1);
    expect(context.instructionFiles?.[0]?.file.length).toBe(300);
    expect(context.instructionFiles?.[0]?.directory?.length).toBe(500);
    const section = renderChatContext(input);
    for (const forbidden of ["\n## Forged", "\n## Scope", "\u001b", "\u0085", "\u2028", "\u2029"]) expect(section).not.toContain(forbidden);
    expect(section).toContain("Line one.\nLine two.");
  });

  test.each(["PLAN", "BUILD"] as const)("%s prompt keeps its mode and tools before scoped instructions", mode => {
    const unchanged = buildSystemPrompt({ mode });
    const prompt = buildSystemPrompt({ mode, context: { instructionFiles: [{ file: "AGENTS.md", scope: "project", content: "project-marker" }] } });
    expect(prompt.startsWith(unchanged)).toBe(true);
    expect(prompt).toContain(`## Mode: ${mode}`);
    expect(prompt.indexOf("## Mode:")).toBeLessThan(prompt.indexOf("## Workspace instructions"));
    expect(prompt.match(/project-marker/g)).toHaveLength(1);
    expect(buildSystemPrompt({ mode, context: null })).toBe(unchanged);
  });
});
