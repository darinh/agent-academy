import type { TaskSnapshot, AgentDefinition } from "../api";
import { useTaskDetailStyles } from "./taskDetailStyles";
import { useTaskDetail } from "./useTaskDetail";
import SpecLinksSection from "./SpecLinksSection";
import DependenciesSection from "./DependenciesSection";
import EvidenceLedger from "./EvidenceLedger";
import GateStatus from "./GateStatus";
import CommentsSection from "./CommentsSection";
import TaskActionsBar from "./TaskActionsBar";

interface TaskDetailProps {
  task: TaskSnapshot;
  agents: AgentDefinition[];
  onRefresh: () => void;
}

export default function TaskDetail({ task, agents, onRefresh }: TaskDetailProps) {
  const s = useTaskDetailStyles();
  const detail = useTaskDetail(task, onRefresh);

  return (
    <div className={s.expandedSection}>
      {task.description && (
        <>
          <div className={s.sectionLabel}>Description</div>
          <div className={s.descriptionText}>{task.description}</div>
        </>
      )}

      {task.successCriteria && (
        <>
          <div className={s.sectionLabel}>Success Criteria</div>
          <div className={s.descriptionText}>{task.successCriteria}</div>
        </>
      )}

      {(task.reviewRounds != null && task.reviewRounds > 0) && (
        <div className={s.reviewMeta}>
          <span>Review round {task.reviewRounds}</span>
          {task.reviewerAgentId && <span>Reviewer: {task.reviewerAgentId}</span>}
          {task.mergeCommitSha && <span>Merge: {task.mergeCommitSha.slice(0, 8)}</span>}
        </div>
      )}

      {task.implementationSummary && (
        <>
          <div className={s.sectionLabel}>Implementation</div>
          <div className={s.descriptionText}>{task.implementationSummary}</div>
        </>
      )}
      {task.validationSummary && (
        <>
          <div className={s.sectionLabel}>Validation</div>
          <div className={s.descriptionText}>{task.validationSummary}</div>
        </>
      )}

      {task.testsCreated && task.testsCreated.length > 0 && (
        <>
          <div className={s.sectionLabel}>Tests Created</div>
          <div className={s.descriptionText}>{task.testsCreated.join("\n")}</div>
        </>
      )}

      <SpecLinksSection specLinks={detail.specLinks} loading={detail.specLinksLoading} />

      <DependenciesSection
        dependsOn={detail.dependsOn}
        dependedOnBy={detail.dependedOnBy}
        loading={detail.depsLoading}
      />

      <EvidenceLedger
        evidence={detail.evidence}
        loading={detail.evidenceLoading}
        loaded={detail.evidenceLoaded}
        onLoad={detail.fetchEvidence}
      />

      {detail.canCheckGates && (
        <GateStatus gate={detail.gate} loading={detail.gateLoading} onCheck={detail.checkGates} />
      )}

      <CommentsSection
        comments={detail.comments}
        commentCount={task.commentCount}
        loading={detail.commentsLoading}
        error={detail.commentsError}
        onRetry={detail.fetchComments}
      />

      <TaskActionsBar
        actions={detail.actions}
        actionPending={detail.actionPending}
        actionResult={detail.actionResult}
        reasonAction={detail.reasonAction}
        reasonText={detail.reasonText}
        onReasonTextChange={detail.setReasonText}
        onAction={detail.handleAction}
        onCancelReason={detail.cancelReason}
        canAssign={detail.canAssign}
        agents={agents}
        showAssignPicker={detail.showAssignPicker}
        onToggleAssignPicker={() => detail.setShowAssignPicker(!detail.showAssignPicker)}
        assignPending={detail.assignPending}
        onAssign={detail.handleAssign}
      />
    </div>
  );
}
