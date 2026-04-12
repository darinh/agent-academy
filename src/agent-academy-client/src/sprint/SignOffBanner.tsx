import type { SprintDetailResponse } from "../api";

interface SignOffBannerProps {
  detail: SprintDetailResponse;
}

export default function SignOffBanner({ detail }: SignOffBannerProps) {
  if (!detail.sprint.awaitingSignOff) return null;

  const elapsed = detail.sprint.signOffRequestedAt
    ? Date.now() - new Date(detail.sprint.signOffRequestedAt).getTime()
    : null;
  let waitLabel = "";
  if (elapsed !== null) {
    const mins = Math.floor(elapsed / 60000);
    waitLabel = ` Waiting ${mins < 60 ? `${mins}m` : `${Math.floor(mins / 60)}h ${mins % 60}m`}.`;
  }

  return (
    <div style={{
      padding: "8px 16px",
      background: "rgba(255, 193, 7, 0.12)",
      borderBottom: "1px solid rgba(255, 193, 7, 0.3)",
      fontSize: "13px",
      display: "flex",
      alignItems: "center",
      gap: "8px",
    }}>
      <span>⏳</span>
      <span>
        <strong>User sign-off required</strong> — agents want to advance from{" "}
        <strong>{detail.sprint.currentStage}</strong> to{" "}
        <strong>{detail.sprint.pendingStage}</strong>.
        Review the {detail.sprint.currentStage} artifacts and approve or reject.
        {waitLabel}
      </span>
    </div>
  );
}
