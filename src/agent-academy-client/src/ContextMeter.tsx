import { memo } from "react";
import { Tooltip, makeStyles, shorthands } from "@fluentui/react-components";
import type { AgentContextUsage } from "./api";

const useStyles = makeStyles({
  root: {
    display: "inline-flex",
    alignItems: "center",
    gap: "4px",
  },
  barOuter: {
    width: "32px",
    height: "4px",
    ...shorthands.borderRadius("2px"),
    backgroundColor: "rgba(255, 255, 255, 0.08)",
    overflow: "hidden",
    flexShrink: 0,
  },
  barInner: {
    height: "100%",
    ...shorthands.borderRadius("2px"),
    transitionProperty: "width, background-color",
    transitionDuration: "0.4s",
    transitionTimingFunction: "ease",
  },
  label: {
    fontSize: "9px",
    fontWeight: 600,
    letterSpacing: "0.02em",
    opacity: 0.7,
    whiteSpace: "nowrap",
    flexShrink: 0,
  },
});

function barColor(pct: number): string {
  if (pct >= 90) return "#f85149";
  if (pct >= 75) return "#d29922";
  if (pct >= 50) return "#58a6ff";
  return "rgba(139, 148, 158, 0.45)";
}

function formatTokens(n: number): string {
  if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(1)}M`;
  if (n >= 1_000) return `${(n / 1_000).toFixed(0)}K`;
  return String(n);
}

interface ContextMeterProps {
  usage: AgentContextUsage;
}

const ContextMeter = memo(function ContextMeter({ usage }: ContextMeterProps) {
  const s = useStyles();
  const pct = Math.min(usage.percentage, 100);
  const color = barColor(pct);
  const tooltip = [
    `Context: ${formatTokens(usage.currentTokens)} / ${formatTokens(usage.maxTokens)}`,
    `Model: ${usage.model ?? "unknown"}`,
    pct >= 90 ? "⚠ Context nearly full — consider compacting" : "",
    pct >= 75 ? "Context usage is elevated" : "",
  ].filter(Boolean).join("\n");

  return (
    <Tooltip content={tooltip} relationship="description" positioning="below">
      <span className={s.root}>
        <span className={s.barOuter}>
          <span
            className={s.barInner}
            style={{ width: `${pct}%`, backgroundColor: color }}
          />
        </span>
        <span className={s.label} style={{ color }}>
          {Math.round(pct)}%
        </span>
      </span>
    </Tooltip>
  );
});

export default ContextMeter;
