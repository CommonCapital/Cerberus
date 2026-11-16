import { expect, test } from "bun:test";
import type { Approval, Job, Question } from "../lib/cerberus/api";
import { emptyRun, type RunView } from "../lib/cerberus/run-state";
import { parseArgs, type RunOptions } from "./args";
import { EXIT, runHeadless, type HeadlessDeps } from "./run";

const options = (line: string): RunOptions => {
  const parsed = parseArgs(line.split(" "));
  if (parsed.kind !== "run") throw new Error(JSON.stringify(parsed));
  return parsed.options;
};

const job = (state: Job["state"], extra: Partial<Job> = {}): Job => ({
  id: "job-12345678-abcd",
  type: "scan",
  state,
  tier: "passive",
  engagementId: null,
  targetPath: "svc",
  resultId: state === "Succeeded" ? "result-1" : null,
  approvalId: null,
  error: null,
  createdAt: "2026-11-12T10:00:00Z",
  startedAt: null,
  finishedAt: null,
  ...extra,
});

const view = (change: Partial<RunView>): RunView => ({ ...emptyRun(), lastSequence: 1, ...change });

const approval = (kinds: string[]): Approval => ({
  id: "approval-1",
  jobId: "job-12345678-abcd",
  kind: kinds[0]!,
  kinds,
  title: "Send code to a cloud model",
  detail: "Files under svc go to the provider, with secrets masked.",
  status: "Pending",
  note: null,
});

const finding = (severity: string, file: string, line: number, title: string, extra: Record<string, unknown> = {}) => ({
  level: "error",
  message: { text: title },
  locations: [{ physicalLocation: { artifactLocation: { uri: file }, region: { startLine: line } } }],
  ...(extra.suppressions ? { suppressions: extra.suppressions } : {}),
  properties: { severity, ...extra },
});
const sarif = (...results: unknown[]) => ({ version: "2.1.0", runs: [{ results }] });

type Script = (emit: (view: RunView) => void, fail: (error: Error) => void) => void | Promise<void>;

function harness(script: Script, change: Partial<HeadlessDeps> = {}) {
  const seen = {
    out: [] as string[],
    log: [] as string[],
    approved: [] as string[],
    cancelled: [] as string[],
    written: [] as { path: string; content: string }[],
    pictures: [] as { path: string; size: number }[],
    stopped: 0,
    timeout: null as (() => void) | null,
    timeoutCalledOff: false,
    clock: 0,
  };
  const deps: HeadlessDeps = {
    submit: async (wanted) => job("Queued", { type: wanted.type }),
    follow: (_id, onChange, onError) => {
      // Like the real follower: events arrive after the call has returned.
      void Promise.resolve().then(() => script(onChange, (error) => onError?.(error)));
      return { stop: async () => void seen.stopped++ };
    },
    approve: async (id) => void seen.approved.push(id),
    cancel: async (id) => void seen.cancelled.push(id),
    sarif: async () => sarif(),
    report: async () => null,
    write: (path, content) => {
      seen.written.push({ path, content });
      return `/work/${path}`;
    },
    screenshots: async () => [],
    writeBytes: (path, bytes) => {
      seen.pictures.push({ path, size: bytes.length });
      return `/work/${path}`;
    },
    out: (line) => void seen.out.push(line),
    log: (line) => void seen.log.push(line),
    after: (_ms, callback) => {
      seen.timeout = callback;
      return () => void (seen.timeoutCalledOff = true);
    },
    now: () => seen.clock,
    ...change,
  };
  return { deps, seen };
}

const succeed: Script = (emit) => emit(view({ job: job("Succeeded") }));

test("a clean scan passes with exit code 0", async () => {
  const { deps, seen } = harness(succeed, { sarif: async () => sarif(finding("low", "svc/a.py", 4, "Verbose error")) });

  const code = await runHeadless(options("run scan svc"), deps);

  expect(code).toBe(EXIT.ok);
  expect(seen.out).toEqual(["Scan job-1234 finished: 1 finding counted (1 low).", "PASSED: nothing at or above high."]);
  expect(seen.log).toEqual(["[start] Scan job-1234 on svc"]);
  expect(seen.cancelled).toEqual([]);
  expect(seen.stopped).toBe(1);
  expect(seen.timeoutCalledOff).toBe(true);
});

