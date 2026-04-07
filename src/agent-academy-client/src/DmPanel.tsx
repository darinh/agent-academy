import { memo, useCallback, useEffect, useRef, useState } from "react";
import type { KeyboardEvent } from "react";
import Markdown from "react-markdown";
import remarkGfm from "remark-gfm";
import {
  Avatar,
  Body1Strong,
  Button,
  makeStyles,
  mergeClasses,
  shorthands,
  Spinner,
  Subtitle2,
  Textarea,
  tokens,
} from "@fluentui/react-components";
import {
  ChatRegular,
  SendRegular,
  AddRegular,
} from "@fluentui/react-icons";
import { roleColor, formatRole } from "./theme";
import { formatTime } from "./utils";
import EmptyState from "./EmptyState";
import ErrorState from "./ErrorState";
import SkeletonLoader from "./SkeletonLoader";
import type { DmThreadSummary, DmMessage } from "./api";
import {
  getDmThreads,
  getDmThreadMessages,
  sendDmToAgent,
} from "./api";

// ── Styles ──────────────────────────────────────────────────────────────

const useLocalStyles = makeStyles({
  root: {
    display: "flex",
    height: "100%",
    overflow: "hidden",
  },
  sidebar: {
    width: "220px",
    minWidth: "220px",
    borderRight: "1px solid var(--aa-border)",
    display: "flex",
    flexDirection: "column",
    overflow: "hidden",
    background: "var(--aa-panel)",
  },
  sidebarHeader: {
    display: "flex",
    alignItems: "center",
    justifyContent: "space-between",
    ...shorthands.padding("8px", "12px"),
    borderBottom: "1px solid var(--aa-border)",
  },
  threadList: {
    flex: 1,
    overflowY: "auto",
    ...shorthands.padding("4px", "0"),
  },
  threadItem: {
    display: "flex",
    alignItems: "center",
    gap: "8px",
    ...shorthands.padding("8px", "12px"),
    cursor: "pointer",
    border: "none",
    background: "transparent",
    width: "100%",
    textAlign: "left",
    color: "inherit",
    transitionProperty: "background",
    transitionDuration: "0.1s",
    "&:hover": {
      background: "rgba(91, 141, 239, 0.06)",
    },
  },
  threadItemSelected: {
    background: "rgba(91, 141, 239, 0.12)",
  },
  threadInfo: {
    flex: 1,
    minWidth: 0,
    display: "flex",
    flexDirection: "column",
    gap: "2px",
  },
  threadNameRow: {
    display: "flex",
    alignItems: "center",
    gap: "8px",
  },
  threadPreview: {
    overflow: "hidden",
    textOverflow: "ellipsis",
    whiteSpace: "nowrap",
    color: "var(--aa-soft)",
    fontSize: "10px",
  },
  threadTime: {
    fontSize: "10px",
    color: "var(--aa-soft)",
    whiteSpace: "nowrap",
    fontFamily: "var(--mono)",
  },
  chatArea: {
    flex: 1,
    display: "flex",
    flexDirection: "column",
    overflow: "hidden",
  },
  chatHeader: {
    display: "flex",
    alignItems: "center",
    gap: "10px",
    ...shorthands.padding("8px", "16px"),
    borderBottom: "1px solid var(--aa-border)",
  },
  messageList: {
    flex: 1,
    overflowY: "auto",
    ...shorthands.padding("14px", "20px"),
    display: "flex",
    flexDirection: "column",
    gap: "12px",
  },
  msgRow: {
    display: "flex",
    gap: "10px",
    maxWidth: "85%",
  },
  msgRowHuman: {
    alignSelf: "flex-end",
    flexDirection: "row-reverse",
  },
  msgBubble: {
    ...shorthands.padding("8px", "12px"),
    ...shorthands.borderRadius("8px"),
    backgroundColor: "var(--aa-panel)",
    border: "1px solid var(--aa-border)",
    maxWidth: "100%",
  },
  msgBubbleHuman: {
    backgroundColor: "rgba(91, 141, 239, 0.15)",
    ...shorthands.borderColor("rgba(91, 141, 239, 0.3)"),
    color: "var(--aa-text)",
  },
  msgMeta: {
    display: "flex",
    alignItems: "center",
    gap: "6px",
    marginBottom: "2px",
  },
  msgTime: {
    fontSize: "10px",
    color: "var(--aa-soft)",
    fontFamily: "var(--mono)",
    marginTop: "4px",
  },
  msgTimeHuman: {
    textAlign: "right",
    color: "var(--aa-soft)",
  },
  msgContent: {
    fontFamily: "var(--mono)",
    fontSize: "13px",
    lineHeight: 1.6,
    "& p": { margin: 0 },
    "& pre": {
      backgroundColor: "var(--aa-bg)",
      ...shorthands.padding("8px"),
      ...shorthands.borderRadius("4px"),
      overflowX: "auto",
      fontSize: "12px",
    },
    "& code": {
      fontSize: "12px",
    },
  },
  composer: {
    borderTop: "1px solid var(--aa-border)",
    background: "var(--aa-panel)",
    ...shorthands.padding("10px", "16px"),
    display: "flex",
    gap: "8px",
    alignItems: "flex-end",
  },
  composerInput: {
    flex: 1,
  },
  emptyState: {
    display: "flex",
    flexDirection: "column",
    alignItems: "center",
    justifyContent: "center",
    height: "100%",
    gap: "12px",
    color: "var(--aa-soft)",
    ...shorthands.padding("24px"),
    textAlign: "center",
  },
  emptyIcon: {
    fontSize: "26px",
    opacity: 0.5,
  },
  rolePill: {
    fontFamily: "var(--mono)",
    fontSize: "9px",
    fontWeight: 600,
    ...shorthands.borderRadius("3px"),
    ...shorthands.padding("3px", "6px", "2px"),
    textTransform: "uppercase",
    lineHeight: "1",
  },
  agentDropdown: {
    position: "absolute",
    top: "100%",
    right: 0,
    zIndex: 100,
    backgroundColor: "var(--aa-panel-alt)",
    border: "1px solid var(--aa-border)",
    ...shorthands.borderRadius("6px"),
    boxShadow: "var(--aa-shadow)",
    minWidth: "200px",
    ...shorthands.padding("4px", "0"),
    marginTop: "4px",
  },
  agentOption: {
    display: "flex",
    alignItems: "center",
    gap: "8px",
    ...shorthands.padding("8px", "12px"),
    cursor: "pointer",
    border: "none",
    background: "transparent",
    width: "100%",
    textAlign: "left",
    color: "inherit",
    "&:hover": {
      background: "rgba(91, 141, 239, 0.06)",
    },
  },
  newMsgWrapper: {
    position: "relative",
  },
  limitedModeNotice: {
    borderTop: "1px solid var(--aa-border)",
    ...shorthands.padding("12px", "16px"),
    color: "var(--aa-gold)",
    backgroundColor: "rgba(255, 152, 0, 0.06)",
    fontSize: "12px",
    lineHeight: 1.6,
  },
});

