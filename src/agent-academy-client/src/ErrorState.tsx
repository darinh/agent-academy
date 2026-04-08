import { Button, makeStyles } from "@fluentui/react-components";
import { ErrorCircleRegular } from "@fluentui/react-icons";

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
    color: "var(--aa-copper)",
    opacity: 0.7,
  },
  message: {
    fontSize: "14px",
    color: "var(--aa-text)",
  },
  detail: {
    fontSize: "13px",
    color: "var(--aa-muted)",
    maxWidth: "320px",
    lineHeight: 1.5,
  },
});

interface ErrorStateProps {
  message: string;
  detail?: string;
  onRetry?: () => void;
}

export default function ErrorState({ message, detail, onRetry }: ErrorStateProps) {
  const s = useLocalStyles();

  return (
    <div className={s.root}>
      <ErrorCircleRegular className={s.icon} />
      <span className={s.message}>{message}</span>
      {detail && <span className={s.detail}>{detail}</span>}
      {onRetry && (
        <Button appearance="subtle" onClick={onRetry}>
          Try again
        </Button>
      )}
    </div>
  );
}
