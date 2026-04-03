import { useEffect, useMemo, useState } from "react";
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
} from "./api";
import type {
  CommandExecutionResponse,
  CommandExecutionStatus,
  HumanCommandName,
} from "./api";
import {
  buildExecuteCommandRequest,
  createDefaultCommandDrafts,
  getCommandDefinition,
  validateCommandDraft,
  WEEK1_COMMANDS,
} from "./commandCatalog";

const POLL_INTERVAL_MS = 2500;
const MAX_HISTORY_ITEMS = 10;

const useLocalStyles = makeStyles({
  root: {
    minHeight: 0,
    display: "grid",
    gridTemplateColumns: "minmax(0, 1.08fr) minmax(320px, 0.92fr)",
    gap: "18px",
    "@media (max-width: 1200px)": {
      gridTemplateColumns: "1fr",
    },
  },
  stack: {
    minHeight: 0,
    display: "grid",
    gap: "18px",
    alignContent: "start",
  },
  section: {
    display: "grid",
    gap: "18px",
    border: "1px solid rgba(155, 176, 210, 0.16)",
    background: "linear-gradient(180deg, rgba(255, 255, 255, 0.04), rgba(255, 255, 255, 0.015))",
    boxShadow: "inset 0 1px 0 rgba(255, 255, 255, 0.04)",
    ...shorthands.borderRadius("28px"),
    ...shorthands.padding("22px"),
  },
  hero: {
    position: "relative",
    overflow: "hidden",
    background:
      "radial-gradient(circle at top right, rgba(131, 207, 255, 0.16), transparent 28%), linear-gradient(135deg, rgba(255, 255, 255, 0.045), rgba(255, 255, 255, 0.012) 44%, rgba(10, 15, 24, 0.92))",
  },
  eyebrow: {
    display: "inline-flex",
    alignItems: "center",
    width: "fit-content",
    color: "#f3d4a8",
    backgroundColor: "rgba(217, 166, 103, 0.12)",
    border: "1px solid rgba(217, 166, 103, 0.22)",
    ...shorthands.borderRadius("999px"),
    ...shorthands.padding("7px", "12px"),
    fontSize: "11px",
    fontWeight: 700,
    letterSpacing: "0.12em",
    textTransform: "uppercase",
  },
  heroTitle: {
    margin: 0,
    fontFamily: "var(--heading)",
    fontSize: "36px",
    lineHeight: 0.98,
    letterSpacing: "-0.05em",
    color: "#f8fbff",
  },
  heroDescription: {
    maxWidth: "64ch",
    color: "#b8cae6",
    fontSize: "15px",
    lineHeight: 1.75,
  },
  metricRow: {
    display: "grid",
    gridTemplateColumns: "repeat(3, minmax(0, 1fr))",
    gap: "12px",
    "@media (max-width: 720px)": {
      gridTemplateColumns: "1fr",
    },
  },
  metricCard: {
    display: "grid",
    gap: "8px",
    border: "1px solid rgba(163, 180, 208, 0.14)",
    background: "rgba(255, 255, 255, 0.03)",
    ...shorthands.borderRadius("22px"),
    ...shorthands.padding("16px"),
  },
  metricLabel: {
    color: "#7f94b6",
    fontSize: "11px",
    fontWeight: 700,
    letterSpacing: "0.12em",
    textTransform: "uppercase",
  },
  metricValue: {
    color: "#eff5ff",
    fontSize: "28px",
    fontWeight: 760,
    letterSpacing: "-0.04em",
  },
  metricNote: {
    color: "#9db3d3",
    fontSize: "12px",
    lineHeight: 1.55,
  },
  commandGrid: {
    display: "grid",
    gridTemplateColumns: "repeat(auto-fit, minmax(220px, 1fr))",
    gap: "12px",
  },
  commandCard: {
    display: "grid",
    gap: "12px",
    textAlign: "left",
    color: "inherit",
    border: "1px solid rgba(163, 180, 208, 0.14)",
    background: "linear-gradient(180deg, rgba(255, 255, 255, 0.035), rgba(255, 255, 255, 0.012))",
    cursor: "pointer",
    transitionDuration: "180ms",
    transitionProperty: "transform, border-color, background-color, box-shadow",
    transitionTimingFunction: "ease",
    ...shorthands.borderRadius("22px"),
    ...shorthands.padding("16px"),
    ":hover": {
      transform: "translateY(-1px)",
      border: "1px solid rgba(131, 207, 255, 0.2)",
      background: "linear-gradient(180deg, rgba(255, 255, 255, 0.05), rgba(255, 255, 255, 0.02))",
      boxShadow: "0 14px 36px rgba(0, 0, 0, 0.16)",
    },
  },
  commandCardActive: {
    border: "1px solid rgba(131, 207, 255, 0.32)",
    background:
      "linear-gradient(135deg, rgba(131, 207, 255, 0.14), rgba(217, 166, 103, 0.06) 48%, rgba(255, 255, 255, 0.02))",
    boxShadow: "0 18px 40px rgba(5, 10, 18, 0.28)",
  },
  commandMetaRow: {
    display: "flex",
    justifyContent: "space-between",
    alignItems: "center",
    gap: "10px",
    flexWrap: "wrap",
  },
  commandTitle: {
    color: "#eff5ff",
    fontSize: "16px",
    fontWeight: 700,
    letterSpacing: "-0.02em",
  },
  commandDescription: {
    color: "#9db3d3",
    fontSize: "13px",
    lineHeight: 1.65,
  },
  commandDetail: {
    color: "#7f94b6",
    fontSize: "12px",
    lineHeight: 1.65,
  },
  composerHeader: {
    display: "grid",
    gap: "8px",
  },
  sectionTitle: {
    margin: 0,
    color: "#eff5ff",
    fontSize: "18px",
    fontWeight: 720,
    letterSpacing: "-0.03em",
  },
  sectionText: {
    color: "#9db3d3",
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
    color: "#eff5ff",
    fontSize: "12px",
    fontWeight: 650,
    letterSpacing: "0.02em",
  },
  fieldDescription: {
    color: "#7f94b6",
    fontSize: "12px",
    lineHeight: 1.55,
  },
  input: {
    "& input, & textarea": {
      color: "#eff5ff",
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
    color: "#7f94b6",
    fontSize: "12px",
    lineHeight: 1.6,
  },
  helperWarning: {
    color: "#f2d3a4",
  },
  errorBox: {
    color: "#ffd6dc",
    border: "1px solid rgba(255, 113, 135, 0.2)",
    backgroundColor: "rgba(95, 13, 30, 0.24)",
    ...shorthands.borderRadius("18px"),
    ...shorthands.padding("12px", "14px"),
    fontSize: "13px",
    lineHeight: 1.65,
  },
  resultRail: {
    minHeight: 0,
    display: "grid",
    gridTemplateRows: "auto minmax(0, 1fr)",
    gap: "18px",
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
    border: "1px solid rgba(163, 180, 208, 0.14)",
    background: "linear-gradient(180deg, rgba(255, 255, 255, 0.035), rgba(255, 255, 255, 0.012))",
    ...shorthands.borderRadius("24px"),
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
    color: "#eff5ff",
    fontSize: "15px",
    fontWeight: 700,
    letterSpacing: "-0.02em",
  },
  historyMeta: {
    color: "#7f94b6",
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
    border: "1px solid rgba(163, 180, 208, 0.12)",
    backgroundColor: "rgba(255, 255, 255, 0.025)",
    ...shorthands.borderRadius("16px"),
    ...shorthands.padding("10px", "12px"),
  },
  summaryLabel: {
    color: "#7f94b6",
    fontSize: "11px",
    textTransform: "uppercase",
    letterSpacing: "0.08em",
  },
  summaryValue: {
    color: "#eff5ff",
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
    borderBottom: "1px solid rgba(163, 180, 208, 0.08)",
    ...shorthands.padding("0", "0", "8px"),
  },
  recordPrimary: {
    color: "#dce8fb",
    fontSize: "13px",
  },
  recordSecondary: {
    color: "#7f94b6",
    fontSize: "12px",
  },
  preview: {
    margin: 0,
    color: "#dce8fb",
    backgroundColor: "rgba(7, 12, 20, 0.82)",
    border: "1px solid rgba(163, 180, 208, 0.1)",
    fontFamily: "var(--mono)",
    fontSize: "12px",
    lineHeight: 1.7,
    overflowX: "auto",
    maxHeight: "320px",
    whiteSpace: "pre-wrap",
    wordBreak: "break-word",
    ...shorthands.borderRadius("18px"),
    ...shorthands.padding("14px"),
  },
  emptyState: {
    display: "grid",
    placeItems: "center",
    minHeight: "240px",
    color: "#7f94b6",
    textAlign: "center",
    lineHeight: 1.8,
    border: "1px dashed rgba(163, 180, 208, 0.16)",
    ...shorthands.borderRadius("24px"),
    ...shorthands.padding("24px"),
  },
});

interface CommandsPanelProps {
  roomId: string | null;
  readOnly?: boolean;
}

interface CommandHistoryItem {
  definition: ReturnType<typeof getCommandDefinition>;
  response: CommandExecutionResponse;
  args?: Record<string, string>;
}

export default function CommandsPanel({ roomId, readOnly = false }: CommandsPanelProps) {
  const s = useLocalStyles();
  const [selectedCommand, setSelectedCommand] = useState<HumanCommandName>("READ_FILE");
  const [drafts, setDrafts] = useState(createDefaultCommandDrafts);
  const [history, setHistory] = useState<CommandHistoryItem[]>([]);
  const [submitting, setSubmitting] = useState(false);
  const [panelError, setPanelError] = useState<string | null>(null);

  const definition = useMemo(() => getCommandDefinition(selectedCommand), [selectedCommand]);
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
  const syncCount = WEEK1_COMMANDS.filter((command) => !command.isAsync).length;

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
          <div className={s.eyebrow}>Human command surface</div>
          <h2 className={s.heroTitle}>Operate the workspace without dropping into a terminal.</h2>
          <div className={s.heroDescription}>
            Week 1 ships a deliberately small command deck: repository inspection, room awareness,
            review queue access, and build/test runs with polling. Everything here is hard allowlisted,
            audited, and tuned to the human UI contract.
          </div>

          <div className={s.metricRow}>
            <div className={s.metricCard}>
              <div className={s.metricLabel}>Allowlisted</div>
              <div className={s.metricValue}>{WEEK1_COMMANDS.length}</div>
              <div className={s.metricNote}>Focused deck for code reading, git context, and workspace triage.</div>
            </div>
            <div className={s.metricCard}>
              <div className={s.metricLabel}>Immediate</div>
              <div className={s.metricValue}>{syncCount}</div>
              <div className={s.metricNote}>Fast commands return inline JSON results on the first round-trip.</div>
            </div>
            <div className={s.metricCard}>
              <div className={s.metricLabel}>Polling</div>
              <div className={s.metricValue}>2</div>
              <div className={s.metricNote}>Build and test runs stay async, then update here as they complete.</div>
            </div>
          </div>
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
            {WEEK1_COMMANDS.map((command) => (
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
        <div className={s.errorBox}>{item.response.error}</div>
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

function badgeColorForCategory(category: string): "informative" | "success" | "warning" | "important" {
  switch (category) {
    case "code":
      return "informative";
    case "git":
      return "warning";
    case "operations":
      return "important";
    default:
      return "success";
  }
}

function badgeColorForStatus(status: CommandExecutionStatus): "success" | "warning" | "danger" | "important" {
  switch (status) {
    case "completed":
      return "success";
    case "pending":
      return "warning";
    case "denied":
      return "important";
    default:
      return "danger";
  }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function summarizeResult(result: unknown): Array<[string, string]> {
  if (!isRecord(result)) {
    return [];
  }

  const ignoredKeys = new Set(["content", "output", "diff", "matches", "tasks", "rooms", "agents", "commits", "messages"]);

  return Object.entries(result)
    .filter(([key, value]) => !ignoredKeys.has(key) && (typeof value === "string" || typeof value === "number" || typeof value === "boolean"))
    .slice(0, 6)
    .map(([key, value]) => [readableLabel(key), String(value)]);
}

function findPrimaryList(result: unknown): Array<{ primary: string; secondary?: string }> {
  if (!isRecord(result)) {
    return [];
  }

  const candidate = ["matches", "tasks", "rooms", "agents", "commits", "messages"]
    .map((key) => result[key])
    .find(Array.isArray);

  if (!Array.isArray(candidate)) {
    return [];
  }

  return candidate.slice(0, 6).map((entry) => {
    if (isRecord(entry)) {
      const primary = String(
        entry.title ?? entry.name ?? entry.file ?? entry.sender ?? entry.sha ?? entry.id ?? "Result item",
      );
      const secondaryValues = [
        entry.status,
        entry.phase,
        entry.role,
        entry.text,
        entry.message,
        entry.content,
        entry.assignedTo,
        entry.line ? `line ${entry.line}` : undefined,
      ].filter((value): value is unknown => value != null);

      return {
        primary,
        secondary: secondaryValues.length > 0 ? secondaryValues.map((value) => String(value)).join(" · ") : undefined,
      };
    }

    return {
      primary: String(entry),
    };
  });
}

function findPreviewBlock(result: unknown): string | null {
  if (!isRecord(result)) {
    return null;
  }

  for (const key of ["content", "output", "diff"]) {
    const value = result[key];
    if (typeof value === "string" && value.trim()) {
      return value;
    }
  }

  return null;
}

function readableLabel(key: string): string {
  return key
    .replace(/([A-Z])/g, " $1")
    .replace(/^./, (char) => char.toUpperCase());
}
