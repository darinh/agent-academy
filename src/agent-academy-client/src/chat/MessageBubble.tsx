import { memo, useMemo } from "react";
import Markdown from "react-markdown";
import remarkGfm from "remark-gfm";
import { mergeClasses } from "@fluentui/react-components";
import { useChatStyles } from "../styles";
import { formatRole, roleColor } from "../theme";
import { formatTime } from "../utils";
import type { ChatEnvelope } from "../api";
import {
  isCommandResultMessage,
  parseCommandResults,
  MESSAGE_LENGTH_THRESHOLD,
} from "../chatUtils";

const CommandResultBubble = memo(function CommandResultBubble(props: {
  message: ChatEnvelope;
}) {
  const s = useChatStyles();
  const results = useMemo(() => parseCommandResults(props.message.content), [props.message.content]);

  if (results.length === 0) {
    return <div className={s.systemMessage}>{props.message.content}</div>;
  }

  return (
    <div className={s.commandResultBlock}>
      {results.map((r, i) => (
        <details key={i} className={s.commandResultItem}>
          <summary className={s.commandResultSummary}>
            <span className={r.status === "Success" ? s.commandStatusOk : s.commandStatusErr}>
              {r.status === "Success" ? "✅" : r.status === "Denied" ? "🚫" : "❌"}
            </span>
            <span className={s.commandName}>{r.command}</span>
            {r.error && <span className={s.commandError}>{r.error}</span>}
          </summary>
          {r.detail && (
            <pre className={s.commandDetail}>{r.detail}</pre>
          )}
        </details>
      ))}
    </div>
  );
});

export const MessageBubble = memo(function MessageBubble(props: {
  message: ChatEnvelope;
  expanded: boolean;
  onToggle: (id: string) => void;
}) {
  const s = useChatStyles();

  if (props.message.senderKind === "System") {
    if (isCommandResultMessage(props.message.content)) {
      return <CommandResultBubble message={props.message} />;
    }
    return <div className={s.systemMessage}>{props.message.content}</div>;
  }

  const colors = roleColor(
    props.message.senderRole ?? (props.message.senderKind === "User" ? "Human" : undefined),
  );
  const isLong = props.message.content.length > MESSAGE_LENGTH_THRESHOLD;

  return (
    <article className={s.bubble}>
      <div className={s.bubbleCard}>
        <div className={s.bubbleHeader}>
          <span style={{ fontFamily: "var(--mono)", fontSize: "12px", fontWeight: 600, lineHeight: 1 }}>{props.message.senderName}</span>
          <span
            className={s.rolePill}
            style={{ backgroundColor: colors.accent + "26", color: colors.accent }}
          >
            {formatRole(props.message.senderRole ?? (props.message.senderKind === "User" ? "Human" : "Agent"))}
          </span>
          <span className={s.messageTime}>{formatTime(props.message.sentAt)}</span>
        </div>
        <div className={mergeClasses(s.bubbleText, isLong && !props.expanded ? s.bubbleCollapsed : undefined)}>
          <Markdown remarkPlugins={[remarkGfm]}>
            {props.expanded || !isLong ? props.message.content : props.message.content.substring(0, MESSAGE_LENGTH_THRESHOLD) + "…"}
          </Markdown>
        </div>
        {isLong && (
          <button
            className={s.expandButton}
            onClick={() => props.onToggle(props.message.id)}
            type="button"
          >
            {props.expanded ? "Show less" : "Show more"}
          </button>
        )}
      </div>
    </article>
  );
});
