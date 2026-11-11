import { useCallback, useEffect, useState } from "react";
import { TextAttributes } from "@opentui/core";
import { format } from "date-fns";
import { useNavigate } from "react-router";
import { useDialog } from "../../providers/dialog";
import { useToast } from "../../providers/toast";
import { cerberus, type AssessmentSummary, type Catalog } from "../../lib/cerberus/api";
import { DialogInputList } from "../dialog-input-list";
import { DialogSearchList } from "../dialog-search-list";

const message = (problem: unknown, fallback: string) => (problem instanceof Error ? problem.message : fallback);

/** Starts a compliance assessment: choose the catalog, optionally type the path first (default: the scan root). */
export const AssessDialogContent = () => {
  const [catalogs, setCatalogs] = useState<Catalog[] | null>(null);
  const { close } = useDialog();
  const { show } = useToast();
  const navigate = useNavigate();

  useEffect(() => {
    let ignore = false;
    cerberus
      .catalogs()
      .then((result) => {
        if (!ignore) setCatalogs(result);
      })
      .catch((problem: unknown) => {
        if (ignore) return;
        show({ variant: "error", message: message(problem, "Could not load the catalogs") });
        close();
      });
    return () => {
      ignore = true;
    };
  }, [close, show]);

  const start = useCallback(
    async (catalogId: string, path: string) => {
      try {
        const job = await cerberus.submitJob({ type: "assess", catalogId, path });
        close();
        navigate(`/runs/${job.id}`);
      } catch (problem) {
        show({ variant: "error", message: message(problem, "Could not start the assessment") });
        throw problem;
      }
    },
    [close, navigate, show],
  );

  if (!catalogs) return <text attributes={TextAttributes.DIM}>Loading catalogs...</text>;

  return (
    <DialogInputList
      items={catalogs}
      getKey={(catalog) => catalog.id}
      renderItem={(catalog, isSelected) => (
        <>
          <text selectable={false} fg={isSelected ? "black" : "white"}>
            {catalog.name}
          </text>
          <box flexGrow={1} />
          <text selectable={false} fg={isSelected ? "black" : undefined} attributes={TextAttributes.DIM}>
            {catalog.controlCount} controls
          </text>
        </>
      )}
      onItem={(catalog) => start(catalog.id, ".")}
      draftActions={(text) => catalogs.map((catalog) => ({ key: catalog.id, label: `"${text}" against ${catalog.name}` }))}
      onDraft={(action, text) => start(action.key, text)}
      placeholder="Path under the scan root (default: .)"
      emptyText="No catalogs are installed."
      hint="↑↓ catalog · enter assess"
    />
  );
};

/** Past assessments; opening one shows its controls, suggestions and answers. */
export const AssessmentsDialogContent = () => {
  const [assessments, setAssessments] = useState<AssessmentSummary[] | null>(null);
  const { close } = useDialog();
  const { show } = useToast();
  const navigate = useNavigate();

  useEffect(() => {
    let ignore = false;
    cerberus
      .assessments()
      .then((result) => {
        if (!ignore) setAssessments(result);
      })
      .catch((problem: unknown) => {
        if (ignore) return;
        show({ variant: "error", message: message(problem, "Could not load assessments") });
        close();
      });
    return () => {
      ignore = true;
    };
  }, [close, show]);

  if (!assessments) return <text attributes={TextAttributes.DIM}>Loading assessments...</text>;

  return (
    <DialogSearchList
      items={assessments}
      onSelect={(assessment) => {
        close();
        navigate(`/assessments/${assessment.id}`);
      }}
      filterFn={(a, query) => `${a.catalogId} ${a.targetPath}`.toLowerCase().includes(query.toLowerCase())}
      renderItem={(a, isSelected) => (
        <>
          <text selectable={false} fg={isSelected ? "black" : "white"}>
            {a.catalogId} {a.targetPath}
          </text>
          <box flexGrow={1} />
          <text selectable={false} fg={isSelected ? "black" : undefined} attributes={TextAttributes.DIM}>
            {a.nonCompliant} failing · {a.notAssessed} open · {format(new Date(a.createdAt), "MMM d HH:mm")}
          </text>
        </>
      )}
      getKey={(a) => a.id}
      placeholder="Search assessments"
      emptyText="No assessments yet. Start one with /assess."
    />
  );
};
