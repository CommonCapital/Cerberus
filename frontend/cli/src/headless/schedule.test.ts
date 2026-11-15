import { expect, test } from "bun:test";
import { parseSchedule, runSchedule, scheduleTable, SCHEDULE_EXIT, type Schedule, type ScheduleDeps } from "./schedule";

const parse = (line: string) => parseSchedule(line.split(" ").filter(Boolean));
const refusal = (line: string) => {
  const parsed = parse(line);
  if (parsed.kind !== "usage") throw new Error(`not refused: ${JSON.stringify(parsed)}`);
  return parsed.message;
};

test("a schedule is a job written as for run, and how often", () => {
  expect(parse("schedule add scan services/api --every 1d --no-sca --name nightly")).toEqual({
    kind: "add",
    job: { type: "scan", path: "services/api", sca: false, exclusions: "honour", engagementId: undefined },
    every: "1d",
    name: "nightly",
    server: null,
  });
  const review = parse("schedule add --every=6H review . --agent-model m --agents sast --server http://host:3000");
  expect(review).toMatchObject({ kind: "add", every: "6h", name: null, server: "http://host:3000", job: { type: "review", agentModel: "m", agents: ["sast"] } });
});

test("the other actions name a schedule by its id", () => {
  expect(parse("schedule list")).toEqual({ kind: "list", server: null });
  expect(parse("schedule run 1a2b")).toEqual({ kind: "run", id: "1a2b", server: null });
  expect(parse("schedule pause 1a2b --server http://h")).toEqual({ kind: "pause", id: "1a2b", server: "http://h" });
  expect(parse("schedule resume 1a2b")).toMatchObject({ kind: "resume", id: "1a2b" });
  expect(parse("schedule remove 1a2b")).toMatchObject({ kind: "remove", id: "1a2b" });
  expect(parse("schedule").kind).toBe("help");
  expect(parse("schedule add --help").kind).toBe("help");
});

test("what cannot be meant is refused", () => {
  expect(refusal("schedule add scan .")).toContain("Say how often");
  expect(refusal("schedule add scan . --every soon")).toContain('"soon" is not an interval');
  expect(refusal("schedule add scan . --every")).toContain("--every needs a value");
  expect(refusal("schedule add scan . --every 1d --every 2d")).toContain("given twice");
  expect(refusal("schedule add pentest . --every 1d")).toContain('"pentest" is not something run can start');
  expect(refusal("schedule add sast . --every 1d")).toContain("--model");            // the job's own rules still hold
  expect(refusal("schedule add scan . --every 1d --model m")).toContain("--model does not apply to scan");
  expect(refusal("schedule frobnicate")).toContain("not something schedule can do");
  expect(refusal("schedule remove")).toContain("Say which schedule");
  expect(refusal("schedule list extra")).toContain("takes no arguments");
});

test("options about one run's result, or about approving in advance, do not apply to a schedule", () => {
  expect(refusal("schedule add scan . --every 1d --sarif out.sarif")).toContain("--sarif is about one run's result");
  expect(refusal("schedule add scan . --every 1d --fail-on=low")).toContain("--fail-on is about one run's result");
  const approve = refusal("schedule add review . --every 1d --approve run-plan");
  expect(approve).toContain("its runs follow your permission settings");
  expect(approve).toContain("always allow");
});

const schedule = (id: string, extra: Partial<Schedule> = {}): Schedule => ({
  id, name: `scan of ${id}`, type: "scan", targetPath: ".", everyMinutes: 1440, every: "every day", enabled: true,
  nextRunAt: "2026-11-13T02:00:00Z", lastRunAt: null, lastOutcome: null, lastJobId: null, ...extra,
});

function harness(stored: Schedule[], change: Partial<ScheduleDeps> = {}) {
  const seen = { out: [] as string[], log: [] as string[], calls: [] as string[] };
  const deps: ScheduleDeps = {
    list: async () => stored,
    create: async (request) => {
      seen.calls.push(`create ${JSON.stringify(request)}`);
      return schedule("9f8e7d6c5b4a", { name: request.name ?? "scan of .", every: "every 6 hours" });
    },
    update: async (id, change) => {
      seen.calls.push(`update ${id} ${JSON.stringify(change)}`);
      return { ...stored.find((s) => s.id === id)!, ...change };
    },
    remove: async (id) => void seen.calls.push(`remove ${id}`),
    runNow: async (id) => {
      seen.calls.push(`run ${id}`);
      return { outcome: "started", detail: "Started.", jobId: "abcdef0123456789" };
    },
    out: (line) => void seen.out.push(line),
    log: (line) => void seen.log.push(line),
    ...change,
  };
  return { deps, seen };
}

