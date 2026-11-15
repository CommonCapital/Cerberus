import { useRef, useState } from "react";
import { TextAttributes, type InputRenderable } from "@opentui/core";
import { useKeyboard } from "@opentui/react";
import { useNavigate } from "react-router";
import { useDialog } from "../../providers/dialog";
import { useKeyboardLayer } from "../../providers/keyboard-layer";
import { useToast } from "../../providers/toast";
import { cerberus } from "../../lib/cerberus/api";

/** Asks what to scan, starts the scan as a background job, and opens its live run. */
export const ScanDialogContent = () => <TargetDialog type="scan" />;

/**
 * Asks what the model should review and starts the SAST agent with it. With a
 * cloud model the run opens waiting for approval: the code it reads is sent out.
 */
export const AgentReviewDialogContent = ({ model }: { model: string }) => <TargetDialog type="sast" model={model} />;

/** Asks which code the model should build a threat model of, and starts the threat-model agent with it. */
export const ThreatModelDialogContent = ({ model }: { model: string }) => <TargetDialog type="threat-model" model={model} />;

/**
 * Asks which code a team should review, and starts the lead agent with it. The lead's plan of
 * specialists comes back as a question on the run: approve it, change it or refuse it there.
 */
export const TeamReviewDialogContent = ({ model }: { model: string }) => <TargetDialog type="investigate" model={model} />;

/** Asks which code to index for search by meaning, and starts the indexing job. */
export const IndexDialogContent = () => <TargetDialog type="index" />;

const HINT = {
  scan: "enter to scan · secrets, weak crypto, injection sinks, vulnerable dependencies",
  sast: "enter to start · the model reads the code (read-only) and reports what it finds; change it with /models",
  "threat-model": "enter to start · the model maps components and threats (STRIDE) and writes them up; change it with /models",
  investigate: "enter to start · a lead agent proposes which specialists to send; you approve the plan, they review, findings are merged",
  index: "enter to index · a local embedding model reads the code so agents can search it by meaning; nothing leaves this machine",
};

const LABEL = { scan: "scan", sast: "code review", "threat-model": "threat model", investigate: "team review", index: "indexing" };

const TargetDialog = ({ type, model }: { type: "scan" | "sast" | "threat-model" | "investigate" | "index"; model?: string }) => {
  const inputRef = useRef<InputRenderable>(null);
  const [starting, setStarting] = useState(false);
  const { close } = useDialog();
  const { isTopLayer } = useKeyboardLayer();
  const { show } = useToast();
  const navigate = useNavigate();

  useKeyboard((key) => {
    if (!isTopLayer("dialog") || starting) return;
    if (key.name !== "return" && key.name !== "enter") return;

    const path = (inputRef.current?.value ?? "").trim() || ".";
    setStarting(true);
    cerberus
      // Started from this screen, somebody is here: the agent may ask them a question while it works.
      .submitJob(type === "sast" || type === "threat-model" || type === "investigate" ? { type, path, model: model ?? "", questions: true } : { type, path })
      .then((job) => {
        close();
        navigate(`/runs/${job.id}`);
      })
      .catch((problem: unknown) => {
        setStarting(false);
        show({ variant: "error", message: problem instanceof Error ? problem.message : `Could not start the ${LABEL[type]}` });
      });
  });

  return (
    <box flexDirection="column" gap={1}>
      <input ref={inputRef} placeholder="Path under the scan root (default: .)" focused />
      <text attributes={TextAttributes.DIM}>
        {starting ? "Starting…" : HINT[type]}
      </text>
    </box>
  );
};