// ── Types ────────────────────────────────────────────────────────────────

interface AgentInfo {
  id: string;
  name: string;
  role: string;
}

interface DmPanelProps {
  agents: AgentInfo[];
  readOnly?: boolean;
}

// ── Component ────────────────────────────────────────────────────────────

export default function DmPanel({ agents, readOnly = false }: DmPanelProps) {
  const s = useLocalStyles();

  const [threads, setThreads] = useState<DmThreadSummary[]>([]);
  const [selectedAgentId, setSelectedAgentId] = useState<string | null>(null);
  const [messages, setMessages] = useState<DmMessage[]>([]);
  const [input, setInput] = useState("");
  const [sending, setSending] = useState(false);
  const [showAgentPicker, setShowAgentPicker] = useState(false);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState(false);

  const scrollRef = useRef<HTMLDivElement>(null);
  const pickerRef = useRef<HTMLDivElement>(null);

  // Load threads
  const refreshThreads = useCallback(async () => {
    try {
      const data = await getDmThreads();
      setThreads(data);
      setLoadError(false);
    } catch {
      setLoadError(true);
    }
  }, []);

  useEffect(() => {
    let active = true;

    void refreshThreads().finally(() => {
      if (active) {
        setLoading(false);
      }
    });

    if (readOnly) {
      return () => {
        active = false;
      };
    }

    const interval = setInterval(() => void refreshThreads(), 10000);
    return () => {
      active = false;
      clearInterval(interval);
    };
  }, [readOnly, refreshThreads]);

  // Load messages when thread selected
  const refreshMessages = useCallback(async (agentId: string) => {
    try {
      const data = await getDmThreadMessages(agentId);
      setMessages(data);
    } catch {
      setMessages([]);
    }
  }, []);

  useEffect(() => {
    if (!selectedAgentId) return;
    void refreshMessages(selectedAgentId);
    if (readOnly) {
      return;
    }

    const interval = setInterval(() => void refreshMessages(selectedAgentId), 3000);
    return () => clearInterval(interval);
  }, [readOnly, selectedAgentId, refreshMessages]);

  // Auto-scroll
  useEffect(() => {
    const el = scrollRef.current;
    if (el) el.scrollTo({ top: el.scrollHeight, behavior: "auto" });
  }, [messages.length]);

  // Close picker on outside click
  useEffect(() => {
    if (!showAgentPicker) return;
    const handler = (e: MouseEvent) => {
      if (pickerRef.current && !pickerRef.current.contains(e.target as Node)) {
        setShowAgentPicker(false);
      }
    };
    document.addEventListener("mousedown", handler);
    return () => document.removeEventListener("mousedown", handler);
  }, [showAgentPicker]);

  const selectThread = useCallback((agentId: string) => {
    setSelectedAgentId(agentId);
    setMessages([]);
    void refreshMessages(agentId);
  }, [refreshMessages]);

  const startNewThread = useCallback((agentId: string) => {
    if (readOnly) return;
    setSelectedAgentId(agentId);
    setMessages([]);
    setShowAgentPicker(false);
    void refreshMessages(agentId);
  }, [readOnly, refreshMessages]);

  const doSend = useCallback(async () => {
    if (readOnly || !selectedAgentId || !input.trim()) return;
    setSending(true);
    try {
      await sendDmToAgent(selectedAgentId, input.trim());
      setInput("");
      void refreshMessages(selectedAgentId);
      void refreshThreads();
    } finally {
      setSending(false);
    }
  }, [readOnly, selectedAgentId, input, refreshMessages, refreshThreads]);

  const handleKeyDown = useCallback(
    (event: KeyboardEvent<HTMLTextAreaElement>) => {
      if (event.key === "Enter" && !event.shiftKey) {
        event.preventDefault();
        void doSend();
      }
    },
    [doSend],
  );

  const selectedAgent = agents.find((a) => a.id === selectedAgentId);
  const agentsWithoutThread = agents.filter(
    (a) => !threads.some((t) => t.agentId === a.id),
  );

  if (loading) {
    return <SkeletonLoader rows={4} variant="list" />;
  }

  if (loadError && threads.length === 0) {
    return (
      <ErrorState
        message="Failed to load conversations"
        detail="Could not retrieve direct message threads."
        onRetry={() => { setLoading(true); setLoadError(false); void refreshThreads().finally(() => setLoading(false)); }}
      />
    );
  }

  return (
    <div className={s.root}>
      {/* ── Conversation list ──────────────── */}
      <div className={s.sidebar}>
        <div className={s.sidebarHeader}>
          <Subtitle2>Messages</Subtitle2>
          <div className={s.newMsgWrapper} ref={pickerRef}>
            <Button
              appearance="subtle"
              icon={<AddRegular />}
              size="small"
              onClick={() => setShowAgentPicker((v) => !v)}
              title="New conversation"
              disabled={readOnly}
            />
            {showAgentPicker && (
              <div className={s.agentDropdown}>
                {agentsWithoutThread.length === 0 ? (
                  <div style={{ padding: "8px 12px", color: tokens.colorNeutralForeground3, fontSize: "13px" }}>
                    All agents have threads
                  </div>
                ) : (
                  agentsWithoutThread.map((agent) => {
                    const colors = roleColor(agent.role);
                    return (
                      <button
                        key={agent.id}
                        className={s.agentOption}
                        onClick={() => startNewThread(agent.id)}
                        type="button"
                      >
                        <Avatar
                          name={agent.name}
                          size={28}
                          style={{ backgroundColor: colors.accent, color: colors.foreground }}
                        />
                        <div>
                          <Body1Strong style={{ fontSize: "13px" }}>{agent.name}</Body1Strong>
                          <div style={{ fontSize: "11px", color: tokens.colorNeutralForeground3 }}>
                            {formatRole(agent.role)}
                          </div>
                        </div>
                      </button>
                    );
                  })
                )}
              </div>
            )}
          </div>
        </div>

        <div className={s.threadList}>
          {threads.length === 0 && !loading ? (
            <EmptyState
              icon={<ChatRegular />}
              title="No conversations yet"
              detail="Click + above to start a direct message with an agent."
            />
          ) : (
            threads.map((thread) => {
              const colors = roleColor(thread.agentRole);
              return (
                <button
                  key={thread.agentId}
                  className={mergeClasses(
                    s.threadItem,
                    selectedAgentId === thread.agentId && s.threadItemSelected,
                  )}
                  onClick={() => selectThread(thread.agentId)}
                  type="button"
                >
                  <Avatar
                    name={thread.agentName}
                    size={36}
                    style={{ backgroundColor: colors.accent, color: colors.foreground }}
                  />
                  <div className={s.threadInfo}>
                    <div className={s.threadNameRow}>
                      <Body1Strong style={{ fontSize: "13px" }}>
                        {thread.agentName}
                      </Body1Strong>
                      <span
                        className={s.rolePill}
                        style={{ backgroundColor: colors.accent, color: colors.foreground }}
                      >
                        {formatRole(thread.agentRole)}
                      </span>
                    </div>
                    <div className={s.threadPreview}>{thread.lastMessage}</div>
                  </div>
                  <span className={s.threadTime}>
                    {formatTime(thread.lastMessageAt)}
                  </span>
                </button>
              );
            })
          )}
        </div>
      </div>

      {/* ── Chat area ──────────────────────── */}
      <div className={s.chatArea}>
        {selectedAgent ? (
          <>
            <div className={s.chatHeader}>
              <Avatar
                name={selectedAgent.name}
                size={32}
                style={{
                  backgroundColor: roleColor(selectedAgent.role).accent,
                  color: roleColor(selectedAgent.role).foreground,
                }}
              />
              <div>
                <Body1Strong>{selectedAgent.name}</Body1Strong>
                <div style={{ fontSize: "12px", color: tokens.colorNeutralForeground3 }}>
                  {formatRole(selectedAgent.role)}
                </div>
              </div>
            </div>

            <div ref={scrollRef} className={s.messageList} role="log" aria-label="Direct messages">
              {messages.length === 0 ? (
                <EmptyState
                  icon={<ChatRegular />}
                  title="No messages yet"
                  detail="Send a message to start the conversation."
                />
              ) : (
                messages.map((msg) => (
                  <DmMessageBubble key={msg.id} message={msg} />
                ))
              )}
            </div>

            {readOnly ? (
              <div className={s.limitedModeNotice}>
                Limited mode is active. Existing direct messages stay readable, but new DMs remain paused until
                Copilot returns to operational.
              </div>
            ) : (
              <div className={s.composer}>
                <Textarea
                  className={s.composerInput}
                  appearance="filled-darker"
                  placeholder={sending ? "Sending…" : `Message ${selectedAgent.name}…`}
                  value={input}
                  onChange={(_, d) => setInput(d.value)}
                  onKeyDown={handleKeyDown}
                  resize="vertical"
                  rows={2}
                  disabled={sending}
                  aria-label={`Message ${selectedAgent.name}`}
                />
                <Button
                  appearance="primary"
                  icon={sending ? <Spinner size="tiny" /> : <SendRegular />}
                  onClick={() => void doSend()}
                  disabled={!input.trim() || sending}
                  title="Send"
                />
              </div>
            )}
          </>
        ) : (
          <EmptyState
            icon={<ChatRegular />}
            title="Direct Messages"
            detail="Select a conversation or click + to message an agent directly."
          />
        )}
      </div>
    </div>
  );
}

// ── Message bubble ──────────────────────────────────────────────────────

const DmMessageBubble = memo(function DmMessageBubble({
  message,
}: {
  message: DmMessage;
}) {
  const s = useLocalStyles();
  const isHuman = message.isFromHuman;

  return (
    <div className={mergeClasses(s.msgRow, isHuman && s.msgRowHuman)}>
      {!isHuman && (
        <Avatar
          name={message.senderName}
          size={32}
          style={{
            backgroundColor: roleColor("Agent").accent,
            color: roleColor("Agent").foreground,
          }}
        />
      )}
      <div>
        <div className={mergeClasses(s.msgBubble, isHuman && s.msgBubbleHuman)}>
          {!isHuman && (
            <div className={s.msgMeta}>
              <Body1Strong style={{ fontSize: "13px" }}>
                {message.senderName}
              </Body1Strong>
            </div>
          )}
          <div className={s.msgContent}>
            <Markdown remarkPlugins={[remarkGfm]}>{message.content}</Markdown>
          </div>
        </div>
        <div className={mergeClasses(s.msgTime, isHuman && s.msgTimeHuman)}>
          {formatTime(message.sentAt)}
        </div>
      </div>
    </div>
  );
});