test("the list is a table of what runs when and how it last went", () => {
  const lines = scheduleTable([
    schedule("1a2b3c4d5e6f", { name: "Nightly scan", lastOutcome: "Started." }),
    schedule("2b3c4d5e6f70", { name: "Weekly review", every: "every week", enabled: false, lastOutcome: "Paused after 3 refusals in a row. Last: Refused: Unknown catalog 'x'" }),
  ]);

  expect(lines).toEqual([
    "ID        NAME           EVERY       NEXT RUN (UTC)    LAST",
    "1a2b3c4d  Nightly scan   every day   2026-11-13 02:00  Started.",
    "2b3c4d5e  Weekly review  every week  paused            Paused after 3 refusals in a row. Last: Refused: Unknown catalog 'x'",
  ]);
  expect(scheduleTable([])[0]).toContain("Nothing is scheduled");
});

test("adding says when it first runs and that each run is checked", async () => {
  const { deps, seen } = harness([]);

  const code = await runSchedule({ kind: "add", job: { type: "scan", path: "." }, every: "6h", name: "six-hourly", server: null }, deps);

  expect(code).toBe(SCHEDULE_EXIT.ok);
  expect(seen.calls).toEqual(['create {"name":"six-hourly","every":"6h","job":{"type":"scan","path":"."}}']);
  expect(seen.out[0]).toBe('Scheduled "six-hourly" every 6 hours; first run 2026-11-13 02:00 UTC. Its id is 9f8e7d6c.');
  expect(seen.out[1]).toContain("a run that needs approval waits for you");
});

test("a schedule is named by the beginning of its id; none or several is an error", async () => {
  const stored = [schedule("1a2b3c4d5e6f"), schedule("1a9999999999"), schedule("2b3c4d5e6f70")];

  const paused = harness(stored);
  expect(await runSchedule({ kind: "pause", id: "2b", server: null }, paused.deps)).toBe(SCHEDULE_EXIT.ok);
  expect(paused.seen.calls).toEqual(['update 2b3c4d5e6f70 {"enabled":false}']);
  expect(paused.seen.out).toEqual(['"scan of 2b3c4d5e6f70" is paused.']);

  const ambiguous = harness(stored);
  expect(await runSchedule({ kind: "remove", id: "1a", server: null }, ambiguous.deps)).toBe(SCHEDULE_EXIT.failed);
  expect(ambiguous.seen.calls).toEqual([]);
  expect(ambiguous.seen.log[0]).toContain('"1a" fits 2 schedules');

  const missing = harness(stored);
  expect(await runSchedule({ kind: "run", id: "ff", server: null }, missing.deps)).toBe(SCHEDULE_EXIT.failed);
  expect(missing.seen.log[0]).toContain('No schedule has an id starting with "ff"');
});

test("run now says what became of it; a refusal is a failure", async () => {
  const started = harness([schedule("1a2b3c4d5e6f", { name: "Nightly" })]);
  expect(await runSchedule({ kind: "run", id: "1a2b3c4d5e6f", server: null }, started.deps)).toBe(SCHEDULE_EXIT.ok);
  expect(started.seen.out).toEqual(['"Nightly": Started. (run abcdef01)']);

  const waiting = harness([schedule("1a2b3c4d5e6f", { name: "Nightly" })], {
    runNow: async () => ({ outcome: "waiting", detail: "Waiting for your approval: Run a scan of \".\".", jobId: "abcdef0123456789" }),
  });
  expect(await runSchedule({ kind: "run", id: "1a", server: null }, waiting.deps)).toBe(SCHEDULE_EXIT.ok);
  expect(waiting.seen.out[0]).toContain("Waiting for your approval");

  const refused = harness([schedule("1a2b3c4d5e6f")], {
    runNow: async () => ({ outcome: "refused", detail: "Refused: Unknown catalog 'x'", jobId: null }),
  });
  expect(await runSchedule({ kind: "run", id: "1a", server: null }, refused.deps)).toBe(SCHEDULE_EXIT.failed);
});

test("resume and remove act on the schedule; a server that refuses is reported", async () => {
  const stored = [schedule("1a2b3c4d5e6f", { name: "Nightly", enabled: false })];

  const resumed = harness(stored);
  expect(await runSchedule({ kind: "resume", id: "1a", server: null }, resumed.deps)).toBe(SCHEDULE_EXIT.ok);
  expect(resumed.seen.out).toEqual(['"Nightly" runs again; next 2026-11-13 02:00 UTC.']);

  const removed = harness(stored);
  expect(await runSchedule({ kind: "remove", id: "1a", server: null }, removed.deps)).toBe(SCHEDULE_EXIT.ok);
  expect(removed.seen.calls).toEqual(["remove 1a2b3c4d5e6f"]);

  const failing = harness([], {
    create: async () => {
      throw new Error("You have 20 schedules already. Remove one first.");
    },
  });
  expect(await runSchedule({ kind: "add", job: { type: "scan", path: "." }, every: "1d", name: null, server: null }, failing.deps)).toBe(SCHEDULE_EXIT.failed);
  expect(failing.seen.log).toEqual(["You have 20 schedules already. Remove one first."]);
});
