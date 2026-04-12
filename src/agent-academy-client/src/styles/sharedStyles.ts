import { makeStyles, shorthands } from "@fluentui/react-components";

export const useSharedStyles = makeStyles({
  taskCard: {
    border: "1px solid var(--aa-border)",
    backgroundColor: "var(--aa-panel)",
    ...shorthands.borderRadius("8px"),
    ...shorthands.padding("14px"),
  },
  taskCardTitle: { marginBottom: "8px", fontSize: "13px", fontWeight: 600 },
  form: { display: "grid", gap: "10px" },
  fieldInput: { width: "100%" },
});
