import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Input,
  Spinner,
  makeStyles,
  shorthands,
} from "@fluentui/react-components";
import type { TaskSnapshot } from "../api";
import { getTasks, updateTaskSprint } from "../api";

// ── Styles ──────────────────────────────────────────────────────────────

const useStyles = makeStyles({
  section: {
    display: "grid",
    gap: "12px",
  },
  header: {
    display: "flex",
    alignItems: "center",
    justifyContent: "space-between",
    gap: "12px",
  },
  sectionTitle: {
    fontFamily: "var(--mono)",
    fontSize: "11px",
    fontWeight: 600,
    textTransform: "uppercase",
    letterSpacing: "0.08em",
    color: "var(--aa-muted)",
  },
  list: {
    display: "grid",
    gap: "8px",
  },
  card: {
    display: "flex",
    alignItems: "center",
    justifyContent: "space-between",
    gap: "12px",
    ...shorthands.padding("12px", "14px"),
    background: "var(--aa-surface)",
    ...shorthands.borderRadius("6px"),
    ...shorthands.border("1px", "solid", "var(--aa-border)"),
  },
  cardBody: {
    display: "grid",
    gap: "4px",
    minWidth: 0,
  },
  cardTitle: {
    color: "var(--aa-text-strong)",
    fontWeight: 600,
    fontSize: "14px",
    overflow: "hidden",
    textOverflow: "ellipsis",
    whiteSpace: "nowrap",
  },
  cardMeta: {
    color: "var(--aa-muted)",
    fontFamily: "var(--mono)",
    fontSize: "11px",
  },
  empty: {
    color: "var(--aa-muted)",
    fontSize: "13px",
    fontStyle: "italic",
  },
  error: {
    color: "var(--aa-danger, #f87171)",
    fontSize: "13px",
  },
  dialogSurface: {
    background:
      "linear-gradient(180deg, rgba(18, 23, 33, 0.98), rgba(9, 12, 18, 0.98))",
    border: "1px solid var(--aa-border)",
    borderRadius: "20px",
    color: "var(--aa-text)",
    maxWidth: "560px",
    width: "min(560px, 92vw)",
  },
  dialogContent: {
    display: "grid",
    gap: "12px",
    color: "var(--aa-muted)",
    maxHeight: "60vh",
    overflowY: "auto",
  },
  pickerRow: {
    display: "flex",
    alignItems: "center",
    justifyContent: "space-between",
    gap: "12px",
    ...shorthands.padding("8px", "10px"),
    ...shorthands.borderRadius("6px"),
    ":hover": { background: "var(--aa-surface)" },
  },
  pickerEmpty: {
    color: "var(--aa-muted)",
    fontSize: "13px",
    textAlign: "center",
    ...shorthands.padding("16px"),
  },
});

// ── Component ───────────────────────────────────────────────────────────

interface SprintTasksProps {
  sprintId: string;
}

