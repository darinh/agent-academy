import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  Input,
  makeStyles,
  mergeClasses,
  shorthands,
  Spinner,
  Text,
  Badge,
  Button,
  useModalAttributes,
} from "@fluentui/react-components";
import {
  SearchRegular,
  DismissRegular,
  PlayRegular,
} from "@fluentui/react-icons";
import {
  executeCommand,
  getCommandExecution,
  getCommandMetadata,
} from "./api";
import type { CommandExecutionResponse } from "./api";
import {
  buildExecuteCommandRequest,
  createDefaultCommandDrafts,
  fromServerMetadata,
  validateCommandDraft,
  WEEK1_COMMANDS,
} from "./commandCatalog";
import type { HumanCommandDefinition, CommandCategory } from "./commandCatalog";

const POLL_INTERVAL_MS = 2500;

const CATEGORY_LABELS: Record<CommandCategory, string> = {
  workspace: "Workspace",
  code: "Code",
  git: "Git",
  operations: "Operations",
};

const CATEGORY_ORDER: CommandCategory[] = ["workspace", "code", "git", "operations"];

const useLocalStyles = makeStyles({
  backdrop: {
    position: "fixed",
    inset: "0",
    zIndex: 9999,
    display: "flex",
    alignItems: "flex-start",
    justifyContent: "center",
    paddingTop: "min(15vh, 120px)",
    backgroundColor: "rgba(0, 0, 0, 0.55)",
    backdropFilter: "blur(4px)",
  },
  container: {
    width: "min(600px, 90vw)",
    maxHeight: "min(520px, 70vh)",
    display: "flex",
    flexDirection: "column",
    backgroundColor: "var(--aa-panel)",
    border: "1px solid var(--aa-border)",
    boxShadow: "0 24px 80px rgba(0,0,0,0.6), 0 0 1px rgba(91,141,239,0.08)",
    ...shorthands.borderRadius("16px"),
    ...shorthands.overflow("hidden"),
  },
  searchRow: {
    display: "flex",
    alignItems: "center",
    gap: "10px",
    ...shorthands.padding("14px", "16px"),
    borderBottom: "1px solid var(--aa-border)",
  },
  searchIcon: {
    color: "var(--aa-soft)",
    fontSize: "18px",
    flexShrink: 0,
  },
  searchInput: {
    flexGrow: 1,
    "& input": {
      backgroundColor: "transparent",
      border: "none",
      color: "#fff",
      fontSize: "15px",
      "&::placeholder": { color: "var(--aa-soft)" },
    },
  },
  hint: {
    flexShrink: 0,
    color: "var(--aa-soft)",
    fontSize: "12px",
  },
  list: {
    overflowY: "auto",
    flexGrow: 1,
    ...shorthands.padding("6px"),
  },
  groupLabel: {
    ...shorthands.padding("8px", "12px", "4px"),
    fontSize: "11px",
    fontWeight: 600,
    textTransform: "uppercase" as const,
    letterSpacing: "0.08em",
    color: "var(--aa-soft)",
  },
  item: {
    display: "flex",
    alignItems: "center",
    gap: "10px",
    ...shorthands.padding("8px", "12px"),
    ...shorthands.borderRadius("8px"),
    cursor: "pointer",
    "&:hover": { backgroundColor: "rgba(91, 141, 239, 0.06)" },
  },
  itemSelected: {
    backgroundColor: "rgba(91, 141, 239, 0.08)",
  },
  itemBody: {
    flexGrow: 1,
    display: "flex",
    flexDirection: "column",
    gap: "1px",
    minWidth: 0,
  },
  itemTitle: {
    fontSize: "14px",
    fontWeight: 500,
    color: "#fff",
    overflow: "hidden",
    textOverflow: "ellipsis",
    whiteSpace: "nowrap",
  },
  itemDesc: {
    fontSize: "12px",
    color: "var(--aa-soft)",
    overflow: "hidden",
    textOverflow: "ellipsis",
    whiteSpace: "nowrap",
  },
  asyncBadge: {
    flexShrink: 0,
  },
  empty: {
    ...shorthands.padding("24px"),
    textAlign: "center",
    color: "var(--aa-soft)",
    fontSize: "13px",
  },
  // ── detail view ──
  detailPane: {
    display: "flex",
    flexDirection: "column",
    gap: "12px",
    ...shorthands.padding("16px"),
    overflowY: "auto",
    flexGrow: 1,
  },
  detailHeader: {
    display: "flex",
    alignItems: "center",
    justifyContent: "space-between",
    gap: "8px",
  },
  detailTitle: {
    fontSize: "16px",
    fontWeight: 600,
    color: "#fff",
  },
  detailDesc: {
    fontSize: "13px",
    color: "var(--aa-muted)",
    lineHeight: "1.4",
  },
  fieldGroup: {
    display: "flex",
    flexDirection: "column",
    gap: "8px",
  },
  fieldLabel: {
    display: "flex",
    justifyContent: "space-between",
    fontSize: "12px",
    fontWeight: 500,
    color: "var(--aa-muted)",
  },
  fieldHint: {
    fontWeight: 400,
    color: "var(--aa-soft)",
  },
  actionRow: {
    display: "flex",
    justifyContent: "flex-end",
    gap: "8px",
    ...shorthands.padding("8px", "0", "0"),
  },
  resultBox: {
    ...shorthands.padding("12px"),
    ...shorthands.borderRadius("8px"),
    backgroundColor: "rgba(139, 148, 158, 0.04)",
    border: "1px solid var(--aa-border)",
    fontSize: "12px",
    fontFamily: "monospace",
    color: "var(--aa-text)",
    whiteSpace: "pre-wrap",
    maxHeight: "200px",
    overflowY: "auto",
    wordBreak: "break-word",
  },
  resultSuccess: {
    ...shorthands.borderColor("rgba(76, 175, 80, 0.3)"),
  },
  resultError: {
    ...shorthands.borderColor("rgba(232, 93, 93, 0.3)"),
  },
  validationError: {
    color: "var(--aa-copper)",
  },
});

