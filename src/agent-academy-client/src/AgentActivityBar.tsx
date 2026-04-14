import { memo } from "react";
import { Tooltip, makeStyles, shorthands } from "@fluentui/react-components";
import { roleColor } from "./theme";
import ContextMeter from "./ContextMeter";
import type { AgentDefinition, AgentLocation, AgentContextUsage } from "./api";

const useLocalStyles = makeStyles({
  root: {
    display: "flex",
    alignItems: "center",
    gap: "6px",
    ...shorthands.padding("8px", "14px"),
    ...shorthands.borderRadius("16px"),
    background: "rgba(139, 148, 158, 0.03)",
    border: "1px solid var(--aa-border)",
    overflowX: "auto",
    scrollbarWidth: "none",
    "::-webkit-scrollbar": { display: "none" },
  },
  label: {
    fontSize: "10px",
    fontWeight: 700,
    letterSpacing: "0.12em",
    textTransform: "uppercase" as const,
    color: "var(--aa-soft)",
    whiteSpace: "nowrap",
    marginRight: "4px",
    flexShrink: 0,
  },
  pill: {
    display: "inline-flex",
    alignItems: "center",
    gap: "6px",
    ...shorthands.padding("4px", "10px", "4px", "6px"),
    ...shorthands.borderRadius("999px"),
    border: "1px solid rgba(255, 255, 255, 0.06)",
    background: "rgba(255, 255, 255, 0.04)",
    fontSize: "12px",
    fontWeight: 600,
    color: "var(--aa-text)",
    whiteSpace: "nowrap",
    flexShrink: 0,
    transitionProperty: "background, border-color, box-shadow",
    transitionDuration: "0.3s",
    transitionTimingFunction: "ease",
  },
  pillWorking: {
    background: "rgba(72, 214, 122, 0.08)",
    border: "1px solid rgba(72, 214, 122, 0.22)",
    boxShadow: "0 0 8px rgba(72, 214, 122, 0.12)",
  },
  pillThinking: {
    background: "rgba(108, 182, 255, 0.08)",
    border: "1px solid rgba(108, 182, 255, 0.22)",
    boxShadow: "0 0 8px rgba(108, 182, 255, 0.12)",
  },
  dot: {
    width: "8px",
    height: "8px",
    ...shorthands.borderRadius("999px"),
    flexShrink: 0,
  },
  dotIdle: {
    opacity: 0.5,
  },
  dotActive: {
    animationName: {
      "0%, 100%": { opacity: 1, transform: "scale(1)" },
      "50%": { opacity: 0.6, transform: "scale(0.85)" },
    },
    animationDuration: "1.8s",
    animationTimingFunction: "ease-in-out",
    animationIterationCount: "infinite",
  },
  stateLabel: {
    fontSize: "10px",
    fontWeight: 600,
    letterSpacing: "0.04em",
    textTransform: "uppercase" as const,
    opacity: 0.7,
  },
});

interface AgentActivityBarProps {
  agents: AgentDefinition[];
  locations: AgentLocation[];
  thinkingAgentIds: Set<string>;
  contextUsage?: Map<string, AgentContextUsage>;
}

const AgentActivityBar = memo(function AgentActivityBar({
  agents,
  locations,
  thinkingAgentIds,
  contextUsage,
}: AgentActivityBarProps) {
  const s = useLocalStyles();

  if (agents.length === 0) return null;

  const locationMap = new Map(locations.map((l) => [l.agentId, l]));

  return (
    <div className={s.root}>
      <span className={s.label}>Agents</span>
      {agents.map((agent) => {
        const loc = locationMap.get(agent.id);
        const isThinking = thinkingAgentIds.has(agent.id);
        const isWorking = loc?.state === "Working";
        const rc = roleColor(agent.role);
        const state = isThinking ? "Thinking" : (loc?.state ?? "Idle");
        const agentContext = contextUsage?.get(agent.id);
        const tooltipContent = `${agent.name} · ${agent.role} · ${state}`;

        const pillClass = [
          s.pill,
          isThinking ? s.pillThinking : isWorking ? s.pillWorking : undefined,
        ]
          .filter(Boolean)
          .join(" ");

        const dotClass = [
          s.dot,
          isWorking || isThinking ? s.dotActive : s.dotIdle,
        ]
          .filter(Boolean)
          .join(" ");

        return (
          <Tooltip key={agent.id} content={tooltipContent} relationship="label" positioning="below">
            <span className={pillClass}>
              <span className={dotClass} style={{ backgroundColor: rc.accent }} />
              <span>{agent.name}</span>
              {(isWorking || isThinking) && (
                <span className={s.stateLabel} style={{ color: rc.accent }}>
                  {state}
                </span>
              )}
              {agentContext && <ContextMeter usage={agentContext} />}
            </span>
          </Tooltip>
        );
      })}
    </div>
  );
});

export default AgentActivityBar;