export default function SprintTasks({ sprintId }: SprintTasksProps) {
  const s = useStyles();

  const [sprintTasks, setSprintTasks] = useState<TaskSnapshot[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [busyTaskId, setBusyTaskId] = useState<string | null>(null);

  const [pickerOpen, setPickerOpen] = useState(false);
  const [pickerLoading, setPickerLoading] = useState(false);
  const [pickerError, setPickerError] = useState<string | null>(null);
  const [allTasks, setAllTasks] = useState<TaskSnapshot[]>([]);
  const [pickerFilter, setPickerFilter] = useState("");
  const [pickerBusyId, setPickerBusyId] = useState<string | null>(null);

  // Monotonic token to discard stale getTasks(sprintId) responses when the
  // selected sprint changes mid-flight or refresh is called concurrently.
  const refreshTokenRef = useRef(0);
  const mountedRef = useRef(true);

  useEffect(() => {
    mountedRef.current = true;
    return () => { mountedRef.current = false; };
  }, []);

  const refresh = useCallback(async () => {
    const token = ++refreshTokenRef.current;
    setLoading(true);
    setError(null);
    try {
      const list = await getTasks(sprintId);
      if (!mountedRef.current || token !== refreshTokenRef.current) return;
      setSprintTasks(list);
    } catch (err) {
      if (!mountedRef.current || token !== refreshTokenRef.current) return;
      setError(err instanceof Error ? err.message : "Failed to load tasks");
    } finally {
      if (mountedRef.current && token === refreshTokenRef.current) {
        setLoading(false);
      }
    }
  }, [sprintId]);

  useEffect(() => {
    refresh();
  }, [refresh]);

  const openPicker = useCallback(async () => {
    setPickerOpen(true);
    setPickerLoading(true);
    setPickerError(null);
    setPickerFilter("");
    try {
      const list = await getTasks();
      setAllTasks(list);
    } catch (err) {
      setPickerError(err instanceof Error ? err.message : "Failed to load tasks");
    } finally {
      setPickerLoading(false);
    }
  }, []);

  const closePicker = useCallback(() => {
    setPickerOpen(false);
    setPickerBusyId(null);
  }, []);

  const handleAdd = useCallback(
    async (taskId: string) => {
      setPickerBusyId(taskId);
      try {
        await updateTaskSprint(taskId, sprintId);
        await refresh();
        // Reflect the change in the picker without re-fetching the full list
        setAllTasks((prev) =>
          prev.map((t) => (t.id === taskId ? { ...t, sprintId } : t)),
        );
      } catch (err) {
        setPickerError(err instanceof Error ? err.message : "Failed to add task");
      } finally {
        setPickerBusyId(null);
      }
    },
    [sprintId, refresh],
  );

  const handleRemove = useCallback(
    async (taskId: string) => {
      setBusyTaskId(taskId);
      try {
        await updateTaskSprint(taskId, null);
        await refresh();
      } catch (err) {
        setError(err instanceof Error ? err.message : "Failed to remove task");
      } finally {
        setBusyTaskId(null);
      }
    },
    [refresh],
  );

  const candidates = useMemo(() => {
    const q = pickerFilter.trim().toLowerCase();
    return allTasks
      .filter((t) => t.sprintId !== sprintId)
      .filter((t) => {
        if (!q) return true;
        return (
          t.title.toLowerCase().includes(q) ||
          t.id.toLowerCase().includes(q)
        );
      });
  }, [allTasks, pickerFilter, sprintId]);

  return (
    <section className={s.section} aria-label="Sprint tasks">
      <div className={s.header}>
        <span className={s.sectionTitle}>
          Tasks ({loading ? "…" : sprintTasks.length})
        </span>
        <Button
          appearance="primary"
          size="small"
          onClick={openPicker}
          data-testid="sprint-tasks-add"
        >
          Add task to sprint
        </Button>
      </div>

      {error && <div className={s.error}>{error}</div>}

      {loading ? (
        <Spinner size="tiny" label="Loading tasks…" />
      ) : sprintTasks.length === 0 ? (
        <div className={s.empty}>
          No tasks linked to this sprint yet.
        </div>
      ) : (
        <div className={s.list}>
          {sprintTasks.map((task) => (
            <div key={task.id} className={s.card} data-testid="sprint-task-row">
              <div className={s.cardBody}>
                <div className={s.cardTitle} title={task.title}>{task.title}</div>
                <div className={s.cardMeta}>
                  {task.status} · {task.priority} · {task.id}
                </div>
              </div>
              <Button
                appearance="subtle"
                size="small"
                disabled={busyTaskId === task.id}
                onClick={() => handleRemove(task.id)}
                data-testid="sprint-task-remove"
              >
                {busyTaskId === task.id ? "Removing…" : "Remove"}
              </Button>
            </div>
          ))}
        </div>
      )}

      <Dialog
        open={pickerOpen}
        onOpenChange={(_, data) => { if (!data.open) closePicker(); }}
      >
        <DialogSurface className={s.dialogSurface}>
          <DialogBody>
            <DialogTitle>Add task to sprint</DialogTitle>
            <DialogContent className={s.dialogContent}>
              <Input
                placeholder="Filter tasks by title or id…"
                value={pickerFilter}
                onChange={(_, data) => setPickerFilter(data.value)}
                data-testid="sprint-tasks-filter"
              />
              {pickerError && <div className={s.error}>{pickerError}</div>}
              {pickerLoading ? (
                <Spinner size="tiny" label="Loading…" />
              ) : candidates.length === 0 ? (
                <div className={s.pickerEmpty}>No candidate tasks.</div>
              ) : (
                <div className={s.list}>
                  {candidates.map((task) => (
                    <div
                      key={task.id}
                      className={s.pickerRow}
                      data-testid="sprint-tasks-candidate"
                    >
                      <div className={s.cardBody}>
                        <div className={s.cardTitle} title={task.title}>{task.title}</div>
                        <div className={s.cardMeta}>
                          {task.status} · {task.priority}
                          {task.sprintId ? ` · in sprint ${task.sprintId}` : ""}
                        </div>
                      </div>
                      <Button
                        appearance="primary"
                        size="small"
                        disabled={pickerBusyId === task.id}
                        onClick={() => handleAdd(task.id)}
                      >
                        {pickerBusyId === task.id ? "Adding…" : "Add"}
                      </Button>
                    </div>
                  ))}
                </div>
              )}
            </DialogContent>
            <DialogActions>
              <Button appearance="subtle" onClick={closePicker}>
                Close
              </Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
    </section>
  );
}
