import { Button, Spinner } from "@fluentui/react-components";
import { CommentRegular, ErrorCircleRegular } from "@fluentui/react-icons";
import type { TaskComment } from "../api";
import V3Badge from "../V3Badge";
import { commentTypeBadge, formatTime } from "./taskListHelpers";
import { useTaskDetailStyles } from "./taskDetailStyles";

interface CommentsSectionProps {
  comments: TaskComment[];
  commentCount: number | null | undefined;
  loading: boolean;
  error: boolean;
  onRetry: () => void;
}

export default function CommentsSection({ comments, commentCount, loading, error, onRetry }: CommentsSectionProps) {
  const s = useTaskDetailStyles();
  return (
    <div className={s.commentsSection}>
      <div className={s.sectionLabel}>
        <CommentRegular fontSize={13} style={{ marginRight: 4 }} />
        Comments {commentCount != null && commentCount > 0 ? `(${commentCount})` : ""}
      </div>
      {loading && <Spinner size="tiny" label="Loading comments…" />}
      {!loading && error && (
        <div style={{ fontSize: "12px", color: "var(--error)", marginTop: "4px", display: "flex", alignItems: "center", gap: "6px" }}>
          <ErrorCircleRegular fontSize={13} />
          Failed to load comments
          <Button size="small" appearance="subtle" onClick={onRetry}>Retry</Button>
        </div>
      )}
      {!loading && !error && comments.length === 0 && (
        <div style={{ fontSize: "12px", color: "var(--aa-muted)", marginTop: "4px" }}>No comments yet</div>
      )}
      {comments.map((c) => (
        <div key={c.id} className={s.commentCard}>
          <div className={s.commentHeader}>
            <span className={s.commentAuthor}>{c.agentName}</span>
            <V3Badge color={commentTypeBadge(c.commentType)}>
              {c.commentType}
            </V3Badge>
            <span className={s.commentTime}>{formatTime(c.createdAt)}</span>
          </div>
          <div className={s.commentContent}>{c.content}</div>
        </div>
      ))}
    </div>
  );
}
