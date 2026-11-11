import { useCallback, useEffect, useState } from "react";
import { TextAttributes } from "@opentui/core";
import { useKeyboard } from "@opentui/react";
import { useDialog } from "../../providers/dialog";
import { useKeyboardLayer } from "../../providers/keyboard-layer";
import { useTheme } from "../../providers/theme";
import { useToast } from "../../providers/toast";
import { cerberus, type Engagement, type EngagementTarget } from "../../lib/cerberus/api";
import { setCurrentEngagement, useCurrentEngagement } from "../../lib/cerberus/engagement";
import { DialogInputList } from "../dialog-input-list";

const message = (problem: unknown, fallback: string) => (problem instanceof Error ? problem.message : fallback);

type Choice = { id: string | null; name: string; detail: string };

/**
 * Chooses the engagement the operator works under, or creates one. Jobs
 * started from the CLI then run under it and are checked against its scope.
 */
export const EngagementDialogContent = () => {
  const [engagements, setEngagements] = useState<Engagement[] | null>(null);
  const current = useCurrentEngagement();
  const { close } = useDialog();
  const { show } = useToast();

  useEffect(() => {
    let ignore = false;
    cerberus
      .engagements()
      .then((result) => {
        if (!ignore) setEngagements(result);
      })
      .catch((problem: unknown) => {
        if (ignore) return;
        show({ variant: "error", message: message(problem, "Could not load engagements") });
        close();
      });
    return () => {
      ignore = true;
    };
  }, [close, show]);

  if (!engagements) return <text attributes={TextAttributes.DIM}>Loading engagements...</text>;

  const choices: Choice[] = [
    ...engagements
      .filter((e) => e.status === "Active")
      .map((e) => ({ id: e.id, name: e.name, detail: `${e.targets.length} target${e.targets.length === 1 ? "" : "s"} in scope` })),
    { id: null, name: "No engagement", detail: "runs are not checked against a scope" },
  ];

  return (
    <DialogInputList
      items={choices}
      getKey={(choice) => choice.id ?? "none"}
      renderItem={(choice, isSelected) => (
        <>
          <text selectable={false} fg={isSelected ? "black" : "white"}>
            {(choice.id ?? null) === (current?.id ?? null) ? "✔" : " "} {choice.name}
          </text>
          <box flexGrow={1} />
          <text selectable={false} fg={isSelected ? "black" : undefined} attributes={TextAttributes.DIM}>
            {choice.detail}
          </text>
        </>
      )}
      onItem={(choice) => {
        setCurrentEngagement(choice.id ? { id: choice.id, name: choice.name } : null);
        show({ variant: "info", message: choice.id ? `Working under "${choice.name}"` : "Working without an engagement" });
        close();
      }}
      draftActions={(text) => [{ key: "create", label: `Create engagement "${text}"`, hint: "passive tier" }]}
      onDraft={async (_action, text) => {
        try {
          const created = await cerberus.createEngagement(text);
          setCurrentEngagement({ id: created.id, name: created.name });
          show({ variant: "info", message: `Created "${created.name}". Add what is in scope with /scope.` });
          close();
        } catch (problem) {
          show({ variant: "error", message: message(problem, "Could not create the engagement") });
          throw problem;
        }
      }}
      placeholder="Type a name to create an engagement"
      emptyText="No engagements yet."
      hint="↑↓ select · enter work under it"
    />
  );
};

/** The scope of the current engagement: the directories jobs may run on. Add one by typing its path. */
export const ScopeDialogContent = () => {
  const current = useCurrentEngagement();
  const [targets, setTargets] = useState<EngagementTarget[] | null>(null);
  const { close } = useDialog();
  const { show } = useToast();
  const engagementId = current?.id;

  const reload = useCallback(async () => {
    if (!engagementId) return;
    setTargets((await cerberus.engagement(engagementId)).targets);
  }, [engagementId]);

  useEffect(() => {
    reload().catch((problem: unknown) => {
      show({ variant: "error", message: message(problem, "Could not load the engagement") });
      close();
    });
  }, [reload, close, show]);

  if (!current) return <text attributes={TextAttributes.DIM}>Choose or create an engagement first, with /engagement.</text>;
  if (!targets) return <text attributes={TextAttributes.DIM}>Loading the scope...</text>;

  return (
    <DialogInputList
      items={targets}
      getKey={(target) => target.id}
      renderItem={(target, isSelected) => (
        <>
          <text selectable={false} fg={isSelected ? "black" : "white"}>
            {target.locator || "(the whole scan root)"}
          </text>
          <box flexGrow={1} />
          <text selectable={false} fg={isSelected ? "black" : undefined} attributes={TextAttributes.DIM}>
            {target.type.toLowerCase()} · {target.verification.toLowerCase()}
          </text>
        </>
      )}
      onItem={async (target) => {
        try {
          await cerberus.removeTarget(current.id, target.id);
          await reload();
          show({ variant: "info", message: `Removed from scope: ${target.locator || "the scan root"}` });
        } catch (problem) {
          show({ variant: "error", message: message(problem, "Could not remove that target") });
        }
      }}
      draftActions={(text) => [{ key: "add", label: `Add "${text}" to the scope`, hint: "I may test this code" }]}
      onDraft={async (_action, text) => {
        try {
          await cerberus.addPathTarget(current.id, text);
          await reload();
        } catch (problem) {
          show({ variant: "error", message: message(problem, "Could not add that path") });
          throw problem;
        }
      }}
      placeholder="Path under the scan root to add"
      emptyText={`Nothing is in scope of "${current.name}" yet, so every run under it is refused.`}
      hint="type a path to add · enter removes the highlighted one"
    />
  );
};

/** The kill switch, with one confirmation: stops everything under the current engagement and ends it. */
export const AbortDialogContent = () => {
  const current = useCurrentEngagement();
  const [stopping, setStopping] = useState(false);
  const { close } = useDialog();
  const { isTopLayer } = useKeyboardLayer();
  const { colors } = useTheme();
  const { show } = useToast();

  useKeyboard((key) => {
    if (!isTopLayer("dialog") || !current || stopping) return;
    if (key.name !== "return" && key.name !== "enter") return;

    setStopping(true);
    cerberus
      .abortEngagement(current.id)
      .then(() => {
        setCurrentEngagement(null);
        show({ variant: "info", message: `Aborted "${current.name}": its jobs are stopped and it takes no more work.` });
        close();
      })
      .catch((problem: unknown) => {
        setStopping(false);
        show({ variant: "error", message: message(problem, "Could not abort the engagement") });
      });
  });

  if (!current) return <text attributes={TextAttributes.DIM}>There is no current engagement to abort.</text>;

  return (
    <box flexDirection="column" gap={1}>
      <text>
        Stop every queued and running job under "{current.name}" and end the engagement?
      </text>
      <text attributes={TextAttributes.DIM}>It cannot be reopened; later work needs a new engagement.</text>
      <box flexDirection="row" gap={1}>
        <text fg={colors.error}>enter</text>
        <text attributes={TextAttributes.DIM}>{stopping ? "stopping…" : "abort it"}</text>
        <text>esc</text>
        <text attributes={TextAttributes.DIM}>keep it running</text>
      </box>
    </box>
  );
};