test("findings at or above the threshold fail the build and are listed, most severe first", async () => {
  const { deps, seen } = harness(succeed, {
    sarif: async () =>
      sarif(
        finding("high", "svc/loader.py", 12, "Unsafe deserialization"),
        finding("medium", "svc/hash.py", 3, "Weak hash"),
        finding("critical", "svc/run.py", 7, "Command injection", { source: "model" }),
        finding("high", "svc/old.py", 1, "Accepted", { suppressions: [{ kind: "external" }] }),
        finding("high", "svc/skip.py", 1, "Ignored", { suppressions: [{ kind: "inSource" }] }),
        finding("critical", "tests/t.py", 1, "In a test", { context: "test" }),
      ),
  });

  const code = await runHeadless(options("run scan svc"), deps);

  expect(code).toBe(EXIT.findings);
  expect(seen.out).toEqual([
    "Scan job-1234 finished: 3 findings counted (1 critical, 1 high, 1 medium).",
    "Not counted: 1 marked to be ignored in the code, 1 decided by an operator, 1 in tests or documentation.",
    "1 of the counted is a model's candidate, for a person to check.",
    "Findings at or above high:",
    "  critical  svc/run.py:7  Command injection  [model]",
    "  high      svc/loader.py:12  Unsafe deserialization",
    "FAILED: 2 findings at or above high.",
  ]);
});

test("the threshold and what counts are the caller's choice", async () => {
  const log = sarif(finding("medium", "svc/hash.py", 3, "Weak hash"), finding("high", "tests/t.py", 1, "In a test", { context: "test" }));

  const strict = harness(succeed, { sarif: async () => log });
  expect(await runHeadless(options("run scan svc --fail-on medium"), strict.deps)).toBe(EXIT.findings);

  const withTests = harness(succeed, { sarif: async () => log });
  expect(await runHeadless(options("run scan svc --include-tests"), withTests.deps)).toBe(EXIT.findings);

  const none = harness(succeed, { sarif: async () => log });
  expect(await runHeadless(options("run scan svc --fail-on none --include-tests"), none.deps)).toBe(EXIT.ok);
  expect(none.seen.out.at(-1)).toBe("No gate (--fail-on none).");
});

test("the SARIF log is written as the server gave it, whether the build passes or fails", async () => {
  const log = sarif(finding("critical", "svc/run.py", 7, "Command injection"));
  const { deps, seen } = harness(succeed, { sarif: async () => log });

  const code = await runHeadless(options("run scan svc --sarif out/cerberus.sarif"), deps);

  expect(code).toBe(EXIT.findings);
  expect(seen.written).toHaveLength(1);
  expect(seen.written[0]!.path).toBe("out/cerberus.sarif");
  expect(JSON.parse(seen.written[0]!.content)).toEqual(log);
  expect(seen.out).toContain("SARIF written to /work/out/cerberus.sarif");
});

test("progress is said once per change, on stderr", async () => {
  const { deps, seen } = harness((emit) => {
    const scanning = { id: "scan", title: "Scan the code", status: "running" as const };
    emit(view({ job: job("Running"), steps: [scanning] }));
    emit(view({ job: job("Running"), steps: [scanning], notes: ["The dependency check was skipped."] }));
    emit(
      view({
        job: job("Running"),
        steps: [{ ...scanning, status: "completed" }, { id: "x", title: "Triage", status: "skipped" }],
        notes: ["The dependency check was skipped."],
        tools: [{ id: "t1", name: "readFile", input: { path: "svc/a.py" } }],
      }),
    );
    emit(view({ job: job("Succeeded") }));
  });

  await runHeadless(options("run scan svc"), deps);

  expect(seen.log).toEqual([
    "[start] Scan job-1234 on svc",
    "[step] Scan the code",
    "[note] The dependency check was skipped.",
    "[done] Scan the code",
    "[skip] Triage",
    "[tool] readFile svc/a.py",
  ]);
});

