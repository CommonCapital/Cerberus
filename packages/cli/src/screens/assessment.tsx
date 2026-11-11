import { useCallback, useEffect, useRef, useState } from "react";
import { useNavigate, useParams } from "react-router";
import { TextAttributes, type InputRenderable } from "@opentui/core";
import { useKeyboard } from "@opentui/react";
import { AssessmentView, canAccept, needsActionPlan } from "../components/assessment-view";
import { cerberus, type Assessment as AssessmentModel } from "../lib/cerberus/api";
import { useKeyboardLayer } from "../providers/keyboard-layer";
import { usePromptConfig } from "../providers/prompt-config";
import { useToast } from "../providers/toast";

/**
 * A compliance assessment, worked through: move over its controls, ask a model
 * for evidence, and accept or dismiss what it suggested.
 */
export function Assessment() {
  const { id } = useParams();
  const navigate = useNavigate();
  const { show } = useToast();
  const { isTopLayer } = useKeyboardLayer();
  const { model } = usePromptConfig();
  const [assessment, setAssessment] = useState<AssessmentModel | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [selected, setSelected] = useState(0);
  // Writing the action plan that accepting a non-compliant suggestion requires.
  const [planning, setPlanning] = useState(false);
  const planRef = useRef<InputRenderable>(null);
  const busy = useRef(false);

  const reload = useCallback(async () => {
    if (!id) return;
    setAssessment(await cerberus.assessment(id));
  }, [id]);

  useEffect(() => {
    reload().catch((problem: unknown) => setError(problem instanceof Error ? problem.message : "Could not load the assessment"));
  }, [reload]);

  /** One action at a time; a failure is reported, not thrown at the screen. */
  const act = async (action: () => Promise<unknown>) => {
    if (busy.current) return;
    busy.current = true;
    try {
      await action();
    } catch (problem) {
      show({ variant: "error", message: problem instanceof Error ? problem.message : "That did not work" });
    } finally {
      busy.current = false;
    }
  };

  const current = assessment?.controls[selected];

  useKeyboard((key) => {
    if (!isTopLayer("base")) return;

    if (planning) {
      if (key.name === "escape") setPlanning(false);
      else if ((key.name === "return" || key.name === "enter") && id && current) {
        const actionPlan = (planRef.current?.value ?? "").trim();
        if (!actionPlan) return;
        setPlanning(false);
        void act(async () => {
          await cerberus.acceptSuggestion(id, current.controlId, { actionPlan });
          await reload();
        });
      }
      return;
    }

    if (key.name === "escape") {
      navigate("/");
      return;
    }
    if (!assessment || !id) return;

    if (key.name === "up") setSelected((index) => Math.max(0, index - 1));
    else if (key.name === "down") setSelected((index) => Math.min(assessment.controls.length - 1, index + 1));
    else if (key.name === "e") {
      void act(async () => {
        const job = await cerberus.submitJob({ type: "evidence", assessmentId: id, model });
        navigate(`/runs/${job.id}`);
      });
    } else if (key.name === "a" && current && canAccept(current)) {
      if (needsActionPlan(current)) setPlanning(true);
      else {
        void act(async () => {
          await cerberus.acceptSuggestion(id, current.controlId);
          await reload();
        });
      }
    } else if (key.name === "x" && current?.suggestion) {
      void act(async () => {
        await cerberus.dismissSuggestion(id, current.controlId);
        await reload();
      });
    }
  });

  return (
    <box flexDirection="column" flexGrow={1} width="100%" height="100%" paddingY={1} paddingX={2} gap={1}>
      <scrollbox flexGrow={1} width="100%">
        <AssessmentView assessment={assessment} selected={selected} error={error} />
      </scrollbox>
      {planning ? (
        <box flexShrink={0} flexDirection="column">
          <text attributes={TextAttributes.DIM}>
            Accepting "{current?.suggestion?.suggestion}" for {current?.controlId} needs an action plan:
          </text>
          <input ref={planRef} placeholder="What will be done about it" focused />
        </box>
      ) : null}
      <box flexShrink={0} flexDirection="row" gap={1} height={1}>
        {planning ? (
          <>
            <text>enter</text>
            <text attributes={TextAttributes.DIM}>accept with this plan · esc cancel</text>
          </>
        ) : (
          <>
            <text>esc</text>
            <text attributes={TextAttributes.DIM}>back</text>
            <text>↑↓</text>
            <text attributes={TextAttributes.DIM}>control</text>
            <text>e</text>
            <text attributes={TextAttributes.DIM}>ask {model} for evidence</text>
            {canAccept(current) ? (
              <>
                <text>a</text>
                <text attributes={TextAttributes.DIM}>accept suggestion</text>
              </>
            ) : null}
            {current?.suggestion ? (
              <>
                <text>x</text>
                <text attributes={TextAttributes.DIM}>dismiss it</text>
              </>
            ) : null}
          </>
        )}
      </box>
    </box>
  );
}
