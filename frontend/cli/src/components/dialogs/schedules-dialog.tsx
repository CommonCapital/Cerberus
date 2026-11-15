import { useCallback, useEffect, useState } from "react";
import { TextAttributes } from "@opentui/core";
import { useDialog } from "../../providers/dialog";
import { useToast } from "../../providers/toast";
import { cerberus, type ScheduleInfo } from "../../lib/cerberus/api";
import { DialogSearchList } from "../dialog-search-list";

const message = (problem: unknown, fallback: string) => (problem instanceof Error ? problem.message : fallback);

export type SchedulesIo = {
  list: () => Promise<ScheduleInfo[]>;
  setEnabled: (id: string, enabled: boolean) => Promise<ScheduleInfo>;
};

const realIo: SchedulesIo = {
  list: () => cerberus.schedules(),
  setEnabled: (id, enabled) => cerberus.updateSchedule(id, { enabled }),
};

/**
 * What matters about a schedule right now, short enough for a row of the dialog: that it waits for the operator,
 * that it is paused (and whether the server paused it), or else its rhythm and next run. Times are UTC, as the server keeps them.
 */
export function describeSchedule(schedule: ScheduleInfo): string {
  const last = schedule.lastOutcome ?? "";
  if (!schedule.enabled) return last.startsWith("Paused after") ? "paused: it kept being refused" : "paused";
  if (last.startsWith("Waiting for your approval")) return "waiting for your approval";
  if (last.startsWith("Refused")) return "last run was refused";
  return `${schedule.every} · next ${schedule.nextRunAt.slice(5, 10)} ${schedule.nextRunAt.slice(11, 16)}`;
}

/**
 * "/schedules": the recurring runs. Enter pauses the highlighted one, or sets
 * it running again. They are made from the command line
 * (`cerberus schedule add …`), where a job is written as for `cerberus run`.
 */
export const SchedulesDialogContent = ({ io = realIo }: { io?: SchedulesIo }) => {
  const [schedules, setSchedules] = useState<ScheduleInfo[] | null>(null);
  const { close } = useDialog();
  const { show } = useToast();

  const reload = useCallback(async () => setSchedules(await io.list()), [io]);

  useEffect(() => {
    reload().catch((problem: unknown) => {
      show({ variant: "error", message: message(problem, "Could not load the schedules") });
      close();
    });
  }, [reload, close, show]);

  if (!schedules) return <text attributes={TextAttributes.DIM}>Loading schedules...</text>;
  if (schedules.length === 0) {
    return <text attributes={TextAttributes.DIM}>Nothing is scheduled. Add one from a terminal: cerberus schedule add scan . --every 1d</text>;
  }

  return (
    <DialogSearchList
      items={schedules}
      onSelect={(schedule) => {
        io.setEnabled(schedule.id, !schedule.enabled)
          .then(async (changed) => {
            await reload();
            show({ variant: "info", message: changed.enabled ? `"${changed.name}" runs again.` : `"${changed.name}" is paused.` });
          })
          .catch((problem: unknown) => show({ variant: "error", message: message(problem, "Could not change that schedule") }));
      }}
      filterFn={(schedule, query) => `${schedule.name} ${schedule.type} ${schedule.targetPath}`.toLowerCase().includes(query.toLowerCase())}
      renderItem={(schedule, isSelected) => (
        <>
          <text selectable={false} fg={isSelected ? "black" : "white"}>
            {schedule.name}
          </text>
          <box flexGrow={1} />
          <text selectable={false} fg={isSelected ? "black" : undefined} attributes={TextAttributes.DIM}>
            {describeSchedule(schedule)}
          </text>
        </>
      )}
      getKey={(schedule) => schedule.id}
      placeholder="Enter pauses or resumes the highlighted schedule"
      emptyText="No such schedule"
    />
  );
};
