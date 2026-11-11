import { useEffect, useRef, useState } from "react";
import { useNavigate, useParams } from "react-router";
import { TextAttributes, type InputRenderable } from "@opentui/core";
import { useKeyboard } from "@opentui/react";
import { RunView } from "../components/run-view";
import { cerberus } from "../lib/cerberus/api";
import { followRun } from "../lib/cerberus/follow";
import { canBeSteered, emptyRun, isFinished, type RunView as RunViewModel } from "../lib/cerberus/run-state";
import { useKeyboardLayer } from "../providers/keyboard-layer";
import { useToast } from "../providers/toast";

/** A run, followed live: watch it work, approve or deny, answer its questions, cancel it. */
export function Run() {
  const { id } = useParams();
  const navigate = useNavigate();
  const { show } = useToast();
  const { isTopLayer } = useKeyboardLayer();
  const [view, setView] = useState<RunViewModel>(emptyRun);
  const [error, setError] = useState<string | null>(null);
  const [selectedOption, setSelectedOption] = useState(0);
  const [report, setReport] = useState<string | null>(null);
  // Writing a message to the running agent; while true, keys go to the text field.
  const [composing, setComposing] = useState(false);
  const messageRef = useRef<InputRenderable>(null);
  const busy = useRef(false);

  useEffect(() => {
    if (!id) return;
    const follower = followRun(id, setView, (problem) => setError(problem.message));
    return () => {
      void follower.stop();
    };
  }, [id]);

  // A review that succeeded has a report to show.
  const reviewId = view.job?.type === "review" && view.job.state === "Succeeded" ? view.job.resultId : null;
  useEffect(() => {
    if (!reviewId) return;
    let current = true;
    cerberus
      .review(reviewId)
      .then((review) => {
        if (current) setReport(review.report);
      })
      .catch((problem: unknown) => {
        if (current) setError(problem instanceof Error ? problem.message : "Could not load the report");
      });
    return () => {
      current = false;
    };
  }, [reviewId]);

  // A finished assessment or evidence run leads to the assessment it produced or worked on.
  const assessmentId =
    view.job && view.job.state === "Succeeded" && (view.job.type === "assess" || view.job.type === "evidence")
      ? view.job.resultId
      : null;

  // A new question starts with its first option highlighted.
  const questionId = view.question?.id;
  useEffect(() => setSelectedOption(0), [questionId]);

  /** Runs one operator action at a time and reports a failure instead of crashing the screen. */
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

  useKeyboard((key) => {
    if (!isTopLayer("base")) return;

    if (composing) {
      if (key.name === "escape") setComposing(false);
      else if (key.name === "return" || key.name === "enter") {
        const text = (messageRef.current?.value ?? "").trim();
        setComposing(false);
        if (text && id) void act(() => cerberus.sendMessage(id, text));
      }
      return;
    }

    if (key.name === "escape") {
      navigate("/");
      return;
    }

    const { approval, question } = view;
    if (approval) {
      if (key.name === "a") void act(() => cerberus.approve(approval.id));
      else if (key.name === "r") void act(() => cerberus.approve(approval.id, { remember: true }));
      else if (key.name === "d") void act(() => cerberus.deny(approval.id));
      return;
    }

    if (question && question.options.length > 0) {
      if (key.name === "up") setSelectedOption((index) => Math.max(0, index - 1));
      else if (key.name === "down") setSelectedOption((index) => Math.min(question.options.length - 1, index + 1));
      else if (key.name === "return" || key.name === "enter") {
        const option = question.options[selectedOption];
        if (option) void act(() => cerberus.answer(question.id, [option.label]));
      }
      return;
    }

    if (key.name === "o" && assessmentId) {
      navigate(`/assessments/${assessmentId}`);
      return;
    }

    if (key.name === "m" && canBeSteered(view)) {
      setComposing(true);
      return;
    }

    if (key.name === "c" && id && view.job && !isFinished(view)) {
      void act(() => cerberus.cancelJob(id));
    }
  });

  const waiting = view.approval !== null || view.question !== null;

  return (
    <box flexDirection="column" flexGrow={1} width="100%" height="100%" paddingY={1} paddingX={2} gap={1}>
      <scrollbox flexGrow={1} width="100%" stickyScroll stickyStart="bottom">
        <RunView view={view} selectedOption={selectedOption} error={error} report={report} />
      </scrollbox>
      {composing ? (
        <box flexShrink={0} flexDirection="column">
          <input ref={messageRef} placeholder="Tell the agent what to do next" focused />
        </box>
      ) : null}
      <box flexShrink={0} flexDirection="row" gap={1} height={1}>
        <text>{composing ? "enter" : "esc"}</text>
        <text attributes={TextAttributes.DIM}>{composing ? "send · esc discard" : "back"}</text>
        {!composing && assessmentId ? (
          <>
            <text>o</text>
            <text attributes={TextAttributes.DIM}>open the assessment</text>
          </>
        ) : null}
        {!composing && canBeSteered(view) && !waiting ? (
          <>
            <text>m</text>
            <text attributes={TextAttributes.DIM}>message the agent</text>
          </>
        ) : null}
        {view.job && !isFinished(view) && !waiting && !composing ? (
          <>
            <text>c</text>
            <text attributes={TextAttributes.DIM}>cancel run</text>
          </>
        ) : null}
      </box>
    </box>
  );
}
