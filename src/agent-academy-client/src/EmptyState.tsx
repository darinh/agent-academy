import type { ReactNode } from "react";
import { Button, makeStyles } from "@fluentui/react-components";

const useLocalStyles = makeStyles({
  root: {
    display: "flex",
    flexDirection: "column",
    alignItems: "center",
    justifyContent: "center",
    height: "100%",
    gap: "12px",
    color: "var(--aa-soft)",
    padding: "24px",
    textAlign: "center",
  },
  icon: {
    fontSize: "48px",
    opacity: 0.5,
  },
  title: {
    fontSize: "15px",
    fontWeight: 600,
    color: "var(--aa-text)",
  },
  detail: {
    fontSize: "13px",
    color: "var(--aa-muted)",
    maxWidth: "320px",
    lineHeight: 1.5,
  },
});

interface EmptyStateProps {
  icon: ReactNode;
  title: string;
  detail?: string;
  action?: { label: string; onClick: () => void };
}

export default function EmptyState({ icon, title, detail, action }: EmptyStateProps) {
  const s = useLocalStyles();

  return (
    <div className={s.root}>
      <span className={s.icon}>{icon}</span>
      <span className={s.title}>{title}</span>
      {detail && <span className={s.detail}>{detail}</span>}
      {action && (
        <Button appearance="subtle" onClick={action.onClick}>
          {action.label}
        </Button>
      )}
    </div>
  );
}