test("--quiet says nothing while it runs; problems are still said", async () => {
  const quiet = harness((emit) => {
    emit(view({ job: job("Running"), steps: [{ id: "s", title: "Scan the code", status: "running" }] }));
    emit(view({ job: job("Failed", { error: "The path is outside the scan root." }) }));
  });

  const code = await runHeadless(options("run scan svc --quiet"), quiet.deps);

  expect(code).toBe(EXIT.notFinished);
  expect(quiet.seen.log).toEqual(["Scan job-1234 failed: The path is outside the scan root."]);
  expect(quiet.seen.out).toEqual([]);
});

test("--json prints one object and nothing else on stdout", async () => {
  const { deps, seen } = harness(succeed, {
    sarif: async () => sarif(finding("high", "svc/loader.py", 12, "Unsafe deserialization"), finding("low", "tests/t.py", 1, "x", { context: "docs" })),
  });

  const code = await runHeadless(options("run scan svc --json --sarif a.sarif"), deps);

  expect(code).toBe(EXIT.findings);
  expect(seen.out).toHaveLength(1);
  expect(JSON.parse(seen.out[0]!)).toEqual({
    job: { id: "job-12345678-abcd", type: "scan", state: "Succeeded", targetPath: "svc", resultId: "result-1" },
    findings: {
      counted: 1,
      bySeverity: { critical: 0, high: 1, medium: 0, low: 0, info: 0 },
      fromModel: 0,
      notCounted: { ignoredInCode: 0, decided: 0, inTestsOrDocs: 1 },
    },
    gate: { failOn: "high", failed: true, findings: 1 },
    files: { sarif: "/work/a.sarif" },
    exitCode: 1,
  });
});

test("an approval nobody gave in advance stops the run, cancels the job and says how to give it", async () => {
  const { deps, seen } = harness((emit) =>
    emit(view({ job: job("AwaitingApproval", { type: "sast" }), approval: approval(["send-code-to-cloud-model"]) })),
  );

  const code = await runHeadless(options("run sast svc --model cloud-model"), deps);

  expect(code).toBe(EXIT.needsPerson);
  expect(seen.approved).toEqual([]);
  expect(seen.cancelled).toEqual(["job-12345678-abcd"]);
  const said = seen.log.join("\n");
  expect(said).toContain("It needs approval: Send code to a cloud model. Files under svc go to the provider, with secrets masked.");
  expect(said).toContain("--approve send-code-to-cloud-model");
  expect(seen.out).toEqual([]);
});

test("an approval named on the command line is given once, and the run goes on", async () => {
  const waiting = view({ job: job("AwaitingApproval", { type: "sast" }), approval: approval(["send-code-to-cloud-model"]) });
  const { deps, seen } = harness(async (emit) => {
    emit(waiting);
    emit({ ...waiting, lastSequence: 2 });   // another event while the approval is on its way
    await Promise.resolve();
    emit(view({ job: job("Succeeded", { type: "sast" }), resolved: ["Approved: Send code to a cloud model"] }));
  });

  const code = await runHeadless(options("run sast svc --model cloud-model --approve send-code-to-cloud-model"), deps);

  expect(code).toBe(EXIT.ok);
  expect(seen.approved).toEqual(["approval-1"]);
  expect(seen.cancelled).toEqual([]);
  expect(seen.log).toContain("[approved] Send code to a cloud model (in advance: --approve send-code-to-cloud-model)");
});

test("a request covering two kinds of action needs both named", async () => {
  const { deps, seen } = harness((emit) =>
    emit(view({ job: job("AwaitingApproval", { type: "review" }), approval: approval(["run-plan", "send-code-to-cloud-model"]) })),
  );

  const code = await runHeadless(options("run review svc --approve run-plan"), deps);

  expect(code).toBe(EXIT.needsPerson);
  expect(seen.approved).toEqual([]);
  expect(seen.log.join("\n")).toContain("--approve run-plan,send-code-to-cloud-model");
});

