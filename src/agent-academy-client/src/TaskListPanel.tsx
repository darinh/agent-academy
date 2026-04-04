import { useState } from "react";
import {
  Badge,
  Card,
  CardHeader,
  makeStyles,
  shorthands,
} from "@fluentui/react-components";
import {
  TaskListLtrRegular,
  PersonRegular,
  ArrowSyncRegular,
  CheckmarkCircleRegular,
  CircleRegular,
  ErrorCircleRegular,
  ClockRegular,
  BranchRegular,
  LinkRegular,
  ChevronDownRegular,
  ChevronRightRegular,
} from "@fluentui/react-icons";
import type { TaskSnapshot, TaskStatus, TaskSize, PullRequestStatus } from "./api";

// -- Styles --

const useLocalStyles = makeStyles({
  root: {
    display: "flex",
    flexDirection: "column",
    height: "100%",
    overflow: "auto",
    gap: "20px",
    ...shorthands.padding("2px"),
  },
  sectionHeader: {
    display: "flex",
    alignItems: "center",
    gap: "10px",
    cursor: "pointer",
    userSelect: "none",
    fontSize: "12px",
    fontWeight: 680,
    color: "var(--aa-soft)",
    textTransform: "uppercase",
    letterSpacing: "0.14em",
    ...shorthands.padding("6px", "4px"),
  },
  sectionCount: {
    color: "var(--aa-muted)",
    fontWeight: 400,
  },
  card: {
    border: "1px solid rgba(214, 188, 149, 0.14)",
    background:
      "linear-gradient(180deg, rgba(255, 244, 227, 0.05), rgba(255, 255, 255, 0.018) 42%, rgba(12, 15, 22, 0.72))",
    boxShadow: "inset 0 1px 0 rgba(255, 244, 227, 0.05)",
    ...shorthands.borderRadius("24px"),
    ...shorthands.padding("18px", "20px"),
    marginBottom: "10px",
  },
  cardTitle: {
    fontSize: "16px",
    fontWeight: 650,
    color: "var(--aa-text-strong)",
    letterSpacing: "-0.03em",
  },
  meta: {
    display: "flex",
    flexWrap: "wrap",
    alignItems: "center",
    gap: "8px",
    marginTop: "8px",
    fontSize: "12px",
    color: "var(--aa-muted)",
  },
  metaItem: {
    display: "flex",
    alignItems: "center",
    gap: "4px",
  },
  prLink: {
    color: "var(--aa-cyan)",
    textDecoration: "none",
    display: "inline-flex",
    alignItems: "center",
    gap: "3px",
    ":hover": { textDecoration: "underline" },
  },
  sizeBadge: {
    fontWeight: 700,
    fontSize: "11px",
    minWidth: "28px",
    textAlign: "center" as const,
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
  emptyIcon: { fontSize: "48px" },
});

// -- Helpers --

type BadgeColor = "informative" | "success" | "warning" | "important" | "danger" | "subtle";

const IN_PROGRESS_STATUSES: TaskStatus[] = [
  "Active", "InReview", "ChangesRequested", "Approved", "Merging", "AwaitingValidation",
];
const PENDING_STATUSES: TaskStatus[] = ["Queued", "Blocked"];
const COMPLETED_STATUSES: TaskStatus[] = ["Completed", "Cancelled"];

function statusColor(status: TaskStatus): BadgeColor {
  switch (status) {
    case "Active": case "Merging":       return "success";
    case "InReview": case "Approved":    return "informative";
    case "AwaitingValidation":           return "warning";
    case "ChangesRequested":             return "warning";
    case "Blocked":                      return "danger";
    case "Completed":                    return "success";
    case "Cancelled":                    return "subtle";
    case "Queued": default:              return "important";
  }
}

function statusIcon(status: TaskStatus) {
  switch (status) {
    case "Active": case "Merging":       return <ArrowSyncRegular />;
    case "InReview": case "Approved":    return <ClockRegular />;
    case "AwaitingValidation":           return <ClockRegular />;
    case "ChangesRequested":             return <ErrorCircleRegular />;
    case "Completed":                    return <CheckmarkCircleRegular />;
    case "Blocked":                      return <ErrorCircleRegular />;
    default:                             return <CircleRegular />;
  }
}

function sizeColor(size: TaskSize): BadgeColor {
  switch (size) {
    case "XS": case "S": return "subtle";
    case "M":            return "informative";
    case "L": case "XL": return "warning";
  }
}

function prStatusLabel(status: PullRequestStatus): string {
  switch (status) {
    case "Open":              return "Open";
    case "ReviewRequested":   return "Review Requested";
    case "ChangesRequested":  return "Changes Requested";
    case "Approved":          return "Approved";
    case "Merged":            return "Merged";
    case "Closed":            return "Closed";
  }
}

function isSafeUrl(url: string): boolean {
  try {
    const parsed = new URL(url);
    return parsed.protocol === "http:" || parsed.protocol === "https:";
  } catch { return false; }
}

function formatDuration(start: string, end?: string | null): string {
  const ms = (end ? new Date(end).getTime() : Date.now()) - new Date(start).getTime();
  const mins = Math.floor(ms / 60000);
  if (mins < 60) return `${mins}m`;
  const hours = Math.floor(mins / 60);
  if (hours < 24) return `${hours}h ${mins % 60}m`;
  const days = Math.floor(hours / 24);
  return `${days}d ${hours % 24}h`;
}

// -- Components --

function TaskCard({ task }: { task: TaskSnapshot }) {
  const s = useLocalStyles();

  return (
    <Card className={s.card}>
      <CardHeader
        image={statusIcon(task.status)}
        header={
          <span className={s.cardTitle}>
            {task.size && (
              <Badge
                className={s.sizeBadge}
                appearance="filled"
                color={sizeColor(task.size)}
                size="small"
              >
                {task.size}
              </Badge>
            )}{" "}
            {task.title}
          </span>
        }
        description={
          <Badge appearance="filled" color={statusColor(task.status)} size="small">
            {task.status}
          </Badge>
        }
      />
      <div className={s.meta}>
        {task.assignedAgentName && (
          <span className={s.metaItem}>
            <PersonRegular fontSize={14} />
            {task.assignedAgentName}
          </span>
        )}
        {task.branchName && (
          <span className={s.metaItem}>
            <BranchRegular fontSize={14} />
            {task.branchName}
          </span>
        )}
        {task.pullRequestUrl && task.pullRequestNumber != null && isSafeUrl(task.pullRequestUrl) && (
          <a
            className={s.prLink}
            href={task.pullRequestUrl}
            target="_blank"
            rel="noopener noreferrer"
          >
            <LinkRegular fontSize={14} />
            PR #{task.pullRequestNumber}
            {task.pullRequestStatus && ` · ${prStatusLabel(task.pullRequestStatus)}`}
          </a>
        )}
        {(task.commitCount ?? 0) > 0 && (
          <span className={s.metaItem}>
            {task.commitCount} commit{task.commitCount !== 1 ? "s" : ""}
          </span>
        )}
        {task.startedAt && (
          <span className={s.metaItem}>
            <ClockRegular fontSize={14} />
            {formatDuration(task.startedAt, task.completedAt)}
          </span>
        )}
        {task.usedFleet && (
          <Badge appearance="outline" color="informative" size="small">Fleet</Badge>
        )}
      </div>
    </Card>
  );
}

// -- Main Component --

interface TaskListPanelProps {
  tasks: TaskSnapshot[];
  error?: boolean;
}

export default function TaskListPanel({ tasks, error }: TaskListPanelProps) {
  const s = useLocalStyles();
  const [completedOpen, setCompletedOpen] = useState(false);

  if (error) {
    return (
      <div className={s.empty}>
        <ErrorCircleRegular className={s.emptyIcon} />
        <span>Failed to load tasks</span>
      </div>
    );
  }

  if (tasks.length === 0) {
    return (
      <div className={s.empty}>
        <TaskListLtrRegular className={s.emptyIcon} />
        <span>No tasks yet</span>
      </div>
    );
  }

  const inProgress = tasks
    .filter((t) => IN_PROGRESS_STATUSES.includes(t.status))
    .sort((a, b) => new Date(b.updatedAt).getTime() - new Date(a.updatedAt).getTime());

  const pending = tasks
    .filter((t) => PENDING_STATUSES.includes(t.status))
    .sort((a, b) => new Date(b.updatedAt).getTime() - new Date(a.updatedAt).getTime());

  const completed = tasks
    .filter((t) => COMPLETED_STATUSES.includes(t.status))
    .sort((a, b) => new Date(b.updatedAt).getTime() - new Date(a.updatedAt).getTime());

  return (
    <div className={s.root}>
      {inProgress.length > 0 && (
        <div>
          <div className={s.sectionHeader}>
            <ArrowSyncRegular fontSize={16} />
            In Progress
            <span className={s.sectionCount}>({inProgress.length})</span>
          </div>
          {inProgress.map((t) => <TaskCard key={t.id} task={t} />)}
        </div>
      )}

      {pending.length > 0 && (
        <div>
          <div className={s.sectionHeader}>
            <CircleRegular fontSize={16} />
            Pending
            <span className={s.sectionCount}>({pending.length})</span>
          </div>
          {pending.map((t) => <TaskCard key={t.id} task={t} />)}
        </div>
      )}

      {completed.length > 0 && (
        <div>
          <div
            className={s.sectionHeader}
            onClick={() => setCompletedOpen(!completedOpen)}
          >
            {completedOpen ? <ChevronDownRegular fontSize={16} /> : <ChevronRightRegular fontSize={16} />}
            Completed
            <span className={s.sectionCount}>({completed.length})</span>
          </div>
          {completedOpen && completed.map((t) => <TaskCard key={t.id} task={t} />)}
        </div>
      )}
    </div>
  );
}
