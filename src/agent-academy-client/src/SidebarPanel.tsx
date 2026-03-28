import { memo, useState, useEffect } from "react";
import type { FormEvent } from "react";
import {
  Button,
  Field,
  Input,
  mergeClasses,
  Spinner,
  Textarea,
} from "@fluentui/react-components";
import { useStyles } from "./useStyles";
import { initials } from "./utils";
import type { AgentPresence, RoomSnapshot } from "./api";
import type { TaskDraft } from "./useWorkspace";

const PHASE_DOT_COLORS: Record<string, string> = {
  Intake: "#94a3b8",
  Planning: "#6cb6ff",
  Discussion: "#a78bfa",
  Implementation: "#34d399",
  Validation: "#fbbf24",
  FinalSynthesis: "#f472b6",
};

/* ── Task Composer ───────────────────────────────────────────────── */

const TaskComposer = memo(function TaskComposer(props: {
  room: RoomSnapshot | null;
  onSubmitTask: (draft: TaskDraft) => Promise<boolean>;
}) {
  const s = useStyles();
  const [title, setTitle] = useState("");
  const [description, setDescription] = useState("");
  const [successCriteria, setSuccessCriteria] = useState("");
  const [submitting, setSubmitting] = useState(false);

  useEffect(() => {
    setTitle("");
    setDescription("");
    setSuccessCriteria("");
  }, [props.room?.id]);

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (submitting) return;
    setSubmitting(true);
    try {
      const created = await props.onSubmitTask({
        title,
        description,
        successCriteria,
        roomId: props.room?.id,
      });
      if (created) {
        setTitle("");
        setDescription("");
        setSuccessCriteria("");
      }
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <div className={s.taskCard}>
      <div className={s.taskCardTitle}>Start a new task</div>
      <form className={s.form} onSubmit={(e) => void handleSubmit(e)}>
        <Field label="Title" required size="small">
          <Input className={s.fieldInput} value={title} onChange={(_, d) => setTitle(d.value)} />
        </Field>
        <Field label="Description" required size="small">
          <Textarea
            className={s.fieldInput}
            value={description}
            onChange={(_, d) => setDescription(d.value)}
            resize="vertical"
            rows={3}
          />
        </Field>
        <Field label="Success criteria" size="small">
          <Textarea
            className={s.fieldInput}
            value={successCriteria}
            onChange={(_, d) => setSuccessCriteria(d.value)}
            resize="vertical"
            rows={2}
          />
        </Field>
        <Button appearance="primary" type="submit" disabled={submitting}>
          {submitting ? "Submitting…" : props.room?.activeTask ? "Replace task" : props.room ? "Assign in room" : "Create room"}
        </Button>
      </form>
    </div>
  );
});

/* ── Sidebar Panel ───────────────────────────────────────────────── */

