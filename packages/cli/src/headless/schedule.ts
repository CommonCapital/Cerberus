import type { SubmitJob } from "../lib/cerberus/api";
import { parseArgs } from "./args";

// `nightcode schedule …`: recurring runs from the command line. A schedule is
// a job — written exactly as for `nightcode run` — and how often to start it.
// Its runs go through the server's ordinary checks each time, so nothing
// about approval is said here: what a run may do without asking is what the
// operator's permission settings say.

export type Schedule = {
  id: string;
  name: string;
  type: string;
  targetPath: string;
  everyMinutes: number;
  every: string;
  enabled: boolean;
  nextRunAt: string;
  lastRunAt: string | null;
  lastOutcome: string | null;
  lastJobId: string | null;
};

export type ScheduleCommand =
  | { kind: "help" }
  | { kind: "usage"; message: string }
  | { kind: "add"; job: SubmitJob; every: string; name: string | null; server: string | null }
  | { kind: "list"; server: string | null }
  | { kind: "remove" | "run" | "pause" | "resume"; id: string; server: string | null };

export const SCHEDULE_USAGE = `Usage: nightcode schedule <add|list|run|pause|resume|remove> …

  schedule add <scan|review|sast|threat-model|index> [path] --every <interval> [--name <name>] [job options]
      Start this job again and again. The interval is like 45m, 6h, 1d or 1w
      (at least 15 minutes). Job options are those of "nightcode run".
  schedule list                 what is scheduled, when it runs next, how the last run went
  schedule run <id>             start it now, without changing its rhythm
  schedule pause <id>           stop it running until resumed
  schedule resume <id>
  schedule remove <id>

An id may be shortened to its first characters. --server <url> names the server.

A scheduled run is checked by the server like one you start yourself. If your
permission settings say a run must be approved, it waits for you — and the
schedule does not start another while it waits. To let it run unattended,
choose "always allow" for that kind of action (the approval prompt, or /permissions).

Exit codes: 0 done, 2 the server refused or could not be reached, 64 the command line is wrong`;

/** Options of "run" that are about one run's result or about approving in advance: none of that applies to a schedule. */
const NOT_FOR_SCHEDULES = ["--sarif", "--report", "--json", "--quiet", "--fail-on", "--include-tests", "--timeout", "--approve"];

const usage = (message: string): ScheduleCommand => ({ kind: "usage", message });

/** Takes `--name value` / `--name=value` out of the arguments. */
function take(args: string[], name: string): { value: string | null; rest: string[]; problem?: string } {
  const rest: string[] = [];
  let value: string | null = null;
  for (let i = 0; i < args.length; i++) {
    const arg = args[i]!;
    if (arg === name || arg.startsWith(`${name}=`)) {
      if (value !== null) return { value, rest, problem: `${name} is given twice.` };
      const given = arg === name ? args[++i] : arg.slice(name.length + 1);
      if (given === undefined || given === "" || (arg === name && given.startsWith("--"))) return { value, rest, problem: `${name} needs a value.` };
      value = given;
    } else {
      rest.push(arg);
    }
  }
  return { value, rest };
}

/** Parses what follows the program name: `schedule add scan . --every 1d`. */
export function parseSchedule(argv: string[]): ScheduleCommand {
  const [, action, ...given] = argv;
  if (!action || action === "help" || action === "--help" || action === "-h" || given.includes("--help") || given.includes("-h")) return { kind: "help" };

  const server = take(given, "--server");
  if (server.problem) return usage(server.problem);
  const args = server.rest;

  if (action === "list") {
    return args.length === 0 ? { kind: "list", server: server.value } : usage(`"schedule list" takes no arguments.`);
  }
  if (action === "remove" || action === "run" || action === "pause" || action === "resume") {
    if (args.length !== 1 || args[0]!.startsWith("-")) return usage(`Say which schedule: nightcode schedule ${action} <id> (see "schedule list").`);
    return { kind: action, id: args[0]!, server: server.value };
  }
  if (action !== "add") return usage(`"${action}" is not something schedule can do. Choose one of: add, list, run, pause, resume, remove.`);

  const every = take(args, "--every");
  if (every.problem) return usage(every.problem);
  const name = take(every.rest, "--name");
  if (name.problem) return usage(name.problem);
  if (every.value === null) return usage("Say how often: --every 6h (or 45m, 1d, 1w).");
  if (!/^\d+[mhdw]?$/i.test(every.value)) return usage(`"${every.value}" is not an interval. Write it like 45m, 6h, 1d or 1w.`);

  const misplaced = name.rest.find((arg) => NOT_FOR_SCHEDULES.includes(arg.split("=")[0]!));
  if (misplaced) {
    const flag = misplaced.split("=")[0]!;
    return usage(
      flag === "--approve"
        ? "--approve does not apply to a schedule: its runs follow your permission settings. Choose \"always allow\" for that kind of action instead."
        : `${flag} is about one run's result and does not apply to a schedule.`,
    );
  }

  // The job itself is written, and checked, exactly as for "run".
  const job = parseArgs(["run", ...name.rest]);
  if (job.kind === "usage") return usage(job.message);
  if (job.kind !== "run") return { kind: "help" };
  return { kind: "add", job: job.options.job, every: every.value.toLowerCase(), name: name.value, server: server.value };
}

