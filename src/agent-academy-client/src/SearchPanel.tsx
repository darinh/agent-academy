import { useCallback, useEffect, useRef, useState } from "react";
import {
  Input,
  makeStyles,
  shorthands,
  Spinner,
} from "@fluentui/react-components";
import {
  SearchRegular,
  ChatMultipleRegular,
  TaskListLtrRegular,
  ArrowEnterLeftRegular,
} from "@fluentui/react-icons";
import V3Badge from "./V3Badge";
import type { BadgeColor } from "./V3Badge";
import { roleColor, formatRole } from "./theme";
import { formatTime } from "./utils";
import type { SearchResults, SearchScope, MessageSearchResult, TaskSearchResult } from "./api";
import { searchWorkspace } from "./api";

// ── Styles ──

const useLocalStyles = makeStyles({
  root: {
    display: "flex",
    flexDirection: "column",
    height: "100%",
    overflow: "hidden",
  },
  searchBar: {
    ...shorthands.padding("14px", "20px"),
    borderBottom: "1px solid var(--aa-border)",
    display: "flex",
    alignItems: "center",
    gap: "10px",
  },
  searchInput: {
    flex: 1,
  },
  scopeRow: {
    display: "flex",
    gap: "6px",
    ...shorthands.padding("0", "20px"),
    paddingTop: "10px",
  },
  scopeBtn: {
    background: "none",
    ...shorthands.border("1px", "solid", "var(--aa-border)"),
    ...shorthands.borderRadius("4px"),
    ...shorthands.padding("3px", "9px"),
    color: "var(--aa-muted)",
    cursor: "pointer",
    fontSize: "11px",
    fontWeight: 500,
    ":hover": {
      ...shorthands.borderColor("var(--aa-border-strong)"),
      color: "var(--aa-text)",
    },
  },
  scopeBtnActive: {
    background: "rgba(91, 141, 239, 0.12)",
    ...shorthands.border("1px", "solid", "rgba(91, 141, 239, 0.3)"),
    ...shorthands.borderRadius("4px"),
    ...shorthands.padding("3px", "9px"),
    color: "var(--aa-cyan)",
    cursor: "pointer",
    fontSize: "11px",
    fontWeight: 600,
  },
  body: {
    flex: 1,
    overflowY: "auto",
    ...shorthands.padding("14px", "20px"),
    display: "flex",
    flexDirection: "column",
    gap: "16px",
  },
  section: {
    display: "flex",
    flexDirection: "column",
    gap: "6px",
  },
  sectionHeader: {
    display: "flex",
    alignItems: "center",
    gap: "8px",
    fontSize: "12px",
    fontWeight: 600,
    color: "var(--aa-soft)",
    fontFamily: "var(--mono)",
    letterSpacing: "0.04em",
    textTransform: "uppercase",
  },
  resultCard: {
    ...shorthands.padding("10px", "12px"),
    ...shorthands.borderRadius("6px"),
    border: "1px solid var(--aa-border)",
    backgroundColor: "var(--aa-bg)",
    cursor: "pointer",
    ":hover": {
      ...shorthands.borderColor("var(--aa-border-strong)"),
      backgroundColor: "rgba(91, 141, 239, 0.04)",
    },
  },
  resultHeader: {
    display: "flex",
    alignItems: "center",
    gap: "8px",
    marginBottom: "4px",
  },
  resultSender: {
    fontFamily: "var(--mono)",
    fontSize: "12px",
    fontWeight: 600,
    color: "var(--aa-text)",
  },
  resultMeta: {
    fontSize: "10px",
    color: "var(--aa-soft)",
    fontFamily: "var(--mono)",
  },
  resultSnippet: {
    fontSize: "12px",
    color: "var(--aa-muted)",
    lineHeight: 1.5,
    wordBreak: "break-word",
  },
  highlight: {
    color: "var(--aa-cyan)",
    fontWeight: 600,
  },
  taskTitle: {
    fontFamily: "var(--mono)",
    fontSize: "12px",
    fontWeight: 600,
    color: "var(--aa-text)",
  },
  empty: {
    display: "flex",
    flexDirection: "column",
    alignItems: "center",
    justifyContent: "center",
    height: "100%",
    gap: "12px",
    color: "var(--aa-soft)",
  },
  emptyIcon: {
    fontSize: "40px",
    opacity: 0.4,
  },
  emptyText: {
    fontSize: "13px",
    textAlign: "center",
  },
  statusBar: {
    ...shorthands.padding("6px", "20px"),
    borderTop: "1px solid var(--aa-border)",
    display: "flex",
    alignItems: "center",
    gap: "8px",
    fontSize: "11px",
    color: "var(--aa-soft)",
    fontFamily: "var(--mono)",
  },
  rolePill: {
    fontSize: "9px",
    fontWeight: 600,
    fontFamily: "var(--mono)",
    letterSpacing: "0.03em",
    ...shorthands.padding("1px", "6px"),
    ...shorthands.borderRadius("4px"),
  },
  sourceBadge: {
    fontSize: "9px",
    fontWeight: 600,
    fontFamily: "var(--mono)",
    ...shorthands.padding("1px", "5px"),
    ...shorthands.borderRadius("3px"),
    backgroundColor: "rgba(91, 141, 239, 0.1)",
    color: "var(--aa-cyan)",
  },
});

