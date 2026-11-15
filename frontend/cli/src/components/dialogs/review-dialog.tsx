import { useCallback } from "react";
import { TextAttributes } from "@opentui/core";
import { useNavigate } from "react-router";
import { useDialog } from "../../providers/dialog";
import { useToast } from "../../providers/toast";
import { cerberus } from "../../lib/cerberus/api";
import { DialogInputList, type DraftAction } from "../dialog-input-list";

type Depth = { key: "rules" | "full"; label: string; hint: string };

/**
 * Plans a security review: of the whole scan root, or of a path typed first.
 * Either with the rules alone, or also with the current model's agents. The
 * run opens waiting for approval of the plan.
 */
export const ReviewDialogContent = ({ model }: { model: string }) => {
  const { close } = useDialog();
  const { show } = useToast();
  const navigate = useNavigate();

  const depths: Depth[] = [
    { key: "rules", label: "Rules only", hint: "scan, compliance, report" },
    { key: "full", label: `With ${model}`, hint: "+ code review, threat model, evidence" },
  ];

  const start = useCallback(
    async (depth: string, path: string) => {
      try {
        const job = await cerberus.submitJob(
          depth === "full"
            ? { type: "review", path, agentModel: model, agents: ["sast", "threat-model", "evidence"] }
            : { type: "review", path },
        );
        close();
        navigate(`/runs/${job.id}`);
      } catch (problem) {
        show({ variant: "error", message: problem instanceof Error ? problem.message : "Could not start the review" });
        throw problem;
      }
    },
    [close, model, navigate, show],
  );

  return (
    <DialogInputList
      items={depths}
      getKey={(depth) => depth.key}
      renderItem={(depth, isSelected) => (
        <>
          <text selectable={false} fg={isSelected ? "black" : "white"}>
            {depth.label}
          </text>
          <box flexGrow={1} />
          <text selectable={false} fg={isSelected ? "black" : undefined} attributes={TextAttributes.DIM}>
            {depth.hint}
          </text>
        </>
      )}
      onItem={(depth) => start(depth.key, ".")}
      draftActions={(text): DraftAction[] => depths.map((depth) => ({ key: depth.key, label: `"${text}" — ${depth.label.toLowerCase()}` }))}
      onDraft={(action, text) => start(action.key, text)}
      placeholder="Path under the scan root (default: .)"
      emptyText=""
      hint="↑↓ choose · enter plan the review — you approve the plan first"
    />
  );
};
