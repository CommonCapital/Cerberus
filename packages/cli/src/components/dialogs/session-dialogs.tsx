import { useEffect, useState } from "react";
import { TextAttributes } from "@opentui/core";
import { useNavigate } from "react-router";
import { useDialog } from "../../providers/dialog";
import { useToast } from "../../providers/toast";
import { cerberus } from "../../lib/cerberus/api";
import { forkPoints, type ForkPoint } from "../../lib/session-housekeeping";
import { DialogInputList } from "../dialog-input-list";
import { DialogSearchList } from "../dialog-search-list";

const message = (problem: unknown, fallback: string) => (problem instanceof Error ? problem.message : fallback);

export type ForkIo = {
  load: (sessionId: string) => Promise<{ messages: unknown[] }>;
  fork: (sessionId: string, options: { through?: string }) => Promise<{ id: string; title: string }>;
};

const realForkIo: ForkIo = {
  load: (sessionId) => cerberus.session(sessionId),
  fork: (sessionId, options) => cerberus.forkSession(sessionId, options),
};

/**
 * "/fork": a copy of the conversation to carry on differently — the whole of
 * it, or up to the reply to an earlier prompt. The original stays as it is;
 * the operator is taken to the copy.
 */
export const ForkDialogContent = ({ sessionId, io = realForkIo }: { sessionId: string; io?: ForkIo }) => {
  const [points, setPoints] = useState<ForkPoint[] | null>(null);
  const { close } = useDialog();
  const navigate = useNavigate();
  const { show } = useToast();

  useEffect(() => {
    let ignore = false;
    io.load(sessionId)
      .then((session) => {
        if (!ignore) setPoints(forkPoints(session.messages as Parameters<typeof forkPoints>[0]));
      })
      .catch((problem: unknown) => {
        if (ignore) return;
        show({ variant: "error", message: message(problem, "Could not load the conversation") });
        close();
      });
    return () => {
      ignore = true;
    };
  }, [sessionId, io, close, show]);

  if (!points) return <text attributes={TextAttributes.DIM}>Loading the conversation...</text>;

  return (
    <DialogSearchList
      items={points}
      onSelect={(point) => {
        io.fork(sessionId, { through: point.through })
          .then((fork) => {
            close();
            navigate(`/sessions/${fork.id}`);
            show({ variant: "info", message: `You are now in the copy, "${fork.title}". The original is unchanged; /sessions has both.` });
          })
          .catch((problem: unknown) => show({ variant: "error", message: message(problem, "Could not fork the conversation") }));
      }}
      filterFn={(point, query) => point.label.toLowerCase().includes(query.toLowerCase())}
      renderItem={(point, isSelected) => (
        <>
          <text selectable={false} fg={isSelected ? "black" : "white"}>
            {point.label}
          </text>
          <box flexGrow={1} />
          <text selectable={false} fg={isSelected ? "black" : undefined} attributes={TextAttributes.DIM}>
            {point.hint}
          </text>
        </>
      )}
      getKey={(point) => point.key}
      placeholder="Fork from where?"
      emptyText="No such point in this conversation"
    />
  );
};

/** "/rename": the operator's own name for the conversation. */
export const RenameDialogContent = ({
  sessionId,
  rename = (id: string, title: string) => cerberus.renameSession(id, title),
}: {
  sessionId: string;
  rename?: (sessionId: string, title: string) => Promise<{ title: string }>;
}) => {
  const { close } = useDialog();
  const { show } = useToast();

  return (
    <DialogInputList
      items={[] as { id: string }[]}
      getKey={(item) => item.id}
      renderItem={() => null}
      onItem={() => {}}
      draftActions={(text) => [{ key: "rename", label: `Rename to "${text}"` }]}
      onDraft={async (_action, text) => {
        try {
          const renamed = await rename(sessionId, text);
          show({ variant: "info", message: `This conversation is now called "${renamed.title}".` });
          close();
        } catch (problem) {
          show({ variant: "error", message: message(problem, "Could not rename the conversation") });
        }
      }}
      placeholder="A new name for this conversation"
      emptyText="Type the name and press Enter. It is what /sessions lists."
      hint=""
    />
  );
};
