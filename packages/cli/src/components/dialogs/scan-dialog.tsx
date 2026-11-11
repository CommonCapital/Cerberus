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
 * Asks what to review and submits the review. Its run opens waiting for
 * approval, showing the plan: scan, assess against every catalog, report.
 */
export const ReviewDialogContent = () => <TargetDialog type="review" />;

/**
 * Asks what the model should review and starts the SAST agent with it. With a
 * cloud model the run opens waiting for approval: the code it reads is sent out.
 */
export const AgentReviewDialogContent = ({ model }: { model: string }) => <TargetDialog type="sast" model={model} />;

const HINT = {
  scan: "enter to scan · secrets, weak crypto, injection sinks, vulnerable dependencies",
  review: "enter to plan a review · scan, compliance assessments, report — you approve the plan first",
  sast: "enter to start · the model reads the code (read-only) and reports what it finds; change it with /models",
};

const TargetDialog = ({ type, model }: { type: "scan" | "review" | "sast"; model?: string }) => {
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
      .submitJob(type === "sast" ? { type, path, model: model ?? "" } : { type, path })
      .then((job) => {
        close();
        navigate(`/runs/${job.id}`);
      })
      .catch((problem: unknown) => {
        setStarting(false);
        show({ variant: "error", message: problem instanceof Error ? problem.message : `Could not start the ${type === "sast" ? "code review" : type}` });
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
