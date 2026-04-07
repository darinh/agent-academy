import { memo } from "react";
import { makeStyles, shorthands } from "@fluentui/react-components";
import {
  ShieldErrorRegular,
  ArrowSyncRegular,
} from "@fluentui/react-icons";
import type { CircuitBreakerState } from "./useCircuitBreakerPolling";

const useLocalStyles = makeStyles({
  root: {
    position: "fixed",
    top: "16px",
    left: "50%",
    transform: "translateX(-50%)",
    zIndex: 999,
    maxWidth: "520px",
    width: "calc(100% - 32px)",
    display: "grid",
    gap: "8px",
    ...shorthands.borderRadius("20px"),
    ...shorthands.padding("14px", "18px"),
    border: "1px solid rgba(91, 141, 239, 0.08)",
    boxShadow: "0 18px 40px rgba(0, 0, 0, 0.3)",
    animationName: {
      from: { opacity: 0, transform: "translateX(-50%) translateY(-10px)" },
      to: { opacity: 1, transform: "translateX(-50%) translateY(0)" },
    },
    animationDuration: "300ms",
    animationTimingFunction: "ease-out",
    animationFillMode: "both",
  },
  open: {
    background:
      "linear-gradient(135deg, rgba(248, 81, 73, 0.20), rgba(80, 20, 20, 0.92) 45%, rgba(22, 10, 14, 0.96))",
  },
  halfOpen: {
    background:
      "linear-gradient(135deg, rgba(251, 191, 36, 0.18), rgba(60, 45, 15, 0.92) 45%, rgba(20, 15, 8, 0.96))",
  },
  badge: {
    display: "inline-flex",
    alignItems: "center",
    gap: "8px",
    width: "fit-content",
    color: "var(--aa-text-strong)",
    backgroundColor: "rgba(91, 141, 239, 0.08)",
    border: "1px solid rgba(91, 141, 239, 0.12)",
    ...shorthands.borderRadius("999px"),
    ...shorthands.padding("6px", "10px"),
    fontSize: "11px",
    fontWeight: 700,
    letterSpacing: "0.08em",
    textTransform: "uppercase" as const,
  },
  message: {
    color: "#f4f8ff",
    fontSize: "15px",
    fontWeight: 600,
  },
  detail: {
    color: "var(--aa-muted)",
    fontSize: "13px",
    lineHeight: 1.5,
  },
});

const CircuitBreakerBanner = memo(function CircuitBreakerBanner(props: {
  state: CircuitBreakerState;
}) {
  const s = useLocalStyles();

  if (!props.state || props.state === "Closed") return null;

  const isOpen = props.state === "Open";

  return (
    <div
      className={`${s.root} ${isOpen ? s.open : s.halfOpen}`}
      role="alert"
      aria-live="assertive"
      data-testid="circuit-breaker-banner"
    >
      <div className={s.badge}>
        {isOpen ? (
          <ShieldErrorRegular style={{ fontSize: 16 }} />
        ) : (
          <ArrowSyncRegular style={{ fontSize: 16 }} />
        )}
        <span>{isOpen ? "Requests paused" : "Checking recovery"}</span>
      </div>
      <div className={s.message}>
        {isOpen
          ? "Agent requests are paused while the backend recovers"
          : "Sending a test request to verify the backend is healthy"}
      </div>
      <div className={s.detail}>
        {isOpen
          ? "The backend experienced repeated failures. Requests will resume automatically once the system recovers — usually within a minute."
          : "A single probe request is being sent. If it succeeds, normal operation resumes immediately."}
      </div>
    </div>
  );
});

export default CircuitBreakerBanner;
