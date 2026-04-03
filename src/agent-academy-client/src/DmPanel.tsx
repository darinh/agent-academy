import { memo, useCallback, useEffect, useRef, useState } from "react";
import type { KeyboardEvent } from "react";
import Markdown from "react-markdown";
import remarkGfm from "remark-gfm";
import {
  Avatar,
  Body1,
  Body1Strong,
  Button,
  Caption1,
  makeStyles,
  mergeClasses,
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
    width: "280px",
    minWidth: "280px",
    borderRight: `1px solid ${tokens.colorNeutralStroke2}`,
    display: "flex",
    flexDirection: "column",
    overflow: "hidden",
  },
  sidebarHeader: {
    display: "flex",
    alignItems: "center",
    justifyContent: "space-between",
    padding: "12px 16px",
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  threadList: {
    flex: 1,
    overflowY: "auto",
    padding: "4px 0",
  },
  threadItem: {
    display: "flex",
    alignItems: "center",
    gap: "12px",
    padding: "10px 16px",
    cursor: "pointer",
    border: "none",
    background: "transparent",
    width: "100%",
    textAlign: "left",
    color: "inherit",
    "&:hover": {
      backgroundColor: tokens.colorNeutralBackground1Hover,
    },
  },
  threadItemSelected: {
    backgroundColor: tokens.colorNeutralBackground1Selected,
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
    color: tokens.colorNeutralForeground3,
    fontSize: "12px",
  },
  threadTime: {
    fontSize: "11px",
    color: tokens.colorNeutralForeground3,
    whiteSpace: "nowrap",
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
    gap: "12px",
    padding: "12px 16px",
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  messageList: {
    flex: 1,
    overflowY: "auto",
    padding: "16px",
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
    padding: "8px 12px",
    borderRadius: "8px",
    backgroundColor: tokens.colorNeutralBackground3,
    maxWidth: "100%",
  },
  msgBubbleHuman: {
    backgroundColor: tokens.colorBrandBackground,
    color: tokens.colorNeutralForegroundOnBrand,
  },
  msgMeta: {
    display: "flex",
    alignItems: "center",
    gap: "6px",
    marginBottom: "2px",
  },
  msgTime: {
    fontSize: "11px",
    color: tokens.colorNeutralForeground3,
    marginTop: "4px",
  },
  msgTimeHuman: {
    textAlign: "right",
    color: tokens.colorNeutralForegroundOnBrand,
    opacity: 0.7,
  },
  msgContent: {
    "& p": { margin: 0 },
    "& pre": {
      backgroundColor: tokens.colorNeutralBackground1,
      padding: "8px",
      borderRadius: "4px",
      overflowX: "auto",
      fontSize: "13px",
    },
    "& code": {
      fontSize: "13px",
    },
  },
  composer: {
    borderTop: `1px solid ${tokens.colorNeutralStroke2}`,
    padding: "12px 16px",
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
    color: tokens.colorNeutralForeground3,
    padding: "24px",
    textAlign: "center",
  },
  emptyIcon: {
    fontSize: "48px",
    opacity: 0.5,
  },
  rolePill: {
    fontSize: "10px",
    fontWeight: 600,
    borderRadius: "4px",
    padding: "1px 6px",
    textTransform: "uppercase",
    letterSpacing: "0.5px",
  },
  agentDropdown: {
    position: "absolute",
    top: "100%",
    right: 0,
    zIndex: 100,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke1}`,
    borderRadius: "8px",
    boxShadow: tokens.shadow16,
    minWidth: "200px",
    padding: "4px 0",
    marginTop: "4px",
  },
  agentOption: {
    display: "flex",
    alignItems: "center",
    gap: "8px",
    padding: "8px 12px",
    cursor: "pointer",
    border: "none",
    background: "transparent",
    width: "100%",
    textAlign: "left",
    color: "inherit",
    "&:hover": {
      backgroundColor: tokens.colorNeutralBackground1Hover,
    },
  },
  newMsgWrapper: {
    position: "relative",
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

  const scrollRef = useRef<HTMLDivElement>(null);
  const pickerRef = useRef<HTMLDivElement>(null);

  // Load threads
  const refreshThreads = useCallback(async () => {
    try {
      const data = await getDmThreads();
      setThreads(data);
    } catch {
      // silently fail
    }
  }, []);

  useEffect(() => {
    void refreshThreads().then(() => setLoading(false));
    const interval = setInterval(() => void refreshThreads(), 10000);
    return () => clearInterval(interval);
  }, [refreshThreads]);

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
    const interval = setInterval(() => void refreshMessages(selectedAgentId), 3000);
    return () => clearInterval(interval);
  }, [selectedAgentId, refreshMessages]);

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
    return (
      <div className={s.emptyState}>
        <Spinner size="small" />
        <Body1>Loading messages…</Body1>
      </div>
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
            <div className={s.emptyState} style={{ height: "auto", padding: "24px 16px" }}>
              <Caption1>No conversations yet</Caption1>
            </div>
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
                <div className={s.emptyState}>
                  <ChatRegular className={s.emptyIcon} />
                  <Body1>Send a message to start the conversation</Body1>
                </div>
              ) : (
                messages.map((msg) => (
                  <DmMessageBubble key={msg.id} message={msg} />
                ))
              )}
            </div>

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
                disabled={sending || readOnly}
                aria-label={`Message ${selectedAgent.name}`}
              />
              <Button
                appearance="primary"
                icon={sending ? <Spinner size="tiny" /> : <SendRegular />}
                onClick={() => void doSend()}
                disabled={!input.trim() || sending || readOnly}
                title="Send"
              />
            </div>
          </>
        ) : (
          <div className={s.emptyState}>
            <ChatRegular className={s.emptyIcon} />
            <Subtitle2>Direct Messages</Subtitle2>
            <Body1>
              Select a conversation or click + to message an agent directly.
            </Body1>
          </div>
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
