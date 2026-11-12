import { afterEach, beforeEach, expect, test } from "bun:test";
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { carryOver, cerberusHome } from "./home";

let root = "";
beforeEach(() => {
  root = mkdtempSync(join(tmpdir(), "cerberus-home-"));
});
afterEach(() => rmSync(root, { recursive: true, force: true }));

test("what was kept under the former name is carried over, and the old directory goes", () => {
  const former = join(root, "old");
  const current = join(root, "new");
  mkdirSync(join(former, "undo", "s1"), { recursive: true });
  writeFileSync(join(former, "auth.json"), '{"token":"t"}');
  writeFileSync(join(former, "undo", "s1", "turn.json"), "{}");

  const moved = carryOver(former, current);

  expect(moved.sort()).toEqual(["auth.json", "undo"]);
  expect(readFileSync(join(current, "auth.json"), "utf-8")).toBe('{"token":"t"}');
  expect(existsSync(join(current, "undo", "s1", "turn.json"))).toBe(true);
  expect(existsSync(former)).toBe(false);
});

test("nothing that is already there is overwritten; what could not move stays where it was", () => {
  const former = join(root, "old");
  const current = join(root, "new");
  mkdirSync(former);
  mkdirSync(current);
  writeFileSync(join(former, "auth.json"), "old sign-in");
  writeFileSync(join(former, "preferences.json"), "old theme");
  writeFileSync(join(current, "auth.json"), "new sign-in");

  const moved = carryOver(former, current);

  expect(moved).toEqual(["preferences.json"]);
  expect(readFileSync(join(current, "auth.json"), "utf-8")).toBe("new sign-in");
  expect(readFileSync(join(former, "auth.json"), "utf-8")).toBe("old sign-in");   // still there, not lost
});

test("with nothing to carry over, nothing is made", () => {
  expect(carryOver(join(root, "none"), join(root, "new"))).toEqual([]);
  expect(existsSync(join(root, "new"))).toBe(false);
});

test("CERBERUS_HOME names the directory outright", () => {
  const before = process.env.CERBERUS_HOME;
  process.env.CERBERUS_HOME = join(root, "elsewhere");
  try {
    expect(cerberusHome()).toBe(join(root, "elsewhere"));
  } finally {
    if (before === undefined) delete process.env.CERBERUS_HOME;
    else process.env.CERBERUS_HOME = before;
  }
});
