import { useCallback, useEffect, useState } from "react";
import { TextAttributes } from "@opentui/core";
import { format } from "date-fns";
import { useNavigate } from "react-router";
import { useDialog } from "../../providers/dialog";
import { useToast } from "../../providers/toast";
import { cerberus, type Job } from "../../lib/cerberus/api";
import { DialogSearchList } from "../dialog-search-list";

/** Recent runs; the ones waiting on the operator are marked. Opening one replays it and follows it live. */
export const RunsDialogContent = () => {
  const [jobs, setJobs] = useState<Job[] | null>(null);
  const { close } = useDialog();
  const { show } = useToast();
  const navigate = useNavigate();

  useEffect(() => {
    let ignore = false;
    cerberus
      .jobs()
      .then((result) => {
        if (!ignore) setJobs(result);
      })
      .catch((problem: unknown) => {
        if (ignore) return;
        show({ variant: "error", message: problem instanceof Error ? problem.message : "Could not load runs" });
        close();
      });
    return () => {
      ignore = true;
    };
  }, [close, show]);

  const handleSelect = useCallback(
    (job: Job) => {
      close();
      navigate(`/runs/${job.id}`);
    },
    [close, navigate],
  );

  if (jobs === null) {
    return <text attributes={TextAttributes.DIM}>Loading runs...</text>;
  }

  return (
    <DialogSearchList
      items={jobs}
      onSelect={handleSelect}
      filterFn={(job, query) => `${job.type} ${job.targetPath} ${job.state}`.toLowerCase().includes(query.toLowerCase())}
      renderItem={(job, isSelected) => {
        const waiting = job.state === "AwaitingApproval" || job.state === "AwaitingInput";
        return (
          <>
            <text selectable={false} fg={isSelected ? "black" : "white"}>
              {waiting ? "! " : "  "}
              {job.type} {job.targetPath}
            </text>
            <box flexGrow={1} />
            <text selectable={false} fg={isSelected ? "black" : undefined} attributes={TextAttributes.DIM}>
              {waiting ? "needs you" : job.state.toLowerCase()} · {format(new Date(job.createdAt), "HH:mm")}
            </text>
          </>
        );
      }}
      getKey={(job) => job.id}
      placeholder="Search runs"
      emptyText="No runs yet — start one with /scan"
    />
  );
};
