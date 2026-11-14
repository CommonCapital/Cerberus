import { expect, test } from "bun:test";
import { describeQueue } from "./queue";

test("a waiting run says where it stands, in words", () => {
  expect(describeQueue({ ahead: 0, others: 0 })).toBe("Waiting its turn: next of yours to start");
  expect(describeQueue({ ahead: 1, others: 0 })).toBe("Waiting its turn: 1 of your runs is ahead of it");
  expect(describeQueue({ ahead: 4, others: 1 })).toBe("Waiting its turn: 4 of your runs are ahead of it · places are shared with 1 other person");
  expect(describeQueue({ ahead: 0, others: 3 })).toBe("Waiting its turn: next of yours to start · places are shared with 3 other people");
});

test("a run that is not waiting has nothing to say", () => {
  expect(describeQueue(null)).toBeNull();
  expect(describeQueue(undefined)).toBeNull();
});
