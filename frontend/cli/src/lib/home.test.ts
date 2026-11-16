import { expect, test } from "bun:test";
import { homedir } from "node:os";
import { join } from "node:path";
import { cerberusHome } from "./home";

function withHome<T>(value: string | undefined, run: () => T): T {
  const before = process.env.CERBERUS_HOME;
  if (value === undefined) delete process.env.CERBERUS_HOME;
  else process.env.CERBERUS_HOME = value;
  try {
    return run();
  } finally {
    if (before === undefined) delete process.env.CERBERUS_HOME;
    else process.env.CERBERUS_HOME = before;
  }
}

test("CERBERUS_HOME names the directory outright", () => {
  expect(withHome("/tmp/elsewhere", cerberusHome)).toBe("/tmp/elsewhere");
});

test("without it, the directory is .cerberus in the home directory", () => {
  expect(withHome(undefined, cerberusHome)).toBe(join(homedir(), ".cerberus"));
});
