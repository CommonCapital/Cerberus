import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useNavigate, useParams } from "react-router";
import { TextAttributes, type InputRenderable } from "@opentui/core";
import { useKeyboard } from "@opentui/react";
import { decisionLabel, FindingsView, orderFindings } from "../components/findings-view";
import { cerberus, type FindingDecision, type ScanDetail } from "../lib/cerberus/api";
import { useKeyboardLayer } from "../providers/keyboard-layer";
import { useToast } from "../providers/toast";

/**
 * The findings of a scan, gone through one by one: accept a risk or mark a
 * false positive — each with the reason — or withdraw a decision. A decision
 * stays with the finding in later scans.
 */
export function Scan() {
  const { id } = useParams();
  const navigate = useNavigate();
  const { show } = useToast();
  const { isTopLayer } = useKeyboardLayer();
  const [scan, setScan] = useState<ScanDetail | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [selected, setSelected] = useState(0);
  // The decision being given a reason; while set, keys go to the text field.
  const [deciding, setDeciding] = useState<FindingDecision["status"] | null>(null);
  const noteRef = useRef<InputRenderable>(null);
  const busy = useRef(false);

  const reload = useCallback(async () => {
    if (!id) return;
    setScan(await cerberus.scan(id));
  }, [id]);

  useEffect(() => {
    reload().catch((problem: unknown) => setError(problem instanceof Error ? problem.message : "Could not load the scan"));
  }, [reload]);

  // The order is fixed when the scan is loaded, so a finding does not jump away the moment it is decided.
  const [order, setOrder] = useState<string[]>([]);
  useEffect(() => {
    if (scan && order.length === 0) setOrder(orderFindings(scan.findings).map((f) => f.id));
  }, [scan, order.length]);
  const findings = useMemo(() => {
    if (!scan) return [];
    const byId = new Map(scan.findings.map((f) => [f.id, f]));
    return order.map((findingId) => byId.get(findingId)).filter((f) => f !== undefined);
  }, [scan, order]);
  const current = findings[selected];

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

    if (deciding) {
      if (key.name === "escape") setDeciding(null);
      else if ((key.name === "return" || key.name === "enter") && id && current) {
        const note = (noteRef.current?.value ?? "").trim();
        if (!note) return;   // a decision without a reason is not recorded
        const status = deciding;
        setDeciding(null);
        void act(async () => {
          await cerberus.decideFinding(id, current.id, status, note);
          await reload();
        });
      }
      return;
    }

    if (key.name === "escape") {
      navigate("/");
      return;
    }
    if (!scan || !current) return;

    if (key.name === "up") setSelected((index) => Math.max(0, index - 1));
    else if (key.name === "down") setSelected((index) => Math.min(findings.length - 1, index + 1));
    else if (key.name === "r") setDeciding("accepted-risk");
    else if (key.name === "p") setDeciding("false-positive");
    else if (key.name === "x" && current.decision) {
      const decisionId = current.decision.id;
      void act(async () => {
        await cerberus.withdrawDecision(decisionId);
        await reload();
      });
    }
  });

  return (
    <box flexDirection="column" flexGrow={1} width="100%" height="100%" paddingY={1} paddingX={2} gap={1}>
      <scrollbox flexGrow={1} width="100%">
        <FindingsView scan={scan} findings={findings} selected={selected} error={error} />
      </scrollbox>
      {deciding ? (
        <box flexShrink={0} flexDirection="column">
          <text attributes={TextAttributes.DIM}>
            Why is "{current?.title}" {deciding === "accepted-risk" ? "a risk you accept" : "a false positive"}?
          </text>
          <input ref={noteRef} placeholder="The reason, for whoever reads this later" focused />
        </box>
      ) : null}
      <box flexShrink={0} flexDirection="row" gap={1} height={1}>
        {deciding ? (
          <>
            <text>enter</text>
            <text attributes={TextAttributes.DIM}>record as {decisionLabel(deciding)} · esc cancel</text>
          </>
        ) : (
          <>
            <text>esc</text>
            <text attributes={TextAttributes.DIM}>back</text>
            {current ? (
              <>
                <text>↑↓</text>
                <text attributes={TextAttributes.DIM}>finding</text>
                <text>r</text>
                <text attributes={TextAttributes.DIM}>accept the risk</text>
                <text>p</text>
                <text attributes={TextAttributes.DIM}>false positive</text>
              </>
            ) : null}
            {current?.decision ? (
              <>
                <text>x</text>
                <text attributes={TextAttributes.DIM}>withdraw the decision</text>
              </>
            ) : null}
          </>
        )}
      </box>
    </box>
  );
}