// ── types ──

type PaletteMode = "search" | "detail";

interface PaletteResult {
  response: CommandExecutionResponse;
  timestamp: number;
}

// ── component ──

interface CommandPaletteProps {
  open: boolean;
  onDismiss: () => void;
  roomId: string | null;
  readOnly?: boolean;
}

export default function CommandPalette({ open, onDismiss, roomId, readOnly }: CommandPaletteProps) {
  const styles = useLocalStyles();

  // ── focus trap via Fluent UI tabster ──
  const { modalAttributes } = useModalAttributes({ trapFocus: true });
  const containerRef = useRef<HTMLDivElement>(null);
  const triggerRef = useRef<Element | null>(null);

  // Capture the element that triggered the palette so we can restore focus on close
  useEffect(() => {
    if (open) {
      triggerRef.current = document.activeElement;
    } else if (triggerRef.current instanceof HTMLElement) {
      triggerRef.current.focus();
      triggerRef.current = null;
    }
  }, [open]);

  // ── commands catalog ──
  const [commands, setCommands] = useState<readonly HumanCommandDefinition[]>(WEEK1_COMMANDS);
  const loadedRef = useRef(false);

  useEffect(() => {
    if (!open || loadedRef.current) return;
    loadedRef.current = true;
    getCommandMetadata()
      .then((meta) => {
        const converted = fromServerMetadata(meta);
        if (converted.length > 0) setCommands(converted);
      })
      .catch(() => {/* keep fallback */});
  }, [open]);

  // ── state ──
  const [search, setSearch] = useState("");
  const [mode, setMode] = useState<PaletteMode>("search");
  const [selectedIndex, setSelectedIndex] = useState(0);
  const [activeCommand, setActiveCommand] = useState<HumanCommandDefinition | null>(null);
  const [drafts, setDrafts] = useState<Record<string, Record<string, string>>>(() =>
    createDefaultCommandDrafts(commands),
  );
  const [executing, setExecuting] = useState(false);
  const [result, setResult] = useState<PaletteResult | null>(null);
  const [validationErrors, setValidationErrors] = useState<string[]>([]);
  const inputRef = useRef<HTMLInputElement>(null);
  const listRef = useRef<HTMLDivElement>(null);
  const pollRef = useRef<ReturnType<typeof setInterval>>(undefined);
  const executionTokenRef = useRef(0);

  // Reset state when opened
  useEffect(() => {
    if (open) {
      setSearch("");
      setMode("search");
      setSelectedIndex(0);
      setActiveCommand(null);
      setResult(null);
      setValidationErrors([]);
      setExecuting(false);
      // Rebuild drafts when commands change
      setDrafts(createDefaultCommandDrafts(commands));
      // Cancel any in-flight polling
      executionTokenRef.current++;
      if (pollRef.current) clearInterval(pollRef.current);
      setTimeout(() => inputRef.current?.focus(), 50);
    }
    return () => { if (pollRef.current) clearInterval(pollRef.current); };
  }, [open, commands]);

  // ── filtering ──
  const filtered = useMemo(() => {
    const q = search.toLowerCase().trim();
    if (!q) return [...commands];
    return commands.filter(
      (c) =>
        c.title.toLowerCase().includes(q) ||
        c.command.toLowerCase().includes(q) ||
        c.description.toLowerCase().includes(q) ||
        c.category.toLowerCase().includes(q),
    );
  }, [commands, search]);

  // Group by category for display (unknown categories fall into last group)
  const grouped = useMemo(() => {
    const groups: { category: CommandCategory; items: HumanCommandDefinition[] }[] = [];
    const knownCats = new Set<string>(CATEGORY_ORDER);
    for (const cat of CATEGORY_ORDER) {
      const items = filtered.filter((c) => c.category === cat);
      if (items.length > 0) groups.push({ category: cat, items });
    }
    const uncategorized = filtered.filter((c) => !knownCats.has(c.category));
    if (uncategorized.length > 0) groups.push({ category: "operations", items: uncategorized });
    return groups;
  }, [filtered]);

  // Flat list for keyboard nav
  const flatItems = useMemo(() => grouped.flatMap((g) => g.items), [grouped]);

  // Clamp selected index
  useEffect(() => {
    setSelectedIndex((prev) => Math.max(0, Math.min(prev, flatItems.length - 1)));
  }, [flatItems.length]);

  // ── handlers ──
  const selectCommand = useCallback(
    (cmd: HumanCommandDefinition) => {
      setActiveCommand(cmd);
      setMode("detail");
      setResult(null);
      setValidationErrors([]);
    },
    [],
  );

  const handleBack = useCallback(() => {
    setMode("search");
    setActiveCommand(null);
    setResult(null);
    setValidationErrors([]);
    setExecuting(false);
    executionTokenRef.current++;
    if (pollRef.current) clearInterval(pollRef.current);
    setTimeout(() => inputRef.current?.focus(), 50);
  }, []);

  const updateField = useCallback(
    (command: string, field: string, value: string) => {
      setDrafts((prev) => ({
        ...prev,
        [command]: { ...(prev[command] ?? {}), [field]: value },
      }));
    },
    [],
  );

  const handleExecute = useCallback(async () => {
    if (!activeCommand || executing || readOnly) return;

    const draft = drafts[activeCommand.command] ?? {};
    const errors = validateCommandDraft(activeCommand, draft);
    if (errors.length > 0) {
      setValidationErrors(errors);
      return;
    }
    setValidationErrors([]);

    const req = buildExecuteCommandRequest(activeCommand, draft);
    if (roomId) (req as unknown as Record<string, unknown>).roomId = roomId;

    const token = ++executionTokenRef.current;
    setExecuting(true);
    setResult(null);

    try {
      let resp = await executeCommand(req);
      if (token !== executionTokenRef.current) return;

      if (activeCommand.isAsync && resp.status === "pending") {
        await new Promise<void>((resolve) => {
          pollRef.current = setInterval(async () => {
            if (token !== executionTokenRef.current) {
              if (pollRef.current) clearInterval(pollRef.current);
              resolve();
              return;
            }
            try {
              const poll = await getCommandExecution(resp.correlationId);
              if (poll.status !== "pending") {
                resp = poll;
                if (pollRef.current) clearInterval(pollRef.current);
                resolve();
              }
            } catch {
              if (pollRef.current) clearInterval(pollRef.current);
              resolve();
            }
          }, POLL_INTERVAL_MS);
        });
      }
      if (token !== executionTokenRef.current) return;
      setResult({ response: resp, timestamp: Date.now() });
    } catch (e) {
      if (token !== executionTokenRef.current) return;
      setResult({
        response: {
          command: activeCommand.command,
          status: "failed",
          result: null,
          error: e instanceof Error ? e.message : "Unknown error",
          errorCode: "NETWORK",
          correlationId: "",
          timestamp: new Date().toISOString(),
          executedBy: "human",
        },
        timestamp: Date.now(),
      });
    } finally {
      if (token === executionTokenRef.current) setExecuting(false);
    }
  }, [activeCommand, drafts, executing, readOnly, roomId]);

  // ── keyboard ──
  const handleKeyDown = useCallback(
    (e: React.KeyboardEvent) => {
      if (mode === "search") {
        if (e.key === "ArrowDown") {
          e.preventDefault();
          setSelectedIndex((i) => Math.min(i + 1, flatItems.length - 1));
        } else if (e.key === "ArrowUp") {
          e.preventDefault();
          setSelectedIndex((i) => Math.max(i - 1, 0));
        } else if (e.key === "Enter" && flatItems.length > 0) {
          e.preventDefault();
          selectCommand(flatItems[selectedIndex]);
        } else if (e.key === "Escape") {
          e.preventDefault();
          onDismiss();
        }
      } else if (mode === "detail") {
        if (e.key === "Escape") {
          e.preventDefault();
          handleBack();
        } else if ((e.metaKey || e.ctrlKey) && e.key === "Enter") {
          e.preventDefault();
          handleExecute();
        }
      }
    },
    [mode, flatItems, selectedIndex, selectCommand, onDismiss, handleBack, handleExecute],
  );

  // Scroll selected item into view
  useEffect(() => {
    if (mode !== "search" || !listRef.current) return;
    const items = listRef.current.querySelectorAll("[data-palette-item]");
    items[selectedIndex]?.scrollIntoView({ block: "nearest" });
  }, [selectedIndex, mode]);

  if (!open) return null;

  const formatResult = (resp: CommandExecutionResponse): string => {
    if (resp.error) return `Error: ${resp.error}`;
    if (resp.result == null) return "Done (no output)";
    if (typeof resp.result === "string") return resp.result;
    return JSON.stringify(resp.result, null, 2);
  };

  return (
    <div className={styles.backdrop} onClick={onDismiss} role="presentation">
      <div
        ref={containerRef}
        className={styles.container}
        onClick={(e) => e.stopPropagation()}
        onKeyDown={handleKeyDown}
        role="dialog"
        aria-modal="true"
        aria-label="Command palette"
        {...modalAttributes}
      >
        {mode === "search" && (
          <>
            <div className={styles.searchRow}>
              <SearchRegular className={styles.searchIcon} />
              <Input
                ref={inputRef}
                className={styles.searchInput}
                appearance="underline"
                placeholder="Search commands…"
                value={search}
                onChange={(_, d) => { setSearch(d.value); setSelectedIndex(0); }}
              />
              <span className={styles.hint}>esc to close</span>
            </div>
            <div className={styles.list} ref={listRef}>
              {flatItems.length === 0 && (
                <div className={styles.empty}>No commands match "{search}"</div>
              )}
              {grouped.map((group) => {
                const globalOffset = flatItems.indexOf(group.items[0]);
                return (
                  <div key={group.category}>
                    <div className={styles.groupLabel}>{CATEGORY_LABELS[group.category]}</div>
                    {group.items.map((cmd, localIdx) => {
                      const gi = globalOffset + localIdx;
                      return (
                        <div
                          key={cmd.command}
                          data-palette-item
                          className={mergeClasses(
                            styles.item,
                            gi === selectedIndex && styles.itemSelected,
                          )}
                          onClick={() => selectCommand(cmd)}
                          onMouseEnter={() => setSelectedIndex(gi)}
                        >
                          <div className={styles.itemBody}>
                            <Text className={styles.itemTitle}>{cmd.title}</Text>
                            <Text className={styles.itemDesc}>{cmd.description}</Text>
                          </div>
                          {cmd.isAsync && (
                            <Badge className={styles.asyncBadge} size="small" appearance="outline" color="informative">
                              async
                            </Badge>
                          )}
                        </div>
                      );
                    })}
                  </div>
                );
              })}
            </div>
          </>
        )}

        {mode === "detail" && activeCommand && (
          <div className={styles.detailPane}>
            <div className={styles.detailHeader}>
              <Text className={styles.detailTitle}>{activeCommand.title}</Text>
              <Button
                size="small"
                appearance="subtle"
                icon={<DismissRegular />}
                onClick={handleBack}
                aria-label="Back to search"
              />
            </div>
            <Text className={styles.detailDesc}>{activeCommand.detail}</Text>

            {activeCommand.fields.length > 0 && (
              <div className={styles.fieldGroup}>
                {activeCommand.fields.map((field) => (
                  <div key={field.name}>
                    <div className={styles.fieldLabel}>
                      <span>
                        {field.label}
                        {field.required && " *"}
                      </span>
                      <span className={styles.fieldHint}>{field.description}</span>
                    </div>
                    <Input
                      size="small"
                      appearance="filled-darker"
                      placeholder={field.placeholder}
                      value={(drafts[activeCommand.command] ?? {})[field.name] ?? ""}
                      onChange={(_, d) => updateField(activeCommand.command, field.name, d.value)}
                    />
                  </div>
                ))}
              </div>
            )}

            {validationErrors.length > 0 && (
              <div>
                {validationErrors.map((err) => (
                  <Text key={err} className={styles.validationError}>{err}</Text>
                ))}
              </div>
            )}

            {result && (
              <div
                className={mergeClasses(
                  styles.resultBox,
                  result.response.status === "completed"
                    ? styles.resultSuccess
                    : styles.resultError,
                )}
              >
                {formatResult(result.response)}
              </div>
            )}

            <div className={styles.actionRow}>
              <Button size="small" appearance="subtle" onClick={handleBack}>
                Back
              </Button>
              <Button
                size="small"
                appearance="primary"
                icon={executing ? <Spinner size="tiny" /> : <PlayRegular />}
                disabled={executing || readOnly}
                onClick={handleExecute}
              >
                {executing ? "Running…" : "Execute"}
              </Button>
            </div>
          </div>
        )}
      </div>
    </div>
  );
}
