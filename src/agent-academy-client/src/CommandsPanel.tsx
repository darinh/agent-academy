import { useEffect, useRef, useMemo, useState } from "react";
import {
  Badge,
  Button,
  Input,
  makeStyles,
  mergeClasses,
  shorthands,
  Spinner,
  Text,
  Textarea,
} from "@fluentui/react-components";
import { PlayRegular } from "@fluentui/react-icons";
import {
  executeCommand,
  getCommandExecution,
  getCommandMetadata,
} from "./api";
import type {
  CommandExecutionResponse,
} from "./api";
import {
  buildExecuteCommandRequest,
  createDefaultCommandDrafts,
  fromServerMetadata,
  validateCommandDraft,
  WEEK1_COMMANDS,
} from "./commandCatalog";
import type { HumanCommandDefinition } from "./commandCatalog";
import {
  badgeColorForCategory,
  badgeColorForStatus,
  findPrimaryList,
  findPreviewBlock,
  MAX_HISTORY_ITEMS,
  POLL_INTERVAL_MS,
  summarizeResult,
} from "./commandsPanelUtils";

const useLocalStyles = makeStyles({
  root: {
    minHeight: 0,
    display: "grid",
    gridTemplateColumns: "minmax(0, 1.08fr) minmax(320px, 0.92fr)",
    gap: "16px",
    overflowY: "auto",
    "@media (max-width: 1200px)": {
      gridTemplateColumns: "1fr",
    },
  },
  stack: {
    minHeight: 0,
    display: "grid",
    gap: "16px",
    alignContent: "start",
    overflowY: "auto",
  },
  section: {
    display: "grid",
    gap: "18px",
    border: "1px solid var(--aa-border)",
    background:
      "var(--aa-panel)",
    boxShadow: "none",
    ...shorthands.borderRadius("8px"),
    ...shorthands.padding("16px"),
  },
  hero: {
    position: "relative",
    overflow: "hidden",
    background:
      "var(--aa-panel)",
  },
  eyebrow: {
    display: "inline-flex",
    alignItems: "center",
    width: "fit-content",
    color: "var(--aa-soft)",
    backgroundColor: "rgba(91, 141, 239, 0.08)",
    border: "1px solid var(--aa-border)",
    ...shorthands.borderRadius("999px"),
    ...shorthands.padding("7px", "12px"),
    fontSize: "11px",
    fontWeight: 700,
    letterSpacing: "0.04em",
    textTransform: "uppercase",
  },
  heroTitle: {
    margin: 0,
    fontFamily: "var(--mono)",
    fontSize: "14px",
    lineHeight: 1.1,
    letterSpacing: "-0.02em",
    color: "var(--aa-text-strong)",
  },
  heroDescription: {
    maxWidth: "64ch",
    color: "var(--aa-muted)",
    fontSize: "13px",
    lineHeight: 1.8,
  },
  metricRow: {
    display: "grid",
    gridTemplateColumns: "repeat(3, minmax(0, 1fr))",
    gap: "14px",
    "@media (max-width: 720px)": {
      gridTemplateColumns: "1fr",
    },
  },
  metricCard: {
    display: "grid",
    gap: "8px",
    border: "1px solid var(--aa-border)",
    background: "rgba(110, 118, 129, 0.1)",
    ...shorthands.borderRadius("8px"),
    ...shorthands.padding("16px"),
  },
  metricLabel: {
    color: "var(--aa-soft)",
    fontSize: "11px",
    fontWeight: 700,
    letterSpacing: "0.04em",
    textTransform: "uppercase",
  },
  metricValue: {
    color: "var(--aa-text-strong)",
    fontFamily: "var(--mono)",
    fontSize: "14px",
    fontWeight: 760,
    letterSpacing: "-0.02em",
  },
  metricNote: {
    color: "var(--aa-muted)",
    fontSize: "12px",
    lineHeight: 1.65,
  },
  commandGrid: {
    display: "grid",
    gridTemplateColumns: "repeat(auto-fit, minmax(220px, 1fr))",
    gap: "14px",
  },
  commandCard: {
    display: "grid",
    gap: "12px",
    textAlign: "left",
    color: "inherit",
    border: "1px solid var(--aa-border)",
    background:
      "var(--aa-panel)",
    cursor: "pointer",
    transitionDuration: "180ms",
    transitionProperty: "transform, border-color, background-color, box-shadow",
    transitionTimingFunction: "ease",
    ...shorthands.borderRadius("8px"),
    ...shorthands.padding("16px"),
    ":hover": {
      transform: "translateY(-1px)",
      border: "1px solid var(--aa-border)",
      background: "var(--aa-panel)",
      boxShadow: "0 14px 36px rgba(0, 0, 0, 0.22)",
    },
  },
  commandCardActive: {
    border: "1px solid var(--aa-border)",
    background:
      "rgba(91, 141, 239, 0.12)",
    boxShadow: "0 18px 40px rgba(5, 10, 18, 0.32)",
  },
  commandMetaRow: {
    display: "flex",
    justifyContent: "space-between",
    alignItems: "center",
    gap: "10px",
    flexWrap: "wrap",
  },
  commandTitle: {
    color: "var(--aa-text-strong)",
    fontSize: "13px",
    fontWeight: 700,
    letterSpacing: "-0.02em",
  },
  commandDescription: {
    color: "var(--aa-muted)",
    fontSize: "13px",
    lineHeight: 1.65,
  },
  commandDetail: {
    color: "var(--aa-soft)",
    fontSize: "12px",
    lineHeight: 1.65,
  },
  composerHeader: {
    display: "grid",
    gap: "8px",
  },
  sectionTitle: {
    margin: 0,
    color: "var(--aa-text-strong)",
    fontSize: "13px",
    fontWeight: 720,
    letterSpacing: "-0.02em",
  },
  sectionText: {
    color: "var(--aa-muted)",
    fontSize: "13px",
    lineHeight: 1.7,
  },
  formGrid: {
    display: "grid",
    gap: "12px",
    gridTemplateColumns: "repeat(auto-fit, minmax(220px, 1fr))",
  },
  field: {
    display: "grid",
    gap: "8px",
  },
  fieldWide: {
    gridColumn: "1 / -1",
  },
  fieldLabel: {
    color: "var(--aa-text-strong)",
    fontSize: "12px",
    fontWeight: 650,
    letterSpacing: "0.02em",
  },
  fieldDescription: {
    color: "var(--aa-soft)",
    fontSize: "12px",
    lineHeight: 1.55,
  },
  input: {
    "& input, & textarea": {
      color: "var(--aa-text-strong)",
      backgroundColor: "rgba(7, 12, 20, 0.72)",
    },
  },
  actionRow: {
    display: "flex",
    justifyContent: "space-between",
    gap: "12px",
    alignItems: "center",
    flexWrap: "wrap",
  },
  helperText: {
    color: "var(--aa-soft)",
    fontSize: "12px",
    lineHeight: 1.6,
  },
  helperWarning: {
    color: "var(--aa-soft)",
  },
  errorBox: {
    color: "#ffd6dc",
    border: "1px solid rgba(181, 110, 79, 0.22)",
    backgroundColor: "rgba(74, 22, 20, 0.3)",
    ...shorthands.borderRadius("6px"),
    ...shorthands.padding("12px", "14px"),
    fontSize: "13px",
    lineHeight: 1.65,
  },
  resultRail: {
    minHeight: 0,
    display: "grid",
    gridTemplateRows: "auto minmax(0, 1fr)",
    gap: "20px",
  },
  historyList: {
    minHeight: 0,
    overflowY: "auto",
    display: "grid",
    alignContent: "start",
    gap: "12px",
  },
  historyItem: {
    display: "grid",
    gap: "12px",
    border: "1px solid var(--aa-border)",
    background:
      "var(--aa-panel)",
    ...shorthands.borderRadius("8px"),
    ...shorthands.padding("16px"),
  },
  historyHeader: {
    display: "flex",
    justifyContent: "space-between",
    gap: "12px",
    flexWrap: "wrap",
    alignItems: "start",
  },
  historyTitleBlock: {
    display: "grid",
    gap: "6px",
  },
  historyTitle: {
    color: "var(--aa-text-strong)",
    fontSize: "13px",
    fontWeight: 700,
    letterSpacing: "-0.02em",
  },
  historyMeta: {
    color: "var(--aa-soft)",
    fontSize: "12px",
  },
  badgeRow: {
    display: "flex",
    gap: "8px",
    alignItems: "center",
    flexWrap: "wrap",
  },
  summaryGrid: {
    display: "grid",
    gridTemplateColumns: "repeat(auto-fit, minmax(140px, 1fr))",
    gap: "10px",
  },
  summaryCard: {
    display: "grid",
    gap: "4px",
    border: "1px solid var(--aa-border)",
    backgroundColor: "rgba(110, 118, 129, 0.1)",
    ...shorthands.borderRadius("6px"),
    ...shorthands.padding("10px", "12px"),
  },
  summaryLabel: {
    color: "var(--aa-soft)",
    fontSize: "11px",
    textTransform: "uppercase",
    letterSpacing: "0.08em",
  },
  summaryValue: {
    color: "var(--aa-text-strong)",
    fontSize: "13px",
    lineHeight: 1.5,
    wordBreak: "break-word",
  },
  recordList: {
    display: "grid",
    gap: "8px",
  },
  recordListItem: {
    display: "grid",
    gap: "4px",
    borderBottom: "1px solid rgba(110, 118, 129, 0.1)",
    ...shorthands.padding("0", "0", "8px"),
  },
  recordPrimary: {
    color: "var(--aa-text)",
    fontSize: "13px",
  },
  recordSecondary: {
    color: "var(--aa-soft)",
    fontSize: "12px",
  },
  preview: {
    margin: 0,
    color: "var(--aa-text)",
    backgroundColor: "rgba(9, 12, 18, 0.86)",
    border: "1px solid rgba(214, 188, 149, 0.1)",
    fontFamily: "var(--mono)",
    fontSize: "12px",
    lineHeight: 1.7,
    overflowX: "auto",
    maxHeight: "320px",
    whiteSpace: "pre-wrap",
    wordBreak: "break-word",
    ...shorthands.borderRadius("6px"),
    ...shorthands.padding("14px"),
  },
  emptyState: {
    display: "grid",
    placeItems: "center",
    minHeight: "240px",
    color: "var(--aa-soft)",
    textAlign: "center",
    lineHeight: 1.8,
    border: "1px dashed var(--aa-border)",
    ...shorthands.borderRadius("8px"),
    ...shorthands.padding("24px"),
  },
});

