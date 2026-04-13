import { useState, useEffect, useCallback, useRef } from "react";
import type {
  TaskSnapshot,
  TaskComment,
  CommandExecutionResponse,
  SpecTaskLink,
  EvidenceRow,
  GateCheckResult,
} from "../api";
import { executeCommand, getTaskComments, getTaskSpecLinks, assignTask } from "../api";
import type { AgentDefinition } from "../api";
import {
  type TaskAction,
  ACTION_META,
  getAvailableActions,
  getCached,
} from "./taskListHelpers";

export interface UseTaskDetailResult {
  comments: TaskComment[];
  commentsLoading: boolean;
  commentsError: boolean;
  fetchComments: () => void;

  specLinks: SpecTaskLink[];
  specLinksLoading: boolean;

  evidence: EvidenceRow[];
  evidenceLoading: boolean;
  evidenceLoaded: boolean;
  fetchEvidence: () => void;

  gate: GateCheckResult | null;
  gateLoading: boolean;
  checkGates: () => void;

  actions: TaskAction[];
  actionPending: TaskAction | null;
  actionResult: { ok: boolean; message: string } | null;
  reasonAction: TaskAction | null;
  reasonText: string;
  setReasonText: (text: string) => void;
  handleAction: (action: TaskAction) => void;
  cancelReason: () => void;

  showAssignPicker: boolean;
  setShowAssignPicker: (show: boolean) => void;
  assignPending: boolean;
  handleAssign: (agent: AgentDefinition) => void;

  canCheckGates: boolean;
  canAssign: boolean;
}