test("a run that waits for a place says once where it stands, and then runs", async () => {
  const asked: string[] = [];
  const { deps, seen } = harness(
    async (emit) => {
      emit(view({ job: job("Queued") }));
      emit(view({ job: job("Queued"), lastSequence: 2 }));   // still waiting: not said again
      await new Promise((resolve) => setTimeout(resolve, 5));
      emit(view({ job: job("Succeeded"), lastSequence: 3 }));
    },
    { position: async (id) => (asked.push(id), { ahead: 2, others: 1 }) },
  );

  const code = await runHeadless(options("run scan svc"), deps);

  expect(code).toBe(EXIT.ok);
  expect(asked).toEqual(["job-12345678-abcd"]);
  expect(seen.log.filter((line) => line.startsWith("[wait]"))).toEqual([
    "[wait] waiting its turn — 2 of your runs are ahead of it · places are shared with 1 other person",
  ]);
});

test("a question stops the run: nobody is here to answer it", async () => {
  const question: Question = {
    id: "q1", jobId: "job-12345678-abcd", text: "The target ignores 3 files. Honour that?", options: [], multiSelect: false, allowText: false, status: "Pending", answer: null,
  };
  const { deps, seen } = harness((emit) => emit(view({ job: job("AwaitingInput"), question })));

  const code = await runHeadless(options("run scan svc"), deps);

  expect(code).toBe(EXIT.needsPerson);
  expect(seen.cancelled).toEqual(["job-12345678-abcd"]);
  expect(seen.log.join("\n")).toContain("It asks a question nobody is here to answer: The target ignores 3 files. Honour that?");
});

test("a team review that stops at its plan says how to approve plans in advance", async () => {
  const question: Question = {
    id: "q1", jobId: "job-12345678-abcd", text: "The lead agent proposes this plan for \"svc\": 1. auth — … Run it?", options: [], multiSelect: false, allowText: true, status: "Pending", answer: null,
  };
  const { deps, seen } = harness((emit) => emit(view({ job: job("AwaitingInput"), question })));

  const code = await runHeadless(options("run investigate svc --model m"), deps);

  expect(code).toBe(EXIT.needsPerson);
  expect(seen.cancelled).toEqual(["job-12345678-abcd"]);
  expect(seen.log.join("\n")).toContain("To let the lead's plan run without being shown first, add --approve run-plan.");
});

test("a job that fails, is cancelled or runs out of time on the server is exit code 2, with the server's reason", async () => {
  for (const [state, said] of [
    ["Failed", "Scan job-1234 failed: boom"],
    ["Cancelled", "Scan job-1234 cancelled: boom"],
    ["TimedOut", "Scan job-1234 ran out of time on the server: boom"],
  ] as const) {
    const { deps, seen } = harness((emit) => emit(view({ job: job(state, { error: "boom" }) })));
    expect(await runHeadless(options("run scan svc"), deps)).toBe(EXIT.notFinished);
    expect(seen.log.at(-1)).toBe(said);
    expect(seen.cancelled).toEqual([]);   // it is already over
  }
});

test("a job that cannot be started is exit code 2", async () => {
  const { deps, seen } = harness(succeed, {
    submit: async () => {
      throw new Error("Cannot reach the server at http://localhost:3000. Is the backend running?");
    },
  });

  expect(await runHeadless(options("run scan svc"), deps)).toBe(EXIT.notFinished);
  expect(seen.log).toEqual(["Could not start the scan: Cannot reach the server at http://localhost:3000. Is the backend running?"]);
});

test("the run gives up after its time limit and cancels the job", async () => {
  const { deps, seen } = harness((emit) => {
    emit(view({ job: job("Running") }));
    seen.timeout?.();
  });

  const code = await runHeadless(options("run scan svc --timeout 1"), deps);

  expect(code).toBe(EXIT.notFinished);
  expect(seen.cancelled).toEqual(["job-12345678-abcd"]);
  expect(seen.log.at(-1)).toBe("Scan job-1234 was stopped. It did not finish within 1 minute (--timeout).");
});

