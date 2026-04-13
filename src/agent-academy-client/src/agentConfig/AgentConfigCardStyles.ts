import { makeStyles, shorthands } from "@fluentui/react-components";

export const useAgentConfigCardStyles = makeStyles({
  card: {
    ...shorthands.padding("16px"),
    ...shorthands.borderRadius("12px"),
    border: "1px solid var(--aa-hairline)",
    backgroundColor: "rgba(255, 255, 255, 0.03)",
    marginBottom: "12px",
  },
  cardHeader: {
    display: "flex",
    alignItems: "center",
    justifyContent: "space-between",
    cursor: "pointer",
    gap: "12px",
    userSelect: "none",
  },
  agentInfo: {
    display: "flex",
    alignItems: "center",
    gap: "10px",
    minWidth: 0,
  },
  agentName: {
    fontSize: "14px",
    fontWeight: 600,
    color: "var(--aa-text-strong)",
  },
  agentRole: {
    fontSize: "12px",
    color: "var(--aa-muted)",
  },
  badges: {
    display: "flex",
    alignItems: "center",
    gap: "6px",
    flexShrink: 0,
  },
  formContainer: {
    marginTop: "16px",
    ...shorthands.borderTop("1px", "solid", "var(--aa-hairline)"),
    paddingTop: "16px",
    display: "flex",
    flexDirection: "column",
    gap: "16px",
  },
  fieldGroup: {
    display: "flex",
    flexDirection: "column",
    gap: "4px",
  },
  fieldLabel: {
    fontSize: "12px",
    fontWeight: 600,
    color: "var(--aa-soft)",
    textTransform: "uppercase" as const,
    letterSpacing: "0.5px",
  },
  fieldHint: {
    fontSize: "11px",
    color: "var(--aa-soft)",
  },
  actions: {
    display: "flex",
    alignItems: "center",
    gap: "8px",
    justifyContent: "flex-end",
    marginTop: "4px",
  },
  error: {
    fontSize: "13px",
    color: "var(--aa-copper)",
  },
  quotaSection: {
    ...shorthands.borderTop("1px", "solid", "var(--aa-hairline)"),
    paddingTop: "16px",
    display: "flex",
    flexDirection: "column",
    gap: "12px",
  },
  quotaHeader: {
    display: "flex",
    alignItems: "center",
    justifyContent: "space-between",
  },
  quotaSectionLabel: {
    fontSize: "12px",
    fontWeight: 700,
    color: "var(--aa-soft)",
    textTransform: "uppercase" as const,
    letterSpacing: "0.8px",
  },
  quotaGrid: {
    display: "grid",
    gridTemplateColumns: "1fr 1fr",
    gap: "12px",
  },
  quotaUsage: {
    fontSize: "11px",
    color: "var(--aa-muted)",
    marginTop: "2px",
  },
  quotaUnlimited: {
    fontSize: "12px",
    color: "var(--aa-muted)",
    fontStyle: "italic",
  },
});
