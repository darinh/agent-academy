import { Spinner } from "@fluentui/react-components";
import { LinkMultipleRegular, CheckmarkCircleRegular, WarningRegular } from "@fluentui/react-icons";
import type { TaskDependencySummary } from "../api";
import V3Badge from "../V3Badge";
import { useTaskDetailStyles } from "./taskDetailStyles";

interface DependenciesSectionProps {
  dependsOn: TaskDependencySummary[];
  dependedOnBy: TaskDependencySummary[];
  loading: boolean;
  onSelectTask?: (taskId: string) => void;
}

export default function DependenciesSection({
  dependsOn,
  dependedOnBy,
  loading,
  onSelectTask,
}: DependenciesSectionProps) {
  const s = useTaskDetailStyles();
  const hasDeps = dependsOn.length > 0 || dependedOnBy.length > 0;

  return (
    <div className={s.commentsSection}>
      <div className={s.sectionLabel}>
        <LinkMultipleRegular fontSize={13} style={{ marginRight: 4 }} />
        Dependencies{" "}
        {hasDeps ? `(${dependsOn.length} upstream · ${dependedOnBy.length} downstream)` : ""}
      </div>
      {loading && <Spinner size="tiny" label="Loading dependencies…" />}
      {!loading && !hasDeps && (
        <div style={{ fontSize: "12px", color: "var(--aa-muted)", marginTop: "4px" }}>
          No dependencies
        </div>
      )}
      {dependsOn.length > 0 && (
        <>
          <div
            style={{
              fontSize: "11px",
              fontWeight: 600,
              color: "var(--aa-muted)",
              marginTop: "6px",
              marginBottom: "2px",
            }}
          >
            Depends on
          </div>
          {dependsOn.map((dep) => (
            <DepRow key={dep.taskId} dep={dep} onSelect={onSelectTask} />
          ))}
        </>
      )}
      {dependedOnBy.length > 0 && (
        <>
          <div
            style={{
              fontSize: "11px",
              fontWeight: 600,
              color: "var(--aa-muted)",
              marginTop: "6px",
              marginBottom: "2px",
            }}
          >
            Depended on by
          </div>
          {dependedOnBy.map((dep) => (
            <DepRow key={dep.taskId} dep={dep} onSelect={onSelectTask} />
          ))}
        </>
      )}
    </div>
  );
}

function DepRow({
  dep,
  onSelect,
}: {
  dep: TaskDependencySummary;
  onSelect?: (taskId: string) => void;
}) {
  const s = useTaskDetailStyles();
  return (
    <div
      className={s.specLinkRow}
      style={{ cursor: onSelect ? "pointer" : undefined }}
      onClick={onSelect ? () => onSelect(dep.taskId) : undefined}
    >
      {dep.isSatisfied ? (
        <CheckmarkCircleRegular fontSize={14} style={{ color: "var(--aa-success, #4caf50)" }} />
      ) : (
        <WarningRegular fontSize={14} style={{ color: "var(--aa-warning, #ff9800)" }} />
      )}
      <V3Badge color={dep.isSatisfied ? "done" : "warn"}>{dep.status}</V3Badge>
      <span style={{ fontSize: "12px", flex: 1, overflow: "hidden", textOverflow: "ellipsis" }}>
        {dep.title}
      </span>
      <span style={{ fontSize: "10px", color: "var(--aa-muted)" }}>{dep.taskId.slice(0, 8)}</span>
    </div>
  );
}
