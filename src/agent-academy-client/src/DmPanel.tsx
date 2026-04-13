import { useCallback, useEffect, useRef, useState } from "react";
import type { KeyboardEvent } from "react";
import {
  Button,
  mergeClasses,
  Spinner,
  Textarea,
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
import { DmMessageBubble, useDmPanelStyles } from "./dm";

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

function initials(name: string): string {
  return name.split(/\s+/).map(w => w[0]).join("").toUpperCase().slice(0, 2);
}

// ── Component ────────────────────────────────────────────────────────────

export default function DmPanel({ agents, readOnly = false }: DmPanelProps) {
  const s = useDmPanelStyles();

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
    } catch {
      // Send failed — input is preserved so the user can retry
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
          <span style={{ fontWeight: 600, fontSize: "14px" }}>Messages</span>
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
                  <div style={{ padding: "8px 12px", color: "var(--aa-soft)", fontSize: "13px" }}>
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
                        <span
                          className={mergeClasses(s.avatar, s.avatarSm)}
                          style={{ backgroundColor: colors.accent }}
                        >
                          {initials(agent.name)}
                        </span>
                        <div>
                          <span style={{ fontWeight: 600, fontSize: "13px" }}>{agent.name}</span>
                          <div style={{ fontSize: "11px", color: "var(--aa-soft)" }}>
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
                  <div className={s.threadInfo}>
                    <div className={s.threadNameRow}>
                      <span style={{ fontWeight: 600, fontSize: "13px" }}>
                        {thread.agentName}
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
              <span style={{ fontWeight: 600 }}>{selectedAgent.name}</span>
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
