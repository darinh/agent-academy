import { memo, useCallback, useEffect, useMemo, useRef, useState } from "react";
import type { KeyboardEvent } from "react";
import {
  Button,
  Spinner,
  Textarea,
} from "@fluentui/react-components";
import {
  CheckmarkCircleRegular,
  ArrowSyncRegular,
  PlugDisconnectedRegular,
  WifiSettingsRegular,
} from "@fluentui/react-icons";
import { useChatStyles } from "../styles";
import type { ChatEnvelope, RoomSnapshot, AgentLocation, AgentDefinition, ConversationSessionSnapshot } from "../api";
import { getRoomSessions, getRoomMessages } from "../api";
import { clearChatDraft, loadChatDraft, saveChatDraft } from "../recovery";
import EmptyState from "../EmptyState";
import SkeletonLoader from "../SkeletonLoader";
import type { ThinkingAgent } from "../useWorkspace";
import {
  loadFilters,
  shouldHideMessage,
  STATUS_LABELS,
  STATUS_COLORS,
} from "../chatUtils";
import type { MessageFilter, ConnectionStatus } from "../chatUtils";
import { MessageBubble } from "./MessageBubble";
import { ThinkingBubble } from "./ThinkingBubble";
import { SessionToolbar } from "./SessionToolbar";

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

  // Expand state: defaultExpanded determines the base state for all messages.
  // overrides tracks messages the user has manually toggled to the opposite.
  const [defaultExpanded, setDefaultExpanded] = useState(() => {
    try { return localStorage.getItem("aa-default-expand") === "true"; } catch { return false; }
  });
  const [overrides, setOverrides] = useState<Set<string>>(new Set());

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
  const currentRoomIdRef = useRef(room?.id ?? null);
  currentRoomIdRef.current = room?.id ?? null;

  // Session management
  const [sessions, setSessions] = useState<ConversationSessionSnapshot[]>([]);
  const [selectedSessionId, setSelectedSessionId] = useState<string | null>(null);
  const [sessionMessages, setSessionMessages] = useState<ChatEnvelope[] | null>(null);
  const [sessionLoadError, setSessionLoadError] = useState<string | null>(null);

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
    setOverrides(new Set());
  }, [readOnly, room?.id]); // eslint-disable-line react-hooks/exhaustive-deps

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
    if (!room) { setSessions([]); setSelectedSessionId(null); setSessionMessages(null); setSessionLoadError(null); return; }
    let cancelled = false;
    setSessionLoadError(null);
    getRoomSessions(room.id, undefined, 50)
      .then((res) => { if (!cancelled) { setSessions(res.sessions); setSessionLoadError(null); } })
      .catch(() => { if (!cancelled) { setSessions([]); setSessionLoadError("Failed to load sessions"); } });
    setSelectedSessionId(null);
    setSessionMessages(null);
    return () => { cancelled = true; };
  }, [room?.id]); // eslint-disable-line react-hooks/exhaustive-deps

  // Load messages when viewing a non-active session
  useEffect(() => {
    if (!selectedSessionId || !room) { setSessionMessages(null); return; }
    const activeSession = sessions.find(s => s.status === "Active");
    if (activeSession && activeSession.id === selectedSessionId) {
      setSessionMessages(null);
      return;
    }
    let cancelled = false;
    getRoomMessages(room.id, { sessionId: selectedSessionId, limit: 200 })
      .then((res) => { if (!cancelled) { setSessionMessages(res.messages); setSessionLoadError(null); } })
      .catch(() => { if (!cancelled) { setSessionMessages([]); setSessionLoadError("Failed to load session messages"); } });
    return () => { cancelled = true; };
  }, [selectedSessionId, room?.id, sessions]); // eslint-disable-line react-hooks/exhaustive-deps

  const handleNewSession = useCallback(() => {
    if (!room || !props.onCreateSession) return;
    props.onCreateSession(room.id);
    setTimeout(() => {
      getRoomSessions(room.id, undefined, 50)
        .then((res) => { setSessions(res.sessions); setSelectedSessionId(null); setSessionMessages(null); })
        .catch(() => {});
    }, 500);
  }, [room, props.onCreateSession]);

  const handleSessionChange = useCallback((sessionId: string | null) => {
    setSelectedSessionId(sessionId);
  }, []);

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

  // Scroll to bottom on room change only; reset indicator state
  useEffect(() => {
    const el = scrollRef.current;
    if (el) el.scrollTo({ top: el.scrollHeight, behavior: "auto" });
    setShowNewMsgIndicator(false);
    isNearBottomRef.current = true;
    prevMsgCountRef.current = filteredMessages.length;
  }, [room?.id]); // eslint-disable-line react-hooks/exhaustive-deps

  const scrollToBottom = useCallback(() => {
    const el = scrollRef.current;
    if (el) el.scrollTo({ top: el.scrollHeight, behavior: "smooth" });
    setShowNewMsgIndicator(false);
  }, []);

  const isExpanded = useCallback((id: string) => {
    return overrides.has(id) ? !defaultExpanded : defaultExpanded;
  }, [overrides, defaultExpanded]);

  const toggleExpand = useCallback((id: string) => {
    setOverrides((cur) => {
      const next = new Set(cur);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  }, []);

  const handleToggleDefaultExpand = useCallback(() => {
    setDefaultExpanded((prev) => {
      const next = !prev;
      try { localStorage.setItem("aa-default-expand", String(next)); } catch { /* */ }
      return next;
    });
    setOverrides(new Set());
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
  const viewingHistoricSession = selectedSessionId != null && sessionMessages != null;

  return (
    <div className={s.conversationLayout}>
      {room && !readOnly && (
        <SessionToolbar
          roomId={room.id}
          sessions={sessions}
          selectedSessionId={selectedSessionId}
          onSessionChange={handleSessionChange}
          onNewSession={handleNewSession}
          configuredAgents={configuredAgents}
          agentLocations={agentLocations}
          onToggleAgent={props.onToggleAgent}
          onCreateSession={props.onCreateSession}
          defaultExpanded={defaultExpanded}
          onToggleDefaultExpand={handleToggleDefaultExpand}
        />
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
        ) : sessionLoadError ? (
          <div style={{
            display: "flex", flexDirection: "column", alignItems: "center",
            justifyContent: "center", gap: "8px", padding: "24px",
            color: "var(--aa-copper, #dc3545)", fontSize: "13px",
          }}>
            <span>⚠ {sessionLoadError}</span>
            <button onClick={() => {
              setSessionLoadError(null);
              if (room) {
                const retryRoomId = room.id;
                getRoomSessions(retryRoomId, undefined, 50)
                  .then((res) => {
                    if (currentRoomIdRef.current === retryRoomId) setSessions(res.sessions);
                  })
                  .catch(() => {
                    if (currentRoomIdRef.current === retryRoomId) setSessionLoadError("Failed to load sessions");
                  });
              }
            }} style={{
              background: "none", border: "1px solid rgba(220, 53, 69, 0.3)",
              borderRadius: "4px", padding: "4px 12px", cursor: "pointer",
              color: "inherit", fontSize: "12px",
            }}>Retry</button>
          </div>
        ) : filteredMessages.length ? (
          filteredMessages.map((msg) => (
            <MessageBubble key={msg.id} message={msg} expanded={isExpanded(msg.id)} onToggle={toggleExpand} />
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
