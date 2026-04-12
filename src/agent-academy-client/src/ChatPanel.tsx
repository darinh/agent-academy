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
import { useChatStyles } from "./styles";
import { formatRole, roleColor } from "./theme";
import { formatTime } from "./utils";
import type { ChatEnvelope, RoomSnapshot, AgentLocation, AgentDefinition, ConversationSessionSnapshot } from "./api";
import { getRoomSessions, getRoomMessages } from "./api";
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

/* ── Message Bubble ─────────────────────────────────────────────── */

const MessageBubble = memo(function MessageBubble(props: {
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

/* ── Thinking Bubble ────────────────────────────────────────────── */

const ThinkingBubble = memo(function ThinkingBubble(props: { agent: ThinkingAgent }) {
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
  agentLocations?: AgentLocation[];
  configuredAgents?: AgentDefinition[];
  onCreateSession?: (roomId: string) => void;
  onToggleAgent?: (roomId: string, agentId: string, present: boolean) => void;
}) {
  const s = useChatStyles();
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

  // Session management
  const [sessions, setSessions] = useState<ConversationSessionSnapshot[]>([]);
  const [selectedSessionId, setSelectedSessionId] = useState<string | null>(null);
  const [sessionMessages, setSessionMessages] = useState<ChatEnvelope[] | null>(null);
  const [agentsOpen, setAgentsOpen] = useState(false);
  const agentsRef = useRef<HTMLDivElement>(null);

  const displayMessages = sessionMessages ?? room?.recentMessages ?? [];
  const filteredMessages = useMemo(
    () => displayMessages.filter((m) => !shouldHideMessage(m, hiddenFilters)),
    [hiddenFilters, displayMessages],
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

  // Load sessions for the room
  useEffect(() => {
    if (!room) { setSessions([]); setSelectedSessionId(null); setSessionMessages(null); return; }
    let cancelled = false;
    getRoomSessions(room.id, undefined, 50)
      .then((res) => { if (!cancelled) setSessions(res.sessions); })
      .catch(() => { if (!cancelled) setSessions([]); });
    setSelectedSessionId(null);
    setSessionMessages(null);
    return () => { cancelled = true; };
  }, [room?.id]); // eslint-disable-line react-hooks/exhaustive-deps

  // Load messages when viewing a non-active session
  useEffect(() => {
    if (!selectedSessionId || !room) { setSessionMessages(null); return; }
    // Find if this is the active session — if so, use live messages
    const activeSession = sessions.find(s => s.status === "Active");
    if (activeSession && activeSession.id === selectedSessionId) {
      setSessionMessages(null); // null = use live room messages
      return;
    }
    let cancelled = false;
    getRoomMessages(room.id, { sessionId: selectedSessionId, limit: 200 })
      .then((res) => { if (!cancelled) setSessionMessages(res.messages); })
      .catch(() => { if (!cancelled) setSessionMessages([]); });
    return () => { cancelled = true; };
  }, [selectedSessionId, room?.id, sessions]); // eslint-disable-line react-hooks/exhaustive-deps

  // Close agents dropdown on outside click
  useEffect(() => {
    if (!agentsOpen) return;
    const handler = (e: MouseEvent) => {
      if (agentsRef.current && !agentsRef.current.contains(e.target as Node)) setAgentsOpen(false);
    };
    document.addEventListener("mousedown", handler);
    return () => document.removeEventListener("mousedown", handler);
  }, [agentsOpen]);

  const handleNewSession = useCallback(() => {
    if (!room || !props.onCreateSession) return;
    props.onCreateSession(room.id);
    // Refresh sessions after a short delay for the backend to process
    setTimeout(() => {
      getRoomSessions(room.id, undefined, 50)
        .then((res) => { setSessions(res.sessions); setSelectedSessionId(null); setSessionMessages(null); })
        .catch(() => {});
    }, 500);
  }, [room, props.onCreateSession]);

  const handleSessionChange = useCallback((e: React.ChangeEvent<HTMLSelectElement>) => {
    const val = e.target.value;
    setSelectedSessionId(val || null);
  }, []);

  const handleToggleAgent = useCallback((agentId: string, currentlyInRoom: boolean) => {
    if (!room || !props.onToggleAgent) return;
    props.onToggleAgent(room.id, agentId, currentlyInRoom);
  }, [room, props.onToggleAgent]);

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


  const agentLocations = props.agentLocations ?? [];
  const configuredAgents = props.configuredAgents ?? [];
  const agentsInRoom = useMemo(
    () => new Set(agentLocations.filter(l => l.roomId === room?.id).map(l => l.agentId)),
    [agentLocations, room?.id],
  );
  const viewingHistoricSession = selectedSessionId != null && sessionMessages != null;

  return (
    <div className={s.conversationLayout}>
      {/* Session & Agent toolbar */}
      {room && !readOnly && (
        <div style={{
          display: "flex", alignItems: "center", gap: "8px",
          padding: "6px 12px", borderBottom: "1px solid var(--aa-border, #333)",
          fontSize: "12px", flexShrink: 0,
        }}>
          {props.onCreateSession && (
            <button
              onClick={handleNewSession}
              style={{
                background: "var(--aa-surface, #1e1e2e)", border: "1px solid var(--aa-border, #333)",
                borderRadius: "4px", padding: "3px 10px", color: "inherit", cursor: "pointer",
                fontSize: "12px", whiteSpace: "nowrap",
              }}
            >
              + New Session
            </button>
          )}
          {sessions.length >= 0 && (
            <select
              value={selectedSessionId ?? ""}
              onChange={handleSessionChange}
              style={{
                background: "var(--aa-surface, #1e1e2e)", border: "1px solid var(--aa-border, #333)",
                borderRadius: "4px", padding: "3px 8px", color: "inherit", fontSize: "12px",
                maxWidth: "180px",
              }}
            >
              <option value="">Current session</option>
              {sessions
                .filter(s => s.status === "Archived")
                .map(s => (
                  <option key={s.id} value={s.id}>
                    Session #{s.sequenceNumber} ({s.messageCount} msgs)
                  </option>
                ))}
            </select>
          )}
          {configuredAgents.length > 0 && props.onToggleAgent && (
            <div ref={agentsRef} style={{ position: "relative" }}>
              <button
                onClick={() => setAgentsOpen(!agentsOpen)}
                style={{
                  background: "var(--aa-surface, #1e1e2e)", border: "1px solid var(--aa-border, #333)",
                  borderRadius: "4px", padding: "3px 10px", color: "inherit", cursor: "pointer",
                  fontSize: "12px", whiteSpace: "nowrap",
                }}
              >
                Agents ({agentsInRoom.size})
              </button>
              {agentsOpen && (
                <div style={{
                  position: "absolute", right: 0, top: "100%", zIndex: 100,
                  background: "var(--aa-panel, #181825)", border: "1px solid var(--aa-border, #333)",
                  borderRadius: "6px", padding: "6px 0", minWidth: "180px",
                  boxShadow: "0 4px 12px rgba(0,0,0,0.4)",
                }}>
                  {configuredAgents.map((agent) => {
                    const inRoom = agentsInRoom.has(agent.id);
                    return (
                      <label
                        key={agent.id}
                        style={{
                          display: "flex", alignItems: "center", gap: "8px",
                          padding: "4px 12px", cursor: "pointer", fontSize: "12px",
                        }}
                      >
                        <input
                          type="checkbox"
                          checked={inRoom}
                          onChange={() => handleToggleAgent(agent.id, inRoom)}
                        />
                        <span>{agent.name}</span>
                        <span style={{ color: "var(--aa-soft)", fontSize: "11px", marginLeft: "auto" }}>
                          {agent.role}
                        </span>
                      </label>
                    );
                  })}
                </div>
              )}
            </div>
          )}
        </div>
      )}

       <div ref={scrollRef} className={s.messageList} role="log" aria-label="Conversation messages" aria-live="polite">
        {viewingHistoricSession && (
          <div style={{
            display: "flex", alignItems: "center", gap: "8px",
            padding: "8px 14px", borderRadius: "12px",
            backgroundColor: "rgba(255, 193, 7, 0.08)",
            border: "1px solid rgba(255, 193, 7, 0.18)",
            fontSize: "12px", color: "var(--aa-soft)", marginBottom: "8px",
          }}>
            <span style={{ fontSize: "14px" }}>📂</span>
            Viewing archived session. Messages are read-only.
          </div>
        )}
        {hasArchivedContext && !viewingHistoricSession && (
          <div style={{
            display: "flex",
            alignItems: "center",
            gap: "8px",
            padding: "8px 14px",
            borderRadius: "12px",
            backgroundColor: "rgba(156, 39, 176, 0.08)",
            border: "1px solid rgba(156, 39, 176, 0.18)",
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
            border: "1px solid rgba(91, 141, 239, 0.3)",
            background: "rgba(91, 141, 239, 0.12)",
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
