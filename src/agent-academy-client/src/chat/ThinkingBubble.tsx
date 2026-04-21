import { memo } from "react";
import { mergeClasses } from "@fluentui/react-components";
import { useChatStyles } from "../styles";
import { formatRole, roleColor } from "../theme";
import type { ThinkingAgent } from "../useWorkspace";

export const ThinkingBubble = memo(function ThinkingBubble(props: { agent: ThinkingAgent }) {
  const s = useChatStyles();
  const colors = roleColor(props.agent.role);

  return (
    <article className={s.bubble}>
      <div className={mergeClasses(s.bubbleCard, s.thinkingCard)} style={{ borderLeftColor: colors.accent }}>
        <div className={s.bubbleHeader}>
          <span style={{ fontFamily: "var(--mono)", fontSize: "12px", fontWeight: 600, lineHeight: 1 }}>{props.agent.name}</span>
          <span className={s.rolePill} style={{ backgroundColor: colors.accent + "26", color: colors.accent }}>
            {formatRole(props.agent.role)}
          </span>
        </div>
        <div className={s.thinkingDots} role="status" aria-label={`${props.agent.name} is thinking`}>thinking ● ● ●</div>
      </div>
    </article>
  );
});
