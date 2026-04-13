import { Button, mergeClasses, Spinner, Textarea } from "@fluentui/react-components";
import {
  CheckmarkCircleRegular,
  ErrorCircleRegular,
  PersonAddRegular,
} from "@fluentui/react-icons";
import type { AgentDefinition } from "../api";
import { type TaskAction, ACTION_META } from "./taskListHelpers";
import { useTaskDetailStyles } from "./taskDetailStyles";

interface TaskActionsBarProps {
  actions: TaskAction[];
  actionPending: TaskAction | null;
  actionResult: { ok: boolean; message: string } | null;
  reasonAction: TaskAction | null;
  reasonText: string;
  onReasonTextChange: (text: string) => void;
  onAction: (action: TaskAction) => void;
  onCancelReason: () => void;

  canAssign: boolean;
  agents: AgentDefinition[];
  showAssignPicker: boolean;
  onToggleAssignPicker: () => void;
  assignPending: boolean;
  onAssign: (agent: AgentDefinition) => void;
}

export default function TaskActionsBar({
  actions, actionPending, actionResult, reasonAction, reasonText,
  onReasonTextChange, onAction, onCancelReason,
  canAssign, agents, showAssignPicker, onToggleAssignPicker, assignPending, onAssign,
}: TaskActionsBarProps) {
  const s = useTaskDetailStyles();
  return (
    <>
      {canAssign && (
        <div className={s.actionBar}>
          <Button
            size="small"
            appearance="primary"
            icon={<PersonAddRegular />}
            onClick={(e) => { e.stopPropagation(); onToggleAssignPicker(); }}
            disabled={assignPending}
          >
            Assign Agent
          </Button>
          {showAssignPicker && (
            <div className={s.assignPicker}>
              {agents.map((agent) => (
                <button
                  key={agent.id}
                  className={s.assignPickerBtn}
                  onClick={(e) => { e.stopPropagation(); onAssign(agent); }}
                  disabled={assignPending}
                >
                  {agent.name} ({agent.role})
                </button>
              ))}
            </div>
          )}
        </div>
      )}

      {actions.length > 0 && (
        <div className={s.actionBar}>
          {actions.map((action) => {
            const meta = ACTION_META[action];
            return (
              <Button
                key={action}
                size="small"
                appearance={meta.appearance}
                icon={meta.icon}
                disabled={actionPending != null || (reasonAction != null && reasonAction !== action)}
                onClick={(e) => { e.stopPropagation(); onAction(action); }}
              >
                {actionPending === action ? <Spinner size="tiny" /> : meta.label}
              </Button>
            );
          })}
        </div>
      )}

      {reasonAction && (
        <div className={s.reasonArea}>
          <Textarea
            placeholder={reasonAction === "requestChanges" ? "Describe the changes needed…" : "Reason for rejection…"}
            value={reasonText}
            onChange={(_, data) => onReasonTextChange(data.value)}
            rows={3}
            style={{ fontSize: "13px" }}
          />
          <div className={s.reasonActions}>
            <Button size="small" appearance="subtle" onClick={onCancelReason}>Cancel</Button>
            <Button
              size="small"
              appearance="primary"
              disabled={!reasonText.trim() || actionPending != null}
              onClick={() => onAction(reasonAction)}
            >
              {actionPending ? <Spinner size="tiny" /> : `Submit ${ACTION_META[reasonAction].label}`}
            </Button>
          </div>
        </div>
      )}

      {actionResult && (
        <div className={mergeClasses(s.actionFeedback, actionResult.ok ? s.actionSuccess : s.actionError)}>
          {actionResult.ok ? <CheckmarkCircleRegular fontSize={14} /> : <ErrorCircleRegular fontSize={14} />}
          {actionResult.message}
        </div>
      )}
    </>
  );
}
