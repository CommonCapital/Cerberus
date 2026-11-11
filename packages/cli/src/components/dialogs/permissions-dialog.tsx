import { useCallback, useEffect, useState } from "react";
import { TextAttributes } from "@opentui/core";
import { useDialog } from "../../providers/dialog";
import { useToast } from "../../providers/toast";
import { cerberus, type Permissions } from "../../lib/cerberus/api";
import { DialogSearchList } from "../dialog-search-list";

type Row = { key: string; label: string; hint: string; apply: (current: Permissions) => Promise<Permissions> };

/** Shows the permission policy and lets the operator switch mode or stop "always allowing" a kind of action. */
export const PermissionsDialogContent = () => {
  const [permissions, setPermissions] = useState<Permissions | null>(null);
  const { close } = useDialog();
  const { show } = useToast();

  useEffect(() => {
    let ignore = false;
    cerberus
      .permissions()
      .then((result) => {
        if (!ignore) setPermissions(result);
      })
      .catch((problem: unknown) => {
        if (ignore) return;
        show({ variant: "error", message: problem instanceof Error ? problem.message : "Could not load permissions" });
        close();
      });
    return () => {
      ignore = true;
    };
  }, [close, show]);

  const handleSelect = useCallback(
    (row: Row) => {
      if (!permissions) return;
      row
        .apply(permissions)
        .then(setPermissions)
        .catch((problem: unknown) =>
          show({ variant: "error", message: problem instanceof Error ? problem.message : "Could not change permissions" }),
        );
    },
    [permissions, show],
  );

  if (!permissions) {
    return <text attributes={TextAttributes.DIM}>Loading permissions...</text>;
  }

  const asking = permissions.mode === "Ask";
  const rows: Row[] = [
    {
      key: "mode",
      label: asking ? "Mode: ask — everything asks first" : "Mode: default — reading and passive jobs run; changes ask",
      hint: asking ? "switch to default" : "switch to ask",
      apply: () => cerberus.setPermissions({ mode: asking ? "default" : "ask" }),
    },
    ...permissions.kinds
      .filter((entry) => permissions.allowedKinds.includes(entry.kind))
      .map(
        (entry): Row => ({
          key: entry.kind,
          label: `Always allowed: ${entry.description.toLowerCase()}`,
          hint: "ask again",
          apply: (current) =>
            cerberus.setPermissions({ allowedKinds: current.allowedKinds.filter((kind) => kind !== entry.kind) }),
        }),
      ),
  ];

  return (
    <DialogSearchList
      items={rows}
      onSelect={handleSelect}
      filterFn={(row, query) => row.label.toLowerCase().includes(query.toLowerCase())}
      renderItem={(row, isSelected) => (
        <>
          <text selectable={false} fg={isSelected ? "black" : "white"}>
            {row.label}
          </text>
          <box flexGrow={1} />
          <text selectable={false} fg={isSelected ? "black" : undefined} attributes={TextAttributes.DIM}>
            {row.hint}
          </text>
        </>
      )}
      getKey={(row) => row.key}
      placeholder="enter to change"
      emptyText="Nothing matches"
    />
  );
};
