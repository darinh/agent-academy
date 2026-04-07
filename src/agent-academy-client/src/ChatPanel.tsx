import { memo, useCallback, useEffect, useMemo, useRef, useState } from "react";
import type { KeyboardEvent } from "react";
import Markdown from "react-markdown";
import remarkGfm from "remark-gfm";
import {
  Button,
  mergeClasses,
  Spinner,
  Textarea,
} from "@fluentui/react-components";
import {
  CheckmarkCircleRegular,
  ArrowSyncRegular,
  PlugDisconnectedRegular,
  WifiSettingsRegular,
} from "@fluentui/react-icons";
import { useStyles } from "./useStyles";
import { formatRole, roleColor } from "./theme";
import { formatTime } from "./utils";
import type { ChatEnvelope, RoomSnapshot } from "./api";
import { getRoomSessions } from "./api";
import { clearChatDraft, loadChatDraft, saveChatDraft } from "./recovery";
import EmptyState from "./EmptyState";
import SkeletonLoader from "./SkeletonLoader";
import type { ThinkingAgent } from "./useWorkspace";
import {
  isCommandResultMessage,
  parseCommandResults,
  loadFilters,
  shouldHideMessage,
  STATUS_LABELS,
  STATUS_COLORS,
  MESSAGE_LENGTH_THRESHOLD,
} from "./chatUtils";
import type { MessageFilter, ConnectionStatus } from "./chatUtils";

/* ── Command Result Bubble ──────────────────────────────────────── */