export type ScheduleDeps = {
  list: () => Promise<Schedule[]>;
  create: (request: { name: string | null; every: string; job: SubmitJob }) => Promise<Schedule>;
  update: (id: string, change: { enabled: boolean }) => Promise<Schedule>;
  remove: (id: string) => Promise<unknown>;
  runNow: (id: string) => Promise<{ outcome: string; detail: string; jobId: string | null }>;
  out: (line: string) => void;
  log: (line: string) => void;
};

const when = (iso: string | null) => (iso ? `${iso.slice(0, 10)} ${iso.slice(11, 16)}` : "—");

/** The table "schedule list" prints. Times are UTC. */
export function scheduleTable(schedules: Schedule[]): string[] {
  if (schedules.length === 0) return ["Nothing is scheduled. Add one with: nightcode schedule add scan . --every 1d"];
  const rows = schedules.map((s) => [
    s.id.slice(0, 8),
    s.name,
    s.every,
    s.enabled ? when(s.nextRunAt) : "paused",
    s.lastOutcome ?? "has not run yet",
  ]);
  const head = ["ID", "NAME", "EVERY", "NEXT RUN (UTC)", "LAST"];
  const widths = head.map((title, i) => Math.max(title.length, ...rows.map((row) => row[i]!.length)));
  const line = (cells: string[]) => cells.map((cell, i) => (i === cells.length - 1 ? cell : cell.padEnd(widths[i]!))).join("  ");
  return [line(head), ...rows.map(line)];
}

/** The schedule an id, or the beginning of one, names. Throws with a message when none or several do. */
function resolve(schedules: Schedule[], id: string): Schedule {
  const matches = schedules.filter((s) => s.id === id || s.id.startsWith(id));
  if (matches.length === 1) return matches[0]!;
  throw new Error(matches.length === 0 ? `No schedule has an id starting with "${id}". See "nightcode schedule list".` : `"${id}" fits ${matches.length} schedules; give more of the id.`);
}

export const SCHEDULE_EXIT = { ok: 0, failed: 2, usage: 64 } as const;

/** Carries out a parsed schedule command and returns the exit code. */
export async function runSchedule(command: Exclude<ScheduleCommand, { kind: "help" | "usage" }>, deps: ScheduleDeps): Promise<number> {
  try {
    switch (command.kind) {
      case "list":
        for (const line of scheduleTable(await deps.list())) deps.out(line);
        return SCHEDULE_EXIT.ok;
      case "add": {
        const made = await deps.create({ name: command.name, every: command.every, job: command.job });
        deps.out(`Scheduled "${made.name}" ${made.every}; first run ${when(made.nextRunAt)} UTC. Its id is ${made.id.slice(0, 8)}.`);
        deps.out("Each run is checked like one you start yourself; a run that needs approval waits for you.");
        return SCHEDULE_EXIT.ok;
      }
      case "run": {
        const schedule = resolve(await deps.list(), command.id);
        const fired = await deps.runNow(schedule.id);
        deps.out(`"${schedule.name}": ${fired.detail}${fired.jobId ? ` (run ${fired.jobId.slice(0, 8)})` : ""}`);
        return fired.outcome === "refused" ? SCHEDULE_EXIT.failed : SCHEDULE_EXIT.ok;
      }
      case "pause":
      case "resume": {
        const schedule = resolve(await deps.list(), command.id);
        const changed = await deps.update(schedule.id, { enabled: command.kind === "resume" });
        deps.out(changed.enabled ? `"${changed.name}" runs again; next ${when(changed.nextRunAt)} UTC.` : `"${changed.name}" is paused.`);
        return SCHEDULE_EXIT.ok;
      }
      case "remove": {
        const schedule = resolve(await deps.list(), command.id);
        await deps.remove(schedule.id);
        deps.out(`Removed "${schedule.name}".`);
        return SCHEDULE_EXIT.ok;
      }
    }
  } catch (problem) {
    deps.log(problem instanceof Error ? problem.message : String(problem));
    return SCHEDULE_EXIT.failed;
  }
}