export function useTaskDetail(task: TaskSnapshot, onRefresh: () => void): UseTaskDetailResult {
  const cached = getCached(task.id, task.updatedAt);

  const [comments, setComments] = useState<TaskComment[]>(cached.comments ?? []);
  const [commentsLoading, setCommentsLoading] = useState(!cached.comments);
  const [commentsError, setCommentsError] = useState(false);

  const [specLinks, setSpecLinks] = useState<SpecTaskLink[]>(cached.specLinks ?? []);
  const [specLinksLoading, setSpecLinksLoading] = useState(!cached.specLinks);

  const [evidence, setEvidence] = useState<EvidenceRow[]>(cached.evidence ?? []);
  const [evidenceLoading, setEvidenceLoading] = useState(false);
  const [evidenceLoaded, setEvidenceLoaded] = useState(!!cached.evidence);

  const [gate, setGate] = useState<GateCheckResult | null>(cached.gate ?? null);
  const [gateLoading, setGateLoading] = useState(false);

  const [actionPending, setActionPending] = useState<TaskAction | null>(null);
  const [actionResult, setActionResult] = useState<{ ok: boolean; message: string } | null>(null);
  const [reasonAction, setReasonAction] = useState<TaskAction | null>(null);
  const [reasonText, setReasonText] = useState("");
  const [showAssignPicker, setShowAssignPicker] = useState(false);
  const [assignPending, setAssignPending] = useState(false);
  const mountedRef = useRef(true);
  const fetchVersionRef = useRef(0);

  useEffect(() => {
    mountedRef.current = true;
    return () => { mountedRef.current = false; };
  }, []);

  useEffect(() => {
    fetchVersionRef.current += 1;
  }, [task.id, task.updatedAt]);

  const fetchComments = useCallback(() => {
    const version = fetchVersionRef.current;
    setCommentsLoading(true);
    setCommentsError(false);
    getTaskComments(task.id)
      .then((c) => {
        if (!mountedRef.current || fetchVersionRef.current !== version) return;
        setComments(c);
        const cache = getCached(task.id, task.updatedAt);
        cache.comments = c;
      })
      .catch(() => { if (mountedRef.current && fetchVersionRef.current === version) setCommentsError(true); })
      .finally(() => { if (mountedRef.current && fetchVersionRef.current === version) setCommentsLoading(false); });
  }, [task.id, task.updatedAt]);

  const fetchSpecLinks = useCallback(() => {
    const version = fetchVersionRef.current;
    setSpecLinksLoading(true);
    getTaskSpecLinks(task.id)
      .then((links) => {
        if (!mountedRef.current || fetchVersionRef.current !== version) return;
        setSpecLinks(links);
        const cache = getCached(task.id, task.updatedAt);
        cache.specLinks = links;
      })
      .catch(() => { if (mountedRef.current && fetchVersionRef.current === version) setSpecLinks([]); })
      .finally(() => { if (mountedRef.current && fetchVersionRef.current === version) setSpecLinksLoading(false); });
  }, [task.id, task.updatedAt]);

  useEffect(() => {
    if (!cached.comments) fetchComments();
    if (!cached.specLinks) fetchSpecLinks();
  }, [task.id, task.updatedAt]);

  const fetchEvidence = useCallback(() => {
    const version = fetchVersionRef.current;
    setEvidenceLoading(true);
    executeCommand({ command: "QUERY_EVIDENCE", args: { taskId: task.id } })
      .then((resp) => {
        if (!mountedRef.current || fetchVersionRef.current !== version) return;
        if (resp.status === "completed" && resp.result) {
          const result = resp.result as Record<string, unknown>;
          const items = (Array.isArray(result.evidence) ? result.evidence : []) as EvidenceRow[];
          setEvidence(items);
          const cache = getCached(task.id, task.updatedAt);
          cache.evidence = items;
        }
        setEvidenceLoaded(true);
      })
      .catch(() => { if (mountedRef.current && fetchVersionRef.current === version) setEvidenceLoaded(true); })
      .finally(() => { if (mountedRef.current && fetchVersionRef.current === version) setEvidenceLoading(false); });
  }, [task.id, task.updatedAt]);

  const checkGates = useCallback(() => {
    const version = fetchVersionRef.current;
    setGateLoading(true);
    executeCommand({ command: "CHECK_GATES", args: { taskId: task.id } })
      .then((resp) => {
        if (!mountedRef.current || fetchVersionRef.current !== version) return;
        if (resp.status === "completed" && resp.result) {
          const result = resp.result as Record<string, unknown>;
          const g: GateCheckResult = {
            taskId: (result.taskId as string) ?? task.id,
            currentPhase: (result.currentPhase as string) ?? "",
            targetPhase: (result.targetPhase as string) ?? "",
            met: (result.met as boolean) ?? false,
            requiredChecks: (result.requiredChecks as number) ?? 0,
            passedChecks: (result.passedChecks as number) ?? 0,
            missingChecks: (Array.isArray(result.missingChecks) ? result.missingChecks : []) as string[],
            evidence: (Array.isArray(result.evidence) ? result.evidence : []) as GateCheckResult["evidence"],
            message: (result.message as string) ?? "",
          };
          setGate(g);
          const cache = getCached(task.id, task.updatedAt);
          cache.gate = g;
        }
      })
      .catch(() => {})
      .finally(() => { if (mountedRef.current && fetchVersionRef.current === version) setGateLoading(false); });
  }, [task.id, task.updatedAt]);

  const actions = getAvailableActions(task.status);

  const handleAction = useCallback(async (action: TaskAction) => {
    const meta = ACTION_META[action];

    if (meta.needsReason && (reasonAction !== action || !reasonText.trim())) {
      setReasonAction(action);
      if (reasonAction !== action) setReasonText("");
      return;
    }

    const args: Record<string, string> = { taskId: task.id };
    if (meta.needsReason && reasonText.trim()) {
      if (action === "requestChanges") args.findings = reasonText.trim();
      else args.reason = reasonText.trim();
    }

    setActionPending(action);
    setActionResult(null);
    try {
      const resp: CommandExecutionResponse = await executeCommand({ command: meta.command, args });
      if (!mountedRef.current) return;
      if (resp.status === "completed") {
        setActionResult({ ok: true, message: `${meta.label} successful` });
        setReasonAction(null);
        setReasonText("");
        onRefresh();
      } else if (resp.status === "denied") {
        setActionResult({ ok: false, message: resp.error ?? "Permission denied" });
      } else {
        setActionResult({ ok: false, message: resp.error ?? "Command failed" });
      }
    } catch (err) {
      if (!mountedRef.current) return;
      setActionResult({ ok: false, message: err instanceof Error ? err.message : "Request failed" });
    } finally {
      if (mountedRef.current) setActionPending(null);
    }
  }, [task.id, reasonAction, reasonText, onRefresh]);

  const cancelReason = useCallback(() => {
    setReasonAction(null);
    setReasonText("");
  }, []);

  const handleAssign = useCallback(async (agent: AgentDefinition) => {
    setAssignPending(true);
    setActionResult(null);
    try {
      await assignTask(task.id, agent.id, agent.name);
      if (!mountedRef.current) return;
      setActionResult({ ok: true, message: `Assigned to ${agent.name}` });
      setShowAssignPicker(false);
      onRefresh();
    } catch (err) {
      if (!mountedRef.current) return;
      setActionResult({ ok: false, message: err instanceof Error ? err.message : "Assignment failed" });
    } finally {
      if (mountedRef.current) setAssignPending(false);
    }
  }, [task.id, onRefresh]);

  const canCheckGates = ["Active", "AwaitingValidation", "InReview"].includes(task.status);
  const canAssign = task.status === "Queued" && !task.assignedAgentId;

  return {
    comments, commentsLoading, commentsError, fetchComments,
    specLinks, specLinksLoading,
    evidence, evidenceLoading, evidenceLoaded, fetchEvidence,
    gate, gateLoading, checkGates,
    actions, actionPending, actionResult,
    reasonAction, reasonText, setReasonText, handleAction, cancelReason,
    showAssignPicker, setShowAssignPicker, assignPending, handleAssign,
    canCheckGates, canAssign,
  };
}