interface CommandsPanelProps {
  roomId: string | null;
  readOnly?: boolean;
}

interface CommandHistoryItem {
  definition: HumanCommandDefinition;
  response: CommandExecutionResponse;
  args?: Record<string, string>;
}

export default function CommandsPanel({ roomId, readOnly = false }: CommandsPanelProps) {
  const s = useLocalStyles();
  const [commands, setCommands] = useState<readonly HumanCommandDefinition[]>(WEEK1_COMMANDS);
  const [, setMetadataLoaded] = useState(false);
  const [selectedCommand, setSelectedCommand] = useState<string>("READ_FILE");
  const [drafts, setDrafts] = useState(() => createDefaultCommandDrafts());
  const [history, setHistory] = useState<CommandHistoryItem[]>([]);
  const [submitting, setSubmitting] = useState(false);
  const [panelError, setPanelError] = useState<string | null>(null);
  const fetchRef = useRef(0);

  useEffect(() => {
    const seq = ++fetchRef.current;
    getCommandMetadata()
      .then((data) => {
        if (seq !== fetchRef.current) return;
        const defs = fromServerMetadata(data);
        if (defs.length > 0) {
          setCommands(defs);
          setDrafts((prev) => {
            const next = createDefaultCommandDrafts(defs);
            // Preserve any drafts the user already started typing
            for (const key of Object.keys(prev)) {
              if (next[key]) {
                const hasDirtyFields = Object.values(prev[key]).some((v) => v.trim() !== "");
                if (hasDirtyFields) next[key] = prev[key];
              }
            }
            return next;
          });
          setSelectedCommand((prev) => defs.some((d) => d.command === prev) ? prev : defs[0].command);
        }
        setMetadataLoaded(true);
      })
      .catch(() => {
        if (seq !== fetchRef.current) return;
        setMetadataLoaded(true); // fallback to hardcoded catalog
      });
  }, []);

  const commandMap = useMemo(
    () => new Map(commands.map((c) => [c.command, c])),
    [commands],
  );

  const definition = commandMap.get(selectedCommand) ?? commands[0];
  const draft = drafts[selectedCommand] ?? {};

  useEffect(() => {
    if (!roomId) {
      return;
    }

    setDrafts((current) => {
      const roomDraft = current.ROOM_HISTORY ?? {};
      if ((roomDraft.roomId ?? "").trim()) {
        return current;
      }

      return {
        ...current,
        ROOM_HISTORY: {
          ...roomDraft,
          roomId,
        },
      };
    });
  }, [roomId]);

  useEffect(() => {
    const pending = history.filter((item) => item.response.status === "pending");
    if (pending.length === 0) {
      return undefined;
    }

    const timer = window.setInterval(() => {
      void Promise.all(pending.map(async (item) => {
        try {
          const next = await getCommandExecution(item.response.correlationId);
          setHistory((current) => current.map((entry) =>
            entry.response.correlationId === next.correlationId
              ? { ...entry, response: next }
              : entry));
        } catch {
          // Leave pending entries alone until the next poll cycle.
        }
      }));
    }, POLL_INTERVAL_MS);

    return () => window.clearInterval(timer);
  }, [history]);

  const pendingCount = history.filter((item) => item.response.status === "pending").length;
  const syncCount = commands.filter((command) => !command.isAsync).length;
  const asyncCount = commands.length - syncCount;

  const handleFieldChange = (fieldName: string, value: string) => {
    setDrafts((current) => ({
      ...current,
      [selectedCommand]: {
        ...(current[selectedCommand] ?? {}),
        [fieldName]: value,
      },
    }));
  };

  const handleExecute = async () => {
    const errors = validateCommandDraft(definition, draft);
    if (errors.length > 0) {
      setPanelError(errors.join(" "));
      return;
    }

    if (readOnly) {
      setPanelError("Commands are paused while Copilot is degraded. Reconnect before running new work.");
      return;
    }

    setSubmitting(true);
    setPanelError(null);

    try {
      const request = buildExecuteCommandRequest(definition, draft);
      const response = await executeCommand(request);
      const args = request.args
        ? Object.fromEntries(Object.entries(request.args).map(([key, value]) => [key, String(value)]))
        : undefined;

      setHistory((current) => [
        { definition, response, args },
        ...current,
      ].slice(0, MAX_HISTORY_ITEMS));
    } catch (error) {
      setPanelError(error instanceof Error ? error.message : "Command execution failed.");
    } finally {
      setSubmitting(false);
    }
  };

  const latestResult = history[0];

  return (
    <div className={s.root}>
      <div className={s.stack}>
        <section className={mergeClasses(s.section, s.hero)}>
          <div style={{ display: "flex", alignItems: "center", gap: "12px", flexWrap: "wrap" }}>
            <div className={s.eyebrow}>Command deck</div>
            <span style={{ color: "var(--aa-muted)", fontSize: "12px" }}>
              {commands.length} commands · {syncCount} instant · {asyncCount} polling
            </span>
          </div>
          <h2 className={s.heroTitle}>Select a command below, fill parameters, then execute.</h2>
        </section>

        <section className={s.section}>
          <div className={s.composerHeader}>
            <h3 className={s.sectionTitle}>Command deck</h3>
            <div className={s.sectionText}>
              Pick a command family, fill only the scalar args the backend supports, then run it through the
              new controller contract.
            </div>
          </div>

          <div className={s.commandGrid}>
            {commands.map((command) => (
              <button
                key={command.command}
                type="button"
                className={mergeClasses(
                  s.commandCard,
                  command.command === selectedCommand && s.commandCardActive,
                )}
                onClick={() => {
                  setSelectedCommand(command.command);
                  setPanelError(null);
                }}
              >
                <div className={s.commandMetaRow}>
                  <Badge appearance="outline" color={badgeColorForCategory(command.category)}>
                    {command.category}
                  </Badge>
                  <Badge appearance={command.isAsync ? "filled" : "outline"} color={command.isAsync ? "warning" : "informative"}>
                    {command.isAsync ? "polling" : "instant"}
                  </Badge>
                </div>
                <div className={s.commandTitle}>{command.title}</div>
                <div className={s.commandDescription}>{command.description}</div>
                <div className={s.commandDetail}>{command.detail}</div>
              </button>
            ))}
          </div>
        </section>

        <section className={s.section}>
          <div className={s.composerHeader}>
            <h3 className={s.sectionTitle}>{definition.title}</h3>
            <div className={s.sectionText}>{definition.detail}</div>
          </div>

          {definition.fields.length > 0 ? (
            <div className={s.formGrid}>
              {definition.fields.map((field) => {
                const fieldValue = draft[field.name] ?? "";
                const isWide = field.kind === "textarea";

                return (
                  <label
                    key={field.name}
                    className={mergeClasses(s.field, isWide && s.fieldWide)}
                  >
                    <div className={s.fieldLabel}>
                      {field.label}
                      {field.required ? " *" : ""}
                    </div>
                    {field.kind === "textarea" ? (
                      <Textarea
                        className={s.input}
                        resize="vertical"
                        rows={4}
                        placeholder={field.placeholder}
                        value={fieldValue}
                        onChange={(_, data) => handleFieldChange(field.name, data.value)}
                      />
                    ) : (
                      <Input
                        className={s.input}
                        type={field.kind === "number" ? "number" : "text"}
                        placeholder={field.placeholder}
                        value={fieldValue}
                        onChange={(_, data) => handleFieldChange(field.name, data.value)}
                      />
                    )}
                    <div className={s.fieldDescription}>{field.description}</div>
                  </label>
                );
              })}
            </div>
          ) : (
            <div className={s.helperText}>
              No extra arguments required. This command can run as-is.
            </div>
          )}

          {panelError && <div className={s.errorBox}>{panelError}</div>}

          <div className={s.actionRow}>
            <Text className={mergeClasses(s.helperText, readOnly && s.helperWarning)}>
              {readOnly
                ? "Limited mode is active. You can inspect the command deck, but new executions are disabled until Copilot returns to operational."
                : definition.isAsync
                  ? "This command returns immediately, then polls for a final result."
                  : "This command returns a result directly in the panel."}
            </Text>
            <Button
              appearance="primary"
              icon={submitting ? <Spinner size="tiny" /> : <PlayRegular />}
              disabled={submitting || readOnly}
              onClick={() => void handleExecute()}
            >
              {submitting ? "Running…" : `Run ${definition.title}`}
            </Button>
          </div>
        </section>
      </div>

      <div className={s.resultRail}>
        <section className={s.section}>
          <div className={s.composerHeader}>
            <h3 className={s.sectionTitle}>Execution rail</h3>
            <div className={s.sectionText}>
              Recent command runs are preserved here, with pending jobs refreshed automatically from their correlation ids.
            </div>
          </div>
          <div className={s.badgeRow}>
            <Badge appearance="outline" color="informative">latest first</Badge>
            <Badge appearance="outline" color="warning">{pendingCount} pending</Badge>
            <Badge appearance="outline" color="success">audited</Badge>
          </div>
          {latestResult ? (
            <CommandResultCard item={latestResult} />
          ) : (
            <div className={s.emptyState}>
              <div>
                No command runs yet.
                <br />
                Pick a card on the left and launch the first command to populate the rail.
              </div>
            </div>
          )}
        </section>

        <section className={s.section}>
          <div className={s.composerHeader}>
            <h3 className={s.sectionTitle}>History</h3>
            <div className={s.sectionText}>
              The most recent {MAX_HISTORY_ITEMS} commands stay visible for quick compare-and-contrast debugging.
            </div>
          </div>
          <div className={s.historyList}>
            {history.slice(1).map((item) => (
              <CommandResultCard key={item.response.correlationId} item={item} compact />
            ))}
          </div>
        </section>
      </div>
    </div>
  );
}

