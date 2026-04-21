import { useEffect, useRef, useMemo, useState } from "react";
import {
  Button,
  Input,
  mergeClasses,
  Spinner,
  Textarea,
} from "@fluentui/react-components";
import { PlayRegular, WarningRegular } from "@fluentui/react-icons";
import V3Badge from "./V3Badge";
import ConfirmDialog from "./ConfirmDialog";
import {
  executeCommand,
  getCommandExecution,
  getCommandMetadata,
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
  MAX_HISTORY_ITEMS,
  POLL_INTERVAL_MS,
} from "./commandsPanelUtils";
import { CommandResultCard, useCommandsPanelStyles } from "./commands";
import type { CommandHistoryItem } from "./commands";

interface CommandsPanelProps {
  roomId: string | null;
  readOnly?: boolean;
}

export default function CommandsPanel({ roomId, readOnly = false }: CommandsPanelProps) {
  const s = useCommandsPanelStyles();
  const [commands, setCommands] = useState<readonly HumanCommandDefinition[]>(WEEK1_COMMANDS);
  const [, setMetadataLoaded] = useState(false);
  const [selectedCommand, setSelectedCommand] = useState<string>("READ_FILE");
  const [drafts, setDrafts] = useState(() => createDefaultCommandDrafts());
  const [history, setHistory] = useState<CommandHistoryItem[]>([]);
  const [submitting, setSubmitting] = useState(false);
  const [panelError, setPanelError] = useState<string | null>(null);
  const [confirmOpen, setConfirmOpen] = useState(false);
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

  const submitCommand = async (confirm: boolean) => {
    setSubmitting(true);
    setPanelError(null);

    try {
      const request = buildExecuteCommandRequest(definition, draft, confirm ? { confirm: true } : undefined);
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

    if (definition.isDestructive) {
      setConfirmOpen(true);
      return;
    }

    await submitCommand(false);
  };

  const handleConfirm = () => {
    setConfirmOpen(false);
    if (readOnly) {
      setPanelError("Commands are paused while Copilot is degraded. Reconnect before running new work.");
      return;
    }
    void submitCommand(true);
  };

  const handleCancelConfirm = () => {
    setConfirmOpen(false);
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
                  <V3Badge color={badgeColorForCategory(command.category)}>
                    {command.category}
                  </V3Badge>
                  <div style={{ display: "flex", gap: "6px", alignItems: "center", flexWrap: "wrap" }}>
                    {command.isDestructive && (
                      <V3Badge color="err">destructive</V3Badge>
                    )}
                    <V3Badge color={command.isAsync ? "warn" : "info"}>
                      {command.isAsync ? "polling" : "instant"}
                    </V3Badge>
                  </div>
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

          {definition.isDestructive && definition.destructiveWarning && (
            <div className={s.destructiveWarning}>
              <WarningRegular style={{ flexShrink: 0 }} />
              <span>{definition.destructiveWarning}</span>
            </div>
          )}

          <div className={s.actionRow}>
            <span className={mergeClasses(s.helperText, readOnly && s.helperWarning)}>
              {readOnly
                ? "Limited mode is active. You can inspect the command deck, but new executions are disabled until Copilot returns to operational."
                : definition.isAsync
                  ? "This command returns immediately, then polls for a final result."
                  : "This command returns a result directly in the panel."}
            </span>
            <Button
              appearance="primary"
              icon={submitting ? <Spinner size="tiny" /> : definition.isDestructive ? <WarningRegular /> : <PlayRegular />}
              disabled={submitting || readOnly}
              onClick={() => void handleExecute()}
            >
              {submitting ? "Running…" : `Run ${definition.title}`}
            </Button>
          </div>

          <ConfirmDialog
            open={confirmOpen}
            onConfirm={handleConfirm}
            onCancel={handleCancelConfirm}
            title={`Confirm ${definition.title}`}
            message={definition.destructiveWarning ?? `${definition.title} performs a destructive action. Are you sure?`}
            confirmLabel="Yes, proceed"
            confirmAppearance="primary"
            cancelLabel="Cancel"
          />
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
            <V3Badge color="info">latest first</V3Badge>
            <V3Badge color="warn">{pendingCount} pending</V3Badge>
            <V3Badge color="ok">audited</V3Badge>
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
