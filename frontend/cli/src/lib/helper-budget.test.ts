import { expect, test } from "bun:test";
import { HelperReply, MAX_HELPERS_PER_REPLY } from "./helper-budget";

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (error: Error) => void;
  const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
}

test("parallel task calls are admitted immediately but start one at a time", async () => {
  const reply = new HelperReply<string>();
  const firstRun = deferred<string>();
  const firstStarted = deferred<void>();
  const starts: string[] = [];
  const first = reply.request("one", async () => {
    starts.push("one");
    firstStarted.resolve();
    return firstRun.promise;
  });
  const second = reply.request("two", async () => { starts.push("two"); return "second"; });
  await firstStarted.promise;
  expect(starts).toEqual(["one"]);
  firstRun.resolve("first");
  expect(await Promise.all([first.result, second.result])).toEqual(["first", "second"]);
  expect(starts).toEqual(["one", "two"]);
});

test("replayed task ids share in-flight and completed results without starting again", async () => {
  const reply = new HelperReply<string>();
  const run = deferred<string>();
  let starts = 0;
  const first = reply.request("same", async () => { starts++; return run.promise; });
  const duplicate = reply.request("same", async () => { throw new Error("must not run"); });
  expect(first.first).toBe(true);
  expect(duplicate.first).toBe(false);
  expect(duplicate.result).toBe(first.result);
  run.resolve("report");
  expect(await duplicate.result).toBe("report");
  const completed = reply.request("same", async () => { throw new Error("must not run"); });
  expect(completed.first).toBe(false);
  expect(await completed.result).toBe("report");
  expect(starts).toBe(1);
});

test("a refused or failed helper start still consumes one of the reply's four places", async () => {
  const reply = new HelperReply<string>();
  let starts = 0;
  for (let i = 0; i < MAX_HELPERS_PER_REPLY; i++) {
    await expect(reply.request(`failed-${i}`, async () => {
      starts++;
      throw new Error("session refused");
    }).result).rejects.toThrow("session refused");
  }
  await expect(reply.request("retry", async () => { starts++; return "must not run"; }).result).rejects.toThrow("limit of 4 helpers");
  const duplicate = reply.request("retry", async () => { starts++; return "must not run"; });
  expect(duplicate.first).toBe(false);
  await expect(duplicate.result).rejects.toThrow("limit of 4 helpers");
  expect(starts).toBe(MAX_HELPERS_PER_REPLY);
});

test("cancellation settles queued tasks promptly and prevents late and queued starts", async () => {
  const reply = new HelperReply<string>();
  const active = deferred<string>();
  const started = deferred<void>();
  const starts: string[] = [];
  const first = reply.request("one", async (signal) => {
    starts.push("one");
    expect(signal).toBe(reply.signal);
    started.resolve();
    return active.promise;
  });
  const second = reply.request("two", async () => { starts.push("two"); return "never"; });
  const settled = Promise.allSettled([first.result, second.result]);
  await started.promise;
  reply.cancel();
  expect(await settled).toMatchObject([{ status: "rejected" }, { status: "rejected" }]);
  expect(reply.signal.aborted).toBe(true);
  await expect(reply.request("late", async () => { starts.push("late"); return "never"; }).result).rejects.toThrow("interrupted");
  active.reject(new Error("a late transport failure is handled"));
  await Promise.resolve();
  expect(starts).toEqual(["one"]);
});

test("only a new user reply renews the allowance and its cancellation is independent", async () => {
  const oldReply = new HelperReply<string>();
  for (let i = 0; i < MAX_HELPERS_PER_REPLY; i++) {
    expect(await oldReply.request(`call-${i}`, async () => "old report").result).toBe("old report");
  }
  await expect(oldReply.request("continuation", async () => "never").result).rejects.toThrow("limit of 4 helpers");
  oldReply.cancel();
  const nextReply = new HelperReply<string>();
  expect(await nextReply.request("call-0", async () => "new report").result).toBe("new report");
  expect(nextReply.signal.aborted).toBe(false);
  await expect(oldReply.request("late", async () => "never").result).rejects.toThrow("interrupted");
});
