import { TextAttributes } from "@opentui/core";
import { useTheme } from "../providers/theme";
import { TASK_MARK, taskProgress, visibleTasks, type ChatTask } from "../lib/task-list";

const MAX_LINES = 7;

/**
 * The agent's task list, kept in view while it works: what is done, what is
 * under way, what is still to come. Renders nothing when there is no list.
 */
export function TaskStrip({ tasks }: { tasks: ChatTask[] | null }) {
  const { colors } = useTheme();
  if (!tasks || tasks.length === 0) return null;

  const { done, total, open } = taskProgress(tasks);
  if (open === 0) {
    return (
      <text attributes={TextAttributes.DIM}>
        <span fg={colors.success}>✔</span> Tasks: all {total} done
      </text>
    );
  }

  const { shown, hidden } = visibleTasks(tasks, MAX_LINES);
  return (
    <box flexDirection="column" flexShrink={0}>
      <text attributes={TextAttributes.BOLD}>
        Tasks {done}/{total}
      </text>
      {shown.map((task) => (
        <box key={task.id} flexDirection="row" gap={1} height={1} overflow="hidden">
          <text
            fg={task.status === "completed" ? colors.success : task.status === "in_progress" ? colors.info : colors.dimSeparator}
          >
            {TASK_MARK[task.status]}
          </text>
          <text attributes={task.status === "in_progress" ? TextAttributes.BOLD : TextAttributes.DIM}>
            {task.content}
            {task.status === "cancelled" ? " (dropped)" : ""}
          </text>
          {task.status === "pending" && task.dependsOn.length > 0 ? (
            <text attributes={TextAttributes.DIM}>after {task.dependsOn.join(", ")}</text>
          ) : null}
        </box>
      ))}
      {hidden > 0 ? <text attributes={TextAttributes.DIM}>  … {hidden} more</text> : null}
    </box>
  );
}
