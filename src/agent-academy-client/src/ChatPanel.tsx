import { memo, useCallback, useEffect, useRef, useState } from "react";
import type { KeyboardEvent } from "react";
import Markdown from "react-markdown";
import remarkGfm from "remark-gfm";
import {
  Avatar,
  Body1,
  Body1Strong,
  Button,
  mergeClasses,
  Spinner,
  Textarea,
} from "@fluentui/react-components";
import { useStyles } from "./useStyles";
import { formatRole, roleColor } from "./theme";
import { formatTime } from "./utils";
import type { ChatEnvelope, RoomSnapshot } from "./api";
import type { ThinkingAgent } from "./useWorkspace";
import type { ConnectionStatus } from "./useActivityHub";

/* ── Message Bubble ─────────────────────────────────────────────── */

const MessageBubble = memo(function MessageBubble(props: {
  message: ChatEnvelope;
  expanded: boolean;
  onToggle: (id: string) => void;
}) {
  const s = useStyles();

  if (props.message.senderKind === "System") {
    return <div className={s.systemMessage}>{props.message.content}</div>;
  }

  const colors = roleColor(
    props.message.senderRole ?? (props.message.senderKind === "User" ? "Human" : undefined),
  );
  const isLong = props.message.content.length > 300;

  return (
    <article className={s.bubble}>
      <Avatar
        name={props.message.senderName}
        color={colors.avatar}
        size={40}
        style={{
          backgroundColor: colors.accent,
          color: colors.foreground,
          boxShadow: "0 14px 30px rgba(0, 0, 0, 0.24)",
        }}
      />
      <div className={s.bubbleCard}>
        <div className={s.bubbleHeader}>
          <Body1Strong>{props.message.senderName}</Body1Strong>
          <span
            className={s.rolePill}
            style={{ backgroundColor: colors.accent, color: colors.foreground }}
          >
            {formatRole(props.message.senderRole ?? (props.message.senderKind === "User" ? "Human" : "Agent"))}
          </span>
          <span className={s.messageTime}>{formatTime(props.message.sentAt)}</span>
        </div>
        <div className={mergeClasses(s.bubbleText, isLong && !props.expanded ? s.bubbleCollapsed : undefined)}>
          <Markdown remarkPlugins={[remarkGfm]}>
            {props.expanded || !isLong ? props.message.content : props.message.content.substring(0, 300) + "…"}
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

/* ── Thinking Bubble ────────────────────────────────────────────── */

const ThinkingBubble = memo(function ThinkingBubble(props: { agent: ThinkingAgent }) {
  const s = useStyles();
  const colors = roleColor(props.agent.role);

  return (
    <article className={s.bubble}>
      <Avatar
        name={props.agent.name}
        color={colors.avatar}
        size={40}
        style={{ backgroundColor: colors.accent, color: colors.foreground }}
      />
      <div className={mergeClasses(s.bubbleCard, s.thinkingCard)} style={{ borderLeftColor: colors.accent }}>
        <div className={s.bubbleHeader}>
          <Body1Strong>{props.agent.name}</Body1Strong>
          <span className={s.rolePill} style={{ backgroundColor: colors.accent, color: colors.foreground }}>
            {formatRole(props.agent.role)}
          </span>
        </div>
        <div className={s.thinkingDots}>thinking ● ● ●</div>
      </div>
    </article>
  );
});

/* ── Chat Panel ─────────────────────────────────────────────────── */

const STATUS_LABELS: Record<ConnectionStatus, string> = {
  connected: "Live — real-time updates active",
  connecting: "Connecting to live updates…",
  reconnecting: "Reconnecting…",
  disconnected: "Disconnected — falling back to polling",
};

const STATUS_COLORS: Record<ConnectionStatus, string> = {
  connected: "#34d399",
  connecting: "#fbbf24",
  reconnecting: "#fbbf24",
  disconnected: "#f87171",
};

const ChatPanel = memo(function ChatPanel(props: {
  room: RoomSnapshot | null;
  thinkingAgents: ThinkingAgent[];
  connectionStatus: ConnectionStatus;
  onSendMessage: (roomId: string, content: string) => Promise<boolean>;
  readOnly?: boolean;
}) {
  const s = useStyles();
  const scrollRef = useRef<HTMLDivElement>(null);
  const [humanMsg, setHumanMsg] = useState("");
  const [sending, setSending] = useState(false);
  const [expandedMsgs, setExpandedMsgs] = useState<Set<string>>(new Set());

  useEffect(() => {
    setHumanMsg("");
    setExpandedMsgs(new Set());
  }, [props.room?.id]);

  useEffect(() => {
    const el = scrollRef.current;
    if (el) el.scrollTo({ top: el.scrollHeight, behavior: "auto" });
  }, [props.room?.id, props.room?.recentMessages.length, props.thinkingAgents.length]);

  const toggleExpand = useCallback((id: string) => {
    setExpandedMsgs((cur) => {
      const next = new Set(cur);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  }, []);

  const doSend = useCallback(async () => {
    const rid = props.room?.id;
    const content = humanMsg.trim();
    if (!rid || !content) return;
    setSending(true);
    try {
      const sent = await props.onSendMessage(rid, content);
      if (sent) setHumanMsg("");
    } finally {
      setSending(false);
    }
  }, [humanMsg, props.onSendMessage, props.room?.id]);

  const handleKeyDown = useCallback((event: KeyboardEvent<HTMLTextAreaElement>) => {
    if (event.key === "Enter" && !event.shiftKey) {
      event.preventDefault();
      void doSend();
    }
  }, [doSend]);

  return (
    <div className={s.conversationLayout}>
      <div ref={scrollRef} className={s.messageList} role="log" aria-label="Conversation messages" aria-live="polite">
        {props.room?.recentMessages.length ? (
          props.room.recentMessages.map((msg) => (
            <MessageBubble key={msg.id} message={msg} expanded={expandedMsgs.has(msg.id)} onToggle={toggleExpand} />
          ))
        ) : (
          <Body1 className={s.emptyState}>No messages yet for this room.</Body1>
        )}
        {props.thinkingAgents.map((agent) => (
          <ThinkingBubble key={agent.id} agent={agent} />
        ))}
      </div>

      <div className={s.statusBar}>
        <span className={s.statusIndicator} style={{ backgroundColor: STATUS_COLORS[props.connectionStatus] }} />
        {STATUS_LABELS[props.connectionStatus]}
      </div>

      {props.room && !props.readOnly && (
        <div className={s.composerShell}>
          <div className={s.composerLabel}>Message the team</div>
          <Textarea
            className={s.composerField}
            appearance="filled-darker"
            placeholder={sending ? "Sending…" : "Type a message to the agents…"}
            value={humanMsg}
            onChange={(_, d) => setHumanMsg(d.value)}
            onKeyDown={handleKeyDown}
            resize="vertical"
            rows={3}
            disabled={sending}
            aria-label="Message to agents"
          />
          <div className={s.composerActions}>
            <Button appearance="subtle" onClick={() => setHumanMsg("")} disabled={sending || !humanMsg}>
              Clear
            </Button>
            <Button appearance="primary" onClick={() => void doSend()} disabled={!humanMsg.trim() || sending}>
              {sending ? <Spinner size="tiny" /> : "Send message"}
            </Button>
          </div>
        </div>
      )}
    </div>
  );
});

export default ChatPanel;