// ── Helpers ──

const SCOPES: { value: SearchScope; label: string }[] = [
  { value: "all", label: "All" },
  { value: "messages", label: "Messages" },
  { value: "tasks", label: "Tasks" },
];

/** Replace FTS5 snippet markers «…» with React elements. */
function renderSnippet(snippet: string, highlightClass: string): React.ReactNode {
  const parts = snippet.split(/(«[^»]*»)/g);
  return parts.map((part, i) => {
    if (part.startsWith("«") && part.endsWith("»")) {
      return <span key={i} className={highlightClass}>{part.slice(1, -1)}</span>;
    }
    return part;
  });
}

const STATUS_COLOR: Record<string, BadgeColor> = {
  Active: "active",
  InReview: "review",
  AwaitingValidation: "review",
  Approved: "done",
  Completed: "done",
  Blocked: "err",
  Cancelled: "cancel",
  ChangesRequested: "warn",
  Merging: "feat",
  Queued: "muted",
};

// ── Component ──

interface SearchPanelProps {
  onNavigateToRoom?: (roomId: string) => void;
  onNavigateToTasks?: () => void;
}

export default function SearchPanel({ onNavigateToRoom, onNavigateToTasks }: SearchPanelProps) {
  const s = useLocalStyles();
  const [query, setQuery] = useState("");
  const [scope, setScope] = useState<SearchScope>("all");
  const [results, setResults] = useState<SearchResults | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const debounceRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const requestIdRef = useRef(0);
  const inputRef = useRef<HTMLInputElement>(null);

  // Auto-focus the search input on mount
  useEffect(() => {
    inputRef.current?.focus();
  }, []);

  const executeSearch = useCallback(async (q: string, s: SearchScope) => {
    if (!q.trim()) {
      setResults(null);
      return;
    }
    const thisRequest = ++requestIdRef.current;
    setLoading(true);
    setError(null);
    try {
      const r = await searchWorkspace(q.trim(), { scope: s });
      // Only apply results if this is still the latest request
      if (thisRequest === requestIdRef.current) {
        setResults(r);
      }
    } catch (err) {
      if (thisRequest === requestIdRef.current) {
        setError(err instanceof Error ? err.message : "Search failed");
      }
    } finally {
      if (thisRequest === requestIdRef.current) {
        setLoading(false);
      }
    }
  }, []);

  // Debounced search on query/scope change
  useEffect(() => {
    if (debounceRef.current) clearTimeout(debounceRef.current);
    debounceRef.current = setTimeout(() => executeSearch(query, scope), 300);
    return () => { if (debounceRef.current) clearTimeout(debounceRef.current); };
  }, [query, scope, executeSearch]);

  const handleMessageClick = (msg: MessageSearchResult) => {
    onNavigateToRoom?.(msg.roomId);
  };

  const handleTaskClick = (_task: TaskSearchResult) => {
    onNavigateToTasks?.();
  };

  const showMessages = scope !== "tasks" && results && results.messages.length > 0;
  const showTasks = scope !== "messages" && results && results.tasks.length > 0;
  const noResults = results && results.totalCount === 0 && query.trim();

  return (
    <div className={s.root}>
      <div className={s.searchBar}>
        <SearchRegular style={{ fontSize: 20, color: "var(--aa-soft)" }} />
        <Input
          ref={inputRef}
          className={s.searchInput}
          placeholder="Search messages, tasks, and agent conversations…"
          value={query}
          onChange={(_e, d) => setQuery(d.value)}
          size="medium"
          appearance="filled-darker"
          contentAfter={loading ? <Spinner size="tiny" /> : undefined}
        />
      </div>

      <div className={s.scopeRow}>
        {SCOPES.map((sc) => (
          <button
            key={sc.value}
            className={scope === sc.value ? s.scopeBtnActive : s.scopeBtn}
            onClick={() => setScope(sc.value)}
          >
            {sc.label}
          </button>
        ))}
      </div>

      <div className={s.body}>
        {error && (
          <div style={{ color: "var(--aa-err)", fontSize: 12 }}>{error}</div>
        )}

        {!query.trim() && !results && (
          <div className={s.empty}>
            <SearchRegular className={s.emptyIcon} />
            <div className={s.emptyText}>
              Search across all room messages and tasks.<br />
              Results include main rooms and agent breakout conversations.
            </div>
          </div>
        )}

        {noResults && (
          <div className={s.empty}>
            <SearchRegular className={s.emptyIcon} />
            <div className={s.emptyText}>
              No results for <strong>"{query.trim()}"</strong>
            </div>
          </div>
        )}

        {showMessages && (
          <div className={s.section}>
            <div className={s.sectionHeader}>
              <ChatMultipleRegular style={{ fontSize: 16 }} />
              Messages ({results.messages.length})
            </div>
            {results.messages.map((msg) => {
              const colors = roleColor(msg.senderRole ?? (msg.senderKind === "User" ? "Human" : undefined));
              return (
                <div
                  key={msg.messageId}
                  className={s.resultCard}
                  onClick={() => handleMessageClick(msg)}
                  role="button"
                  tabIndex={0}
                  onKeyDown={(e) => e.key === "Enter" && handleMessageClick(msg)}
                >
                  <div className={s.resultHeader}>
                    <span className={s.resultSender}>{msg.senderName}</span>
                    <span
                      className={s.rolePill}
                      style={{ backgroundColor: colors.accent + "26", color: colors.accent }}
                    >
                      {formatRole(msg.senderRole ?? (msg.senderKind === "User" ? "Human" : "Agent"))}
                    </span>
                    <span className={s.resultMeta}>{msg.roomName}</span>
                    {msg.source === "breakout" && (
                      <span className={s.sourceBadge}>breakout</span>
                    )}
                    <span className={s.resultMeta}>{formatTime(msg.sentAt)}</span>
                  </div>
                  <div className={s.resultSnippet}>
                    {renderSnippet(msg.snippet, s.highlight)}
                  </div>
                </div>
              );
            })}
          </div>
        )}

        {showTasks && (
          <div className={s.section}>
            <div className={s.sectionHeader}>
              <TaskListLtrRegular style={{ fontSize: 16 }} />
              Tasks ({results.tasks.length})
            </div>
            {results.tasks.map((task) => (
              <div
                key={task.taskId}
                className={s.resultCard}
                onClick={() => handleTaskClick(task)}
                role="button"
                tabIndex={0}
                onKeyDown={(e) => e.key === "Enter" && handleTaskClick(task)}
              >
                <div className={s.resultHeader}>
                  <span className={s.taskTitle}>{task.title}</span>
                  <V3Badge color={STATUS_COLOR[task.status] ?? "muted"}>
                    {task.status}
                  </V3Badge>
                  {task.assignedAgentName && (
                    <span className={s.resultMeta}>
                      <ArrowEnterLeftRegular style={{ fontSize: 10 }} /> {task.assignedAgentName}
                    </span>
                  )}
                </div>
                <div className={s.resultSnippet}>
                  {renderSnippet(task.snippet, s.highlight)}
                </div>
              </div>
            ))}
          </div>
        )}
      </div>

      {results && results.totalCount > 0 && (
        <div className={s.statusBar}>
          {results.totalCount} result{results.totalCount !== 1 ? "s" : ""} for "{results.query}"
        </div>
      )}
    </div>
  );
}
