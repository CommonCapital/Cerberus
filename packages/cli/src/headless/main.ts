import { mkdirSync, writeFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { getAuth } from "../lib/auth";
import { ApiError, baseUrl, cerberus, type Job } from "../lib/cerberus/api";
import { followRun } from "../lib/cerberus/follow";
import { parseArgs, USAGE } from "./args";
import { parseSchedule, runSchedule, SCHEDULE_EXIT, SCHEDULE_USAGE } from "./schedule";
import { KEY_EXIT, KEY_USAGE, parseKey, readSecretFromTerminal, runKey } from "./keys";
import { EXIT, runHeadless, type HeadlessDeps } from "./run";

// `cerberus run …`: the real network, disk and clock behind run.ts.

async function reportOf(job: Job): Promise<string | null> {
  if (!job.resultId) return null;
  if (job.type === "review") return (await cerberus.review(job.resultId)).report;
  if (job.type === "sast" || job.type === "threat-model") return (await cerberus.agentRun(job.resultId)).report;
  return null;
}

const deps: HeadlessDeps = {
  submit: async (job) => {
    try {
      return await cerberus.submitJob(job);
    } catch (problem) {
      // The messages of the interactive CLI point at slash commands; here there are none.
      if (problem instanceof ApiError && problem.status === 401) {
        throw new Error("The server did not accept the sign-in. Put a current token in CERBERUS_TOKEN, or sign in with /login in the interactive CLI.");
      }
      if (problem instanceof ApiError && problem.message.includes("/engagement")) {
        throw new Error("This server only runs work under an engagement. Name one with --engagement <id>.");
      }
      throw problem;
    }
  },
  follow: followRun,
  approve: (id, note) => cerberus.approve(id, { note }),
  cancel: (id) => cerberus.cancelJob(id),
  sarif: (source) => cerberus.sarif(source.kind, source.id),
  report: reportOf,
  screenshots: async (jobId) => {
    const kept = await cerberus.artifacts(jobId);
    return Promise.all(kept.map(async (artifact) => ({ name: artifact.name, source: artifact.source, bytes: await cerberus.artifactBytes(artifact.id) })));
  },
  writeBytes: (path, bytes) => {
    const full = resolve(process.cwd(), path);
    mkdirSync(dirname(full), { recursive: true });
    writeFileSync(full, bytes);
    return full;
  },
  write: (path, content) => {
    const full = resolve(process.cwd(), path);
    mkdirSync(dirname(full), { recursive: true });
    writeFileSync(full, content);
    return full;
  },
  out: (line) => process.stdout.write(`${line}\n`),
  log: (line) => process.stderr.write(`${line}\n`),
  after: (ms, callback) => {
    const timer = setTimeout(callback, ms);
    return () => clearTimeout(timer);
  },
  now: () => Date.now(),
};

/** Runs the command line (without the program name) and returns the exit code. */
export async function main(argv: string[]): Promise<number> {
  if (argv[0] === "schedule") return scheduleMain(argv);
  if (argv[0] === "key" || argv[0] === "models") return keyMain(argv);
  const parsed = parseArgs(argv);
  if (parsed.kind === "help") {
    process.stdout.write(`${USAGE}\n`);
    return EXIT.ok;
  }
  if (parsed.kind === "usage") {
    process.stderr.write(`${parsed.message}\nSee: cerberus run --help\n`);
    return EXIT.usage;
  }

  if (parsed.options.server) process.env.API_URL = parsed.options.server;
  if (!getAuth()) {
    process.stderr.write(`Not signed in to ${baseUrl()}. Put a token in CERBERUS_TOKEN, or sign in with /login in the interactive CLI.\n`);
    return EXIT.notFinished;
  }
  return runHeadless(parsed.options, deps);
}

/** `cerberus schedule …` */
async function scheduleMain(argv: string[]): Promise<number> {
  const command = parseSchedule(argv);
  if (command.kind === "help") {
    process.stdout.write(`${SCHEDULE_USAGE}\n`);
    return SCHEDULE_EXIT.ok;
  }
  if (command.kind === "usage") {
    process.stderr.write(`${command.message}\nSee: cerberus schedule --help\n`);
    return SCHEDULE_EXIT.usage;
  }
  if (command.server) process.env.API_URL = command.server;
  if (!getAuth()) {
    process.stderr.write(`Not signed in to ${baseUrl()}. Put a token in CERBERUS_TOKEN, or sign in with /login in the interactive CLI.\n`);
    return SCHEDULE_EXIT.failed;
  }
  return runSchedule(command, {
    list: cerberus.schedules,
    create: cerberus.createSchedule,
    update: cerberus.updateSchedule,
    remove: cerberus.deleteSchedule,
    runNow: cerberus.runSchedule,
    out: (line) => process.stdout.write(`${line}\n`),
    log: (line) => process.stderr.write(`${line}\n`),
  });
}

/** `cerberus key …` and `cerberus models` */
async function keyMain(argv: string[]): Promise<number> {
  const command = parseKey(argv);
  if (command.kind === "help") {
    process.stdout.write(`${KEY_USAGE}\n`);
    return KEY_EXIT.ok;
  }
  if (command.kind === "usage") {
    process.stderr.write(`${command.message}\nSee: cerberus key --help\n`);
    return KEY_EXIT.usage;
  }
  if (command.server) process.env.API_URL = command.server;
  if (!getAuth()) {
    process.stderr.write(`Not signed in to ${baseUrl()}. Put a token in CERBERUS_TOKEN, or sign in with /login in the interactive CLI.\n`);
    return KEY_EXIT.failed;
  }
  return runKey(command, {
    list: cerberus.apiKeys,
    set: cerberus.setApiKey,
    remove: cerberus.removeApiKey,
    models: cerberus.models,
    env: (name) => process.env[name],
    readSecret: readSecretFromTerminal,
    out: (line) => process.stdout.write(`${line}\n`),
    log: (line) => process.stderr.write(`${line}\n`),
  });
}