function CommandResultCard({ item, compact = false }: { item: CommandHistoryItem; compact?: boolean }) {
  const s = useLocalStyles();
  const metadata = summarizeResult(item.response.result);
  const arrayEntries = findPrimaryList(item.response.result);
  const preview = findPreviewBlock(item.response.result);
  const argsText = item.args && Object.keys(item.args).length > 0
    ? Object.entries(item.args).map(([key, value]) => `${key}: ${value}`).join(" · ")
    : "No args";

  return (
    <article className={s.historyItem}>
      <div className={s.historyHeader}>
        <div className={s.historyTitleBlock}>
          <div className={s.historyTitle}>{item.definition.title}</div>
          <div className={s.historyMeta}>
            {new Date(item.response.timestamp).toLocaleString()} · {argsText}
          </div>
        </div>
        <div className={s.badgeRow}>
          <Badge appearance="outline" color={badgeColorForStatus(item.response.status)}>
            {item.response.status}
          </Badge>
          <Badge appearance="outline" color={item.definition.isAsync ? "warning" : "informative"}>
            {item.response.command}
          </Badge>
        </div>
      </div>

      {item.response.error && (
        <div className={s.errorBox}>
          {item.response.errorCode && (
            <Badge appearance="filled" color="danger" style={{ marginRight: 8 }}>
              {item.response.errorCode}
            </Badge>
          )}
          {item.response.error}
        </div>
      )}

      {metadata.length > 0 && (
        <div className={s.summaryGrid}>
          {metadata.map(([label, value]) => (
            <div key={label} className={s.summaryCard}>
              <div className={s.summaryLabel}>{label}</div>
              <div className={s.summaryValue}>{value}</div>
            </div>
          ))}
        </div>
      )}

      {!compact && arrayEntries.length > 0 && (
        <div className={s.recordList}>
          {arrayEntries.map((entry, index) => (
            <div key={index} className={s.recordListItem}>
              <div className={s.recordPrimary}>{entry.primary}</div>
              {entry.secondary && <div className={s.recordSecondary}>{entry.secondary}</div>}
            </div>
          ))}
        </div>
      )}

      {preview && <pre className={s.preview}>{preview}</pre>}
      {!preview && item.response.result != null && (
        <pre className={s.preview}>{JSON.stringify(item.response.result, null, 2)}</pre>
      )}
    </article>
  );
}