test("a server that keeps failing ends the run; a hiccup now and then does not", async () => {
  const patient = harness((emit, fail) => {
    for (let i = 0; i < 8; i++) {
      patient.seen.clock += 30_000;   // spread out: never five within a minute
      fail(new Error("Cannot reach the server"));
    }
    emit(view({ job: job("Succeeded") }));
  });
  expect(await runHeadless(options("run scan svc"), patient.deps)).toBe(EXIT.ok);

  const gone = harness((_emit, fail) => {
    for (let i = 0; i < 5; i++) {
      gone.seen.clock += 4_000;
      fail(new Error("Cannot reach the server at http://localhost:3000."));
    }
  });
  expect(await runHeadless(options("run scan svc"), gone.deps)).toBe(EXIT.notFinished);
  expect(gone.seen.log.at(-1)).toBe("Scan job-1234 was stopped. The server stopped answering: Cannot reach the server at http://localhost:3000.");
});

test("a threat model has no gate; its report is written when asked for", async () => {
  const { deps, seen } = harness((emit) => emit(view({ job: job("Succeeded", { type: "threat-model" }) })), {
    report: async () => "# Threat model\n\nOne component.",
    sarif: async () => {
      throw new Error("a threat model has nothing to export");
    },
  });

  const code = await runHeadless(options("run threat-model svc --model m --report out/threats.md"), deps);

  expect(code).toBe(EXIT.ok);
  expect(seen.written).toEqual([{ path: "out/threats.md", content: "# Threat model\n\nOne component.\n" }]);
  expect(seen.out).toEqual(["Threat model job-1234 finished.", "Report written to /work/out/threats.md"]);
});

test("a run without a written report says so instead of writing an empty file", async () => {
  const { deps, seen } = harness((emit) => emit(view({ job: job("Succeeded", { type: "sast" }) })));

  const code = await runHeadless(options("run sast svc --model m --report r.md"), deps);

  expect(code).toBe(EXIT.ok);
  expect(seen.written).toEqual([]);
  expect(seen.log.at(-1)).toBe("This SAST agent run wrote no report, so r.md was not written.");
});

test("a result that cannot be collected does not pass the build", async () => {
  const { deps, seen } = harness(succeed, {
    sarif: async () => {
      throw new Error("Request failed (500)");
    },
  });

  expect(await runHeadless(options("run scan svc"), deps)).toBe(EXIT.notFinished);
  expect(seen.log.at(-1)).toBe("Scan job-1234 finished, but its result could not be collected: Request failed (500)");
});

test("the screenshots a run kept are saved where asked, named by number and page", async () => {
  const { deps, seen } = harness((emit) => emit(view({ job: job("Succeeded", { type: "sast" }) })), {
    screenshots: async (jobId) => {
      expect(jobId).toBe("job-12345678-abcd");
      return [
        { name: "pickle — Python object serialization", source: "https://docs.python.org/3/library/pickle.html", bytes: new Uint8Array([1, 2, 3]) },
        { name: "", source: "https://example.org/", bytes: new Uint8Array([4, 5]) },
      ];
    },
  });

  const code = await runHeadless(options("run sast svc --model m --research --screenshots shots --fail-on none"), deps);

  expect(code).toBe(EXIT.ok);
  expect(seen.pictures).toEqual([
    { path: "shots/01-pickle-python-object-serialization.png", size: 3 },
    { path: "shots/02-page.png", size: 2 },
  ]);
  expect(seen.out).toContain("2 screenshots saved to shots");
  expect(seen.log).toContain("[screenshot] /work/shots/01-pickle-python-object-serialization.png — https://docs.python.org/3/library/pickle.html");
});

test("a run that kept no screenshots says so", async () => {
  const { deps, seen } = harness((emit) => emit(view({ job: job("Succeeded", { type: "sast" }) })));

  await runHeadless(options("run sast svc --model m --screenshots shots --fail-on none"), deps);

  expect(seen.pictures).toEqual([]);
  expect(seen.out).toContain("The run kept no screenshots.");
});