const CommandResultBubble = memo(function CommandResultBubble(props: {
  message: ChatEnvelope;
}) {
  const s = useStyles();
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

/* ── Message Bubble ─────────────────────────────────────────────── */

const MessageBubble = memo(function MessageBubble(props: {
  message: ChatEnvelope;
  expanded: boolean;
  onToggle: (id: string) => void;
}) {
  const s = useStyles();

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
          <span style={{ fontSize: "12px", fontWeight: 600 }}>{props.message.senderName}</span>
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

/* ── Thinking Bubble ────────────────────────────────────────────── */

const ThinkingBubble = memo(function ThinkingBubble(props: { agent: ThinkingAgent }) {
  const s = useStyles();
  const colors = roleColor(props.agent.role);

  return (
    <article className={s.bubble}>
      <div className={mergeClasses(s.bubbleCard, s.thinkingCard)} style={{ borderLeftColor: colors.accent }}>
        <div className={s.bubbleHeader}>
          <span style={{ fontSize: "12px", fontWeight: 600 }}>{props.agent.name}</span>
          <span className={s.rolePill} style={{ backgroundColor: colors.accent + "26", color: colors.accent }}>
            {formatRole(props.agent.role)}
          </span>
        </div>
        <div className={s.thinkingDots} role="status" aria-label={`${props.agent.name} is thinking`}>thinking ● ● ●</div>
      </div>
    </article>
  );
});

/* ── Status Icons (React components, not extractable) ──────────── */

const STATUS_ICONS: Record<ConnectionStatus, React.ReactNode> = {
  connected: <CheckmarkCircleRegular fontSize={14} />,
  connecting: <WifiSettingsRegular fontSize={14} />,
  reconnecting: <ArrowSyncRegular fontSize={14} />,
  disconnected: <PlugDisconnectedRegular fontSize={14} />,
};

const ChatPanel = memo(function ChatPanel(props: {
  room: RoomSnapshot | null;
  loading?: boolean;
  thinkingAgents: ThinkingAgent[];
  connectionStatus: ConnectionStatus;
  onSendMessage: (roomId: string, content: string) => Promise<boolean>;
  readOnly?: boolean;
  hiddenFilters?: Set<MessageFilter>;
}) {
  const s = useStyles();
  const scrollRef = useRef<HTMLDivElement>(null);
  const [humanMsg, setHumanMsg] = useState("");
  const [sending, setSending] = useState(false);
  const [expandedMsgs, setExpandedMsgs] = useState<Set<string>>(new Set());
  const [localHiddenFilters] = useState<Set<MessageFilter>>(loadFilters);
  const hiddenFilters = props.hiddenFilters ?? localHiddenFilters;
  const [hasArchivedContext, setHasArchivedContext] = useState(false);
  const [showNewMsgIndicator, setShowNewMsgIndicator] = useState(false);
  const isNearBottomRef = useRef(true);
  const prevMsgCountRef = useRef(0);
  const room = props.room;
  const readOnly = props.readOnly ?? false;
  const onSendMessage = props.onSendMessage;
  const thinkingAgents = props.thinkingAgents;
  const connectionStatus = props.connectionStatus;

  const filteredMessages = useMemo(
    () => room?.recentMessages.filter((m) => !shouldHideMessage(m, hiddenFilters)) ?? [],
    [hiddenFilters, room],
  );

  useEffect(() => {
    if (!room || readOnly) {
      setHumanMsg("");
    } else {
      setHumanMsg(loadChatDraft(room.id));
    }
    setExpandedMsgs(new Set());
  }, [readOnly, room]);

  // Check if this room has archived session context
  useEffect(() => {
    if (!room) {
      setHasArchivedContext(false);
      return;
    }
    let cancelled = false;
    getRoomSessions(room.id, "Archived", 1)
      .then((res) => {
        if (!cancelled) setHasArchivedContext(res.totalCount > 0);
      })
      .catch(() => {
        if (!cancelled) setHasArchivedContext(false);
      });
    return () => { cancelled = true; };
  }, [room?.id]); // eslint-disable-line react-hooks/exhaustive-deps

  useEffect(() => {
    if (!room || readOnly) {
      return;
    }

    saveChatDraft(room.id, humanMsg);
  }, [humanMsg, readOnly, room]);

  // Track scroll position to determine if user is near bottom
  useEffect(() => {
    const el = scrollRef.current;
    if (!el) return;
    const onScroll = () => {
      const threshold = 80;
      isNearBottomRef.current = el.scrollHeight - el.scrollTop - el.clientHeight < threshold;
      if (isNearBottomRef.current) setShowNewMsgIndicator(false);
    };
    el.addEventListener("scroll", onScroll, { passive: true });
    return () => el.removeEventListener("scroll", onScroll);
  }, []);

  // Auto-scroll only when near bottom; show indicator otherwise
  useEffect(() => {
    const el = scrollRef.current;
    if (!el) return;
    const newCount = filteredMessages.length;
    if (newCount > prevMsgCountRef.current) {
      if (isNearBottomRef.current) {
        el.scrollTo({ top: el.scrollHeight, behavior: "auto" });
      } else {
        setShowNewMsgIndicator(true);
      }
    }
    prevMsgCountRef.current = newCount;
  }, [filteredMessages.length]);

  // Always scroll on room change or thinking state change; reset indicator state
  useEffect(() => {
    const el = scrollRef.current;
    if (el) el.scrollTo({ top: el.scrollHeight, behavior: "auto" });
    setShowNewMsgIndicator(false);
    isNearBottomRef.current = true;
    prevMsgCountRef.current = filteredMessages.length;
  }, [room?.id, thinkingAgents.length]); // eslint-disable-line react-hooks/exhaustive-deps

  const scrollToBottom = useCallback(() => {
    const el = scrollRef.current;
    if (el) el.scrollTo({ top: el.scrollHeight, behavior: "smooth" });
    setShowNewMsgIndicator(false);
  }, []);

  const toggleExpand = useCallback((id: string) => {
    setExpandedMsgs((cur) => {
      const next = new Set(cur);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  }, []);

  const doSend = useCallback(async () => {
    const rid = room?.id;
    const content = humanMsg.trim();
    if (!rid || !content) return;
    setSending(true);
    try {
      const sent = await onSendMessage(rid, content);
      if (sent) {
        clearChatDraft(rid);
        setHumanMsg("");
      }
    } finally {
      setSending(false);
    }
  }, [humanMsg, onSendMessage, room?.id]);

  const handleKeyDown = useCallback((event: KeyboardEvent<HTMLTextAreaElement>) => {
    if (event.key === "Enter" && !event.shiftKey) {
      event.preventDefault();
      void doSend();
    }
  }, [doSend]);


  return (
    <div className={s.conversationLayout}>
       <div ref={scrollRef} className={s.messageList} role="log" aria-label="Conversation messages" aria-live="polite">
        {hasArchivedContext && (
          <div style={{
            display: "flex",
            alignItems: "center",
            gap: "8px",
            padding: "8px 14px",
            borderRadius: "12px",
            backgroundColor: "rgba(183, 148, 255, 0.08)",
            border: "1px solid rgba(183, 148, 255, 0.18)",
            fontSize: "12px",
            color: "var(--aa-soft)",
            marginBottom: "8px",
          }}>
            <span style={{ fontSize: "14px" }}>📋</span>
            Agents have context from a previous conversation session
          </div>
        )}
        {props.loading && filteredMessages.length === 0 ? (
          <SkeletonLoader rows={4} variant="chat" />
        ) : filteredMessages.length ? (
          filteredMessages.map((msg) => (
            <MessageBubble key={msg.id} message={msg} expanded={expandedMsgs.has(msg.id)} onToggle={toggleExpand} />
          ))
        ) : (
          <EmptyState
            icon={<span style={{ fontSize: "48px", opacity: 0.5 }}>💬</span>}
            title={room?.recentMessages.length ? "All messages filtered" : "No messages yet"}
            detail={room?.recentMessages.length ? "Adjust filters above to see hidden messages." : "Messages will appear here when the team starts collaborating."}
          />
        )}
        {thinkingAgents.map((agent) => (
          <ThinkingBubble key={agent.id} agent={agent} />
        ))}
      </div>

      {showNewMsgIndicator && (
        <button
          onClick={scrollToBottom}
          style={{
            display: "flex",
            alignItems: "center",
            justifyContent: "center",
            gap: "6px",
            width: "fit-content",
            margin: "0 auto",
            padding: "6px 16px",
            borderRadius: "999px",
            border: "1px solid rgba(124, 176, 248, 0.3)",
            background: "rgba(124, 176, 248, 0.12)",
            color: "var(--aa-cyan)",
            fontSize: "12px",
            fontWeight: 600,
            cursor: "pointer",
          }}
          type="button"
          aria-label="Scroll to new messages"
        >
          New messages ↓
        </button>
      )}

      {connectionStatus !== "connected" && (
        <div className={s.statusBar} role="status" aria-label={STATUS_LABELS[connectionStatus]}>
          <span className={s.statusIndicator} style={{ backgroundColor: STATUS_COLORS[connectionStatus] }}>
            {STATUS_ICONS[connectionStatus]}
          </span>
          {STATUS_LABELS[connectionStatus]}
        </div>
      )}

      {room && !readOnly && (
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
            <span style={{ fontSize: "11px", color: "var(--aa-muted)", marginRight: "auto" }}>
              Enter to send · Shift+Enter for new line
            </span>
            <Button
              appearance="subtle"
              onClick={() => {
                if (room) {
                  clearChatDraft(room.id);
                }
                setHumanMsg("");
              }}
              disabled={sending || !humanMsg}
            >
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
