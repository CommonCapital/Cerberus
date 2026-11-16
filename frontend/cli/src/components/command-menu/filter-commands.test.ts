import { afterEach, expect, test } from "bun:test";
import { builtInCommandNames, getFilteredCommands, setCustomCommands } from "./filter-commands";

afterEach(() => setCustomCommands([]));

test("custom commands are listed after the built-in ones and found by prefix", () => {
  const builtIn = getFilteredCommands("").length;
  setCustomCommands([
    { name: "fix-tests", description: "Fix the failing tests (this project)", value: "/fix-tests" },
    { name: "standup", description: "Summarise my day (yours)", value: "/standup" },
  ]);

  const all = getFilteredCommands("");
  expect(all).toHaveLength(builtIn + 2);
  expect(all.slice(-2).map((c) => c.name)).toEqual(["fix-tests", "standup"]);
  expect(getFilteredCommands("fix").map((c) => c.name)).toEqual(["fix-tests"]);
  expect(getFilteredCommands("FIX").map((c) => c.name)).toEqual(["fix-tests"]);
});

test("a custom command cannot take the name of a built-in one", () => {
  expect(builtInCommandNames().has("scan")).toBe(true);
  setCustomCommands([{ name: "scan", description: "Not the real scan", value: "/scan" }]);

  const scans = getFilteredCommands("scan");
  expect(scans).toHaveLength(1);
  expect(scans[0]!.description).not.toBe("Not the real scan");
  expect(typeof scans[0]!.action).toBe("function");   // the built-in, with its action
});