const SidebarPanel = memo(function SidebarPanel(props: {
  sidebarOpen: boolean;
  busy: boolean;
  rooms: RoomSnapshot[];
  room: RoomSnapshot | null;
  roster: AgentPresence[];
  onRefresh: () => void;
  onToggleSidebar: () => void;
  onSelectRoom: (roomId: string) => void;
  onSubmitTask: (draft: TaskDraft) => Promise<boolean>;
  onSwitchProject?: () => void;
  workspace?: { name: string; path: string } | null;
}) {
  const s = useStyles();
  const phaseDotColor = PHASE_DOT_COLORS[props.room?.currentPhase ?? ""] ?? "#94a3b8";

  return (
    <aside className={mergeClasses(s.sidebar, !props.sidebarOpen && s.sidebarCollapsed)}>
      {/* Header */}
      <div className={s.sidebarHeader}>
        <div className={s.sidebarToolbar}>
          {props.sidebarOpen ? (
            <div className={s.appTitle}>Agent Academy</div>
          ) : (
            <div className={s.eyebrow}>Live</div>
          )}
          <div style={{ display: "flex", alignItems: "center", gap: "6px" }}>
            {props.busy && <Spinner size="tiny" />}
            {props.sidebarOpen && (
              <Button appearance="subtle" size="small" onClick={props.onRefresh} aria-label="Refresh">
                ↻
              </Button>
            )}
            <Button
              appearance="subtle"
              size="small"
              onClick={props.onToggleSidebar}
              aria-label={props.sidebarOpen ? "Collapse sidebar" : "Expand sidebar"}
            >
              {props.sidebarOpen ? "◁" : "▷"}
            </Button>
          </div>
        </div>

        {props.sidebarOpen && props.workspace && (
          <div style={{ marginTop: "4px" }}>
            <div style={{ display: "flex", alignItems: "center", gap: "6px", fontSize: "11px", color: "#7c90b2", padding: "0 12px" }}>
              <span>📁</span>
              <span style={{ overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap", flex: 1 }} title={props.workspace.path}>
                {props.workspace.name}
              </span>
              {props.onSwitchProject && (
                <Button
                  appearance="transparent"
                  size="small"
                  onClick={props.onSwitchProject}
                  aria-label="Switch project"
                  style={{ minWidth: "auto", padding: "0 4px", fontSize: "11px", color: "#7c90b2", height: "20px" }}
                >
                  ⇄
                </Button>
              )}
            </div>
          </div>
        )}
      </div>

      {/* Body */}
      {props.sidebarOpen ? (
        <div className={s.sidebarBody}>
          {props.room && (
            <div>
              <div className={s.sidebarRoomHeader}>
                <span className={s.sidebarRoomDot} style={{ backgroundColor: phaseDotColor }} />
                <span>{props.room.name}</span>
              </div>
              {props.roster.map((agent) => (
                <div key={agent.agentId} className={s.agentListItem}>
                  <span className={s.agentStateDot} style={{ backgroundColor: "#34d399" }} />
                  <span>{agent.name}</span>
                  {agent.role && <span style={{ color: "#7c90b2", fontSize: "11px" }}>· {agent.role}</span>}
                </div>
              ))}
            </div>
          )}

          {props.rooms.length > 1 && (
            <section className={s.section}>
              <div className={s.sectionHeader}>
                <div className={s.sectionLabel}>All rooms</div>
              </div>
              <div className={s.roomList}>
                {props.rooms.map((candidate) => {
                  const dotColor = PHASE_DOT_COLORS[candidate.currentPhase] ?? "#94a3b8";
                  return (
                    <button
                      key={candidate.id}
                      className={mergeClasses(s.roomButton, s.roomButtonHover, props.room?.id === candidate.id ? s.roomButtonActive : undefined)}
                      onClick={() => props.onSelectRoom(candidate.id)}
                      aria-label={`Select room ${candidate.name}`}
                      type="button"
                    >
                      <div className={s.roomButtonIcon}>{initials(candidate.name)}</div>
                      <div style={{ display: "flex", alignItems: "center", gap: "8px" }}>
                        <div className={s.roomButtonName}>{candidate.name}</div>
                        <span style={{ width: "6px", height: "6px", borderRadius: "999px", backgroundColor: dotColor, flexShrink: 0 }} />
                      </div>
                    </button>
                  );
                })}
              </div>
            </section>
          )}

          <section className={s.section}>
            <TaskComposer room={props.room} onSubmitTask={props.onSubmitTask} />
          </section>
        </div>
      ) : (
        <div className={s.compactSidebar}>
          {props.rooms.map((candidate) => (
            <button
              key={candidate.id}
              className={mergeClasses(s.compactButton, props.room?.id === candidate.id ? s.compactButtonActive : undefined)}
              onClick={() => props.onSelectRoom(candidate.id)}
              title={candidate.name}
              aria-label={candidate.name}
              type="button"
            >
              {initials(candidate.name)}
            </button>
          ))}
        </div>
      )}
    </aside>
  );
});

export default SidebarPanel;
