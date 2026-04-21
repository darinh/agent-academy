import { useState, useCallback } from "react";
import { Button, Spinner, mergeClasses } from "@fluentui/react-components";
import {
  CheckmarkCircleRegular,
  ErrorCircleRegular,
  ArrowCounterclockwiseRegular,
} from "@fluentui/react-icons";
import type { TaskSnapshot, TaskStatus, TaskPriority } from "../api";
import { updateTaskStatus, updateTaskPriority, completeTask } from "../api";
import V3Badge from "../V3Badge";
import { statusBadgeColor, priorityBadgeColor } from "./taskListHelpers";
import { useTaskDetailStyles } from "./taskDetailStyles";

const SAFE_STATUSES: TaskStatus[] = ["Queued", "Active", "Blocked", "AwaitingValidation", "InReview"];
const ALL_PRIORITIES: TaskPriority[] = ["Critical", "High", "Medium", "Low"];

interface TaskPropertyControlsProps {
  task: TaskSnapshot;
  onRefresh: () => void;
}

export default function TaskPropertyControls({ task, onRefresh }: TaskPropertyControlsProps) {
  const s = useTaskDetailStyles();
  const [pending, setPending] = useState<"status" | "priority" | "complete" | null>(null);
  const [result, setResult] = useState<{ ok: boolean; message: string } | null>(null);
  const [showStatusPicker, setShowStatusPicker] = useState(false);
  const [showPriorityPicker, setShowPriorityPicker] = useState(false);
  const [showCompleteForm, setShowCompleteForm] = useState(false);
  const [commitCount, setCommitCount] = useState("");

  const isTerminal = task.status === "Completed" || task.status === "Cancelled";

  const handleStatusChange = useCallback(async (status: TaskStatus) => {
    setPending("status");
    setResult(null);
    setShowStatusPicker(false);
    try {
      await updateTaskStatus(task.id, status);
      setResult({ ok: true, message: `Status → ${status}` });
      onRefresh();
    } catch (err) {
      setResult({ ok: false, message: err instanceof Error ? err.message : "Failed" });
    } finally {
      setPending(null);
    }
  }, [task.id, onRefresh]);

  const handlePriorityChange = useCallback(async (priority: TaskPriority) => {
    setPending("priority");
    setResult(null);
    setShowPriorityPicker(false);
    try {
      await updateTaskPriority(task.id, priority);
      setResult({ ok: true, message: `Priority → ${priority}` });
      onRefresh();
    } catch (err) {
      setResult({ ok: false, message: err instanceof Error ? err.message : "Failed" });
    } finally {
      setPending(null);
    }
  }, [task.id, onRefresh]);

  const handleComplete = useCallback(async () => {
    const count = parseInt(commitCount, 10);
    if (isNaN(count) || count < 0) {
      setResult({ ok: false, message: "Commit count must be a non-negative number" });
      return;
    }
    setPending("complete");
    setResult(null);
    try {
      await completeTask(task.id, count);
      setResult({ ok: true, message: "Task completed" });
      setShowCompleteForm(false);
      setCommitCount("");
      onRefresh();
    } catch (err) {
      setResult({ ok: false, message: err instanceof Error ? err.message : "Failed" });
    } finally {
      setPending(null);
    }
  }, [task.id, commitCount, onRefresh]);

  if (isTerminal) return null;

  return (
    <div className={s.actionBar}>
      {/* Status changer */}
      <div style={{ position: "relative" }}>
        <Button
          size="small"
          appearance="subtle"
          icon={pending === "status" ? <Spinner size="tiny" /> : <ArrowCounterclockwiseRegular />}
          disabled={pending != null}
          onClick={(e) => { e.stopPropagation(); setShowStatusPicker(!showStatusPicker); setShowPriorityPicker(false); setShowCompleteForm(false); }}
        >
          Status: {task.status}
        </Button>
        {showStatusPicker && (
          <div className={s.assignPicker}>
            {SAFE_STATUSES.filter((st) => st !== task.status).map((st) => (
              <button
                key={st}
                className={s.assignPickerBtn}
                onClick={(e) => { e.stopPropagation(); handleStatusChange(st); }}
                disabled={pending != null}
              >
                <V3Badge color={statusBadgeColor(st)}>{st}</V3Badge>
              </button>
            ))}
          </div>
        )}
      </div>

      {/* Priority changer */}
      <div style={{ position: "relative" }}>
        <Button
          size="small"
          appearance="subtle"
          disabled={pending != null}
          onClick={(e) => { e.stopPropagation(); setShowPriorityPicker(!showPriorityPicker); setShowStatusPicker(false); setShowCompleteForm(false); }}
        >
          Priority: {task.priority ?? "Medium"}
        </Button>
        {showPriorityPicker && (
          <div className={s.assignPicker}>
            {ALL_PRIORITIES.filter((p) => p !== task.priority).map((p) => (
              <button
                key={p}
                className={s.assignPickerBtn}
                onClick={(e) => { e.stopPropagation(); handlePriorityChange(p); }}
                disabled={pending != null}
              >
                <V3Badge color={priorityBadgeColor(p)}>{p}</V3Badge>
              </button>
            ))}
          </div>
        )}
      </div>

      {/* Complete button */}
      {!showCompleteForm ? (
        <Button
          size="small"
          appearance="primary"
          icon={<CheckmarkCircleRegular />}
          disabled={pending != null}
          onClick={(e) => { e.stopPropagation(); setShowCompleteForm(true); setShowStatusPicker(false); setShowPriorityPicker(false); }}
        >
          Complete
        </Button>
      ) : (
        <div className={s.reasonArea}>
          <label style={{ fontSize: "12px", color: "var(--aa-muted)" }}>
            Commit count
            <input
              type="number"
              min="0"
              value={commitCount}
              onChange={(e) => setCommitCount(e.target.value)}
              onClick={(e) => e.stopPropagation()}
              style={{
                marginLeft: 8,
                width: 80,
                padding: "4px 8px",
                fontSize: "13px",
                background: "var(--aa-surface)",
                border: "1px solid var(--aa-border)",
                borderRadius: 4,
                color: "var(--aa-text-strong)",
              }}
            />
          </label>
          <div className={s.reasonActions}>
            <Button size="small" appearance="subtle" onClick={() => { setShowCompleteForm(false); setCommitCount(""); }}>
              Cancel
            </Button>
            <Button
              size="small"
              appearance="primary"
              disabled={pending != null || !commitCount.trim()}
              onClick={(e) => { e.stopPropagation(); handleComplete(); }}
            >
              {pending === "complete" ? <Spinner size="tiny" /> : "Complete Task"}
            </Button>
          </div>
        </div>
      )}

      {result && (
        <div className={mergeClasses(s.actionFeedback, result.ok ? s.actionSuccess : s.actionError)}>
          {result.ok ? <CheckmarkCircleRegular fontSize={14} /> : <ErrorCircleRegular fontSize={14} />}
          {result.message}
        </div>
      )}
    </div>
  );
}
