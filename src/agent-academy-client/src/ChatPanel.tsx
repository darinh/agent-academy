import { memo, useCallback, useEffect, useMemo, useRef, useState } from "react";
import type { KeyboardEvent } from "react";
import Markdown from "react-markdown";
import remarkGfm from "remark-gfm";
import {
  Avatar,
  Body1,
  Body1Strong,
  Badge,
  Button,
  Menu,
  MenuItemCheckbox,
  MenuList,
  MenuPopover,
  MenuTrigger,
  mergeClasses,
  Spinner,
  Textarea,
} from "@fluentui/react-components";
import type { MenuCheckedValueChangeData } from "@fluentui/react-components";
import { useStyles } from "./useStyles";
import { formatRole, roleColor } from "./theme";
import { formatTime } from "./utils";
import type { ChatEnvelope, RoomSnapshot } from "./api";
import { clearChatDraft, loadChatDraft, saveChatDraft } from "./recovery";
import type { ThinkingAgent } from "./useWorkspace";
import type { ConnectionStatus } from "./useActivityHub";

/* ── Command Result Helpers ─────────────────────────────────────── */

interface ParsedCommandResult {
  status: "Success" | "Error" | "Denied";
  command: string;
  correlationId: string;
  error?: string;
  detail?: string;
}

function isCommandResultMessage(content: string): boolean {
  return content.startsWith("=== COMMAND RESULTS ===");
}

function parseCommandResults(content: string): ParsedCommandResult[] {
  const results: ParsedCommandResult[] = [];
  const lines = content.split("\n");

  let current: ParsedCommandResult | null = null;
  const detailLines: string[] = [];

  const flushCurrent = () => {
    if (current) {
      if (detailLines.length > 0) current.detail = detailLines.join("\n").trim();
      results.push(current);
      detailLines.length = 0;
    }
  };

  for (const line of lines) {
    if (line.startsWith("=== ")) continue;

    const statusMatch = line.match(/^\[(Success|Error|Denied)\]\s+(\S+)\s+\(([^)]+)\)/);
    if (statusMatch) {
      flushCurrent();
      current = {
        status: statusMatch[1] as ParsedCommandResult["status"],
        command: statusMatch[2],
        correlationId: statusMatch[3],
      };
      continue;
    }

    if (!current) continue;

    if (line.startsWith("  Error: ")) {
      current.error = line.replace("  Error: ", "");
    } else {
      // Capture all remaining lines as detail (strip leading 2-space indent if present)
      detailLines.push(line.startsWith("  ") ? line.slice(2) : line);
    }
  }
  flushCurrent();

  return results;
}

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

/* ── Filter Helpers ──────────────────────────────────────────────── */

type MessageFilter = "system" | "commands";
const FILTER_STORAGE_KEY = "agent-academy-chat-filters";

function loadFilters(): Set<MessageFilter> {
  try {
    const raw = localStorage.getItem(FILTER_STORAGE_KEY);
    if (raw) return new Set(JSON.parse(raw) as MessageFilter[]);
  } catch { /* ignore */ }
  return new Set();
}

function saveFilters(filters: Set<MessageFilter>) {
  try {
    localStorage.setItem(FILTER_STORAGE_KEY, JSON.stringify([...filters]));
  } catch { /* storage unavailable — filter state lives in memory only */ }
}

function shouldHideMessage(msg: ChatEnvelope, hidden: Set<MessageFilter>): boolean {
  if (msg.senderKind !== "System") return false;
  const isCmdResult = isCommandResultMessage(msg.content);
  if (isCmdResult && hidden.has("commands")) return true;
  if (!isCmdResult && hidden.has("system")) return true;
  return false;
}

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
  const [hiddenFilters, setHiddenFilters] = useState<Set<MessageFilter>>(loadFilters);
  const room = props.room;
  const readOnly = props.readOnly ?? false;
  const onSendMessage = props.onSendMessage;
  const thinkingAgents = props.thinkingAgents;
  const connectionStatus = props.connectionStatus;

  // Filters use checkedValues where checked = visible (not hidden)
  const checkedValues = useMemo(() => {
    const visible: string[] = [];
    if (!hiddenFilters.has("system")) visible.push("system");
    if (!hiddenFilters.has("commands")) visible.push("commands");
    return { show: visible };
  }, [hiddenFilters]);

  const onFilterChange = useCallback(
    (_: unknown, data: MenuCheckedValueChangeData) => {
      const nowVisible = new Set(data.checkedItems);
      const next = new Set<MessageFilter>();
      if (!nowVisible.has("system")) next.add("system");
      if (!nowVisible.has("commands")) next.add("commands");
      setHiddenFilters(next);
      saveFilters(next);
    },
    [],
  );

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

  useEffect(() => {
    if (!room || readOnly) {
      return;
    }

    saveChatDraft(room.id, humanMsg);
  }, [humanMsg, readOnly, room]);

  useEffect(() => {
    const el = scrollRef.current;
    if (el) el.scrollTo({ top: el.scrollHeight, behavior: "auto" });
  }, [filteredMessages.length, room?.id, thinkingAgents.length]);

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

  const hiddenCount = (room?.recentMessages.length ?? 0) - filteredMessages.length;

  return (
    <div className={s.conversationLayout}>
      <div className={s.chatHeader}>
        <Menu checkedValues={checkedValues} onCheckedValueChange={onFilterChange}>
          <MenuTrigger disableButtonEnhancement>
            <Button size="small" appearance="subtle" className={s.filterMenuButton}>
              Filter{hiddenCount > 0 && <Badge size="small" appearance="filled" color="informative" className={s.filterBadge}>{hiddenCount}</Badge>}
            </Button>
          </MenuTrigger>
          <MenuPopover>
            <MenuList>
              <MenuItemCheckbox name="show" value="system">
                System messages
              </MenuItemCheckbox>
              <MenuItemCheckbox name="show" value="commands">
                Command results
              </MenuItemCheckbox>
            </MenuList>
          </MenuPopover>
        </Menu>
      </div>

       <div ref={scrollRef} className={s.messageList} role="log" aria-label="Conversation messages" aria-live="polite">
        {filteredMessages.length ? (
          filteredMessages.map((msg) => (
            <MessageBubble key={msg.id} message={msg} expanded={expandedMsgs.has(msg.id)} onToggle={toggleExpand} />
          ))
        ) : (
          <Body1 className={s.emptyState}>
            {room?.recentMessages.length ? "All messages filtered." : "No messages yet for this room."}
          </Body1>
        )}
        {thinkingAgents.map((agent) => (
          <ThinkingBubble key={agent.id} agent={agent} />
        ))}
      </div>

      <div className={s.statusBar}>
        <span className={s.statusIndicator} style={{ backgroundColor: STATUS_COLORS[connectionStatus] }} />
        {STATUS_LABELS[connectionStatus]}
      </div>

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
