import type React from "react";
import {
  ClockRegular,
  ArrowSyncRegular,
  CheckmarkCircleRegular,
  FilterRegular,
  CheckmarkRegular,
  DismissRegular,
  EditRegular,
  MergeRegular,
} from "@fluentui/react-icons";
import type { TaskSnapshot, TaskStatus, TaskSize, TaskCommentType } from "../api";
import type { BadgeColor } from "../V3Badge";

// ── Filter definitions ──────────────────────────────────────────────────

export type TaskFilter = "all" | "review" | "active" | "completed";

export const FILTER_ITEMS: { value: TaskFilter; label: string; icon: React.ReactNode }[] = [
  { value: "all", label: "All", icon: <FilterRegular fontSize={14} /> },
  { value: "review", label: "Review Queue", icon: <ClockRegular fontSize={14} /> },
  { value: "active", label: "Active", icon: <ArrowSyncRegular fontSize={14} /> },
  { value: "completed", label: "Completed", icon: <CheckmarkCircleRegular fontSize={14} /> },
];

const REVIEW_STATUSES: TaskStatus[] = ["InReview", "AwaitingValidation", "Approved", "ChangesRequested"];
const ACTIVE_STATUSES: TaskStatus[] = ["Active", "Merging", "Blocked", "Queued"];
const COMPLETED_STATUSES: TaskStatus[] = ["Completed", "Cancelled"];

export function filterTasks(tasks: TaskSnapshot[], filter: TaskFilter): TaskSnapshot[] {
  switch (filter) {
    case "review":
      return tasks.filter((t) => REVIEW_STATUSES.includes(t.status));
    case "active":
      return tasks.filter((t) => ACTIVE_STATUSES.includes(t.status));
    case "completed":
      return tasks.filter((t) => COMPLETED_STATUSES.includes(t.status));
    default:
      return tasks;
  }
}

export function filterCount(tasks: TaskSnapshot[], filter: TaskFilter): number {
  return filterTasks(tasks, filter).length;
}

// ── Badge color helpers ─────────────────────────────────────────────────

export function statusBadgeColor(status: TaskStatus): BadgeColor {
  switch (status) {
    case "Active": case "Merging":       return "active";
    case "InReview":                     return "review";
    case "AwaitingValidation":           return "warn";
    case "Approved":                     return "ok";
    case "ChangesRequested":             return "warn";
    case "Blocked":                      return "err";
    case "Completed":                    return "done";
    case "Cancelled":                    return "cancel";
    case "Queued": default:              return "info";
  }
}

export function typeBadgeColor(type: string): BadgeColor {
  switch (type) {
    case "Bug":     return "bug";
    case "Feature": return "feat";
    default:        return "muted";
  }
}

export function sizeBadgeColor(size: TaskSize): BadgeColor {
  switch (size) {
    case "XS": case "S": return "muted";
    case "M":            return "info";
    case "L": case "XL": return "warn";
  }
}

export function commentTypeBadge(type: TaskCommentType): BadgeColor {
  switch (type) {
    case "Finding":  return "warn";
    case "Blocker":  return "err";
    case "Evidence": return "ok";
    case "Retrospective": return "info";
    case "Comment": default: return "muted";
  }
}

export function specLinkBadge(type: string): BadgeColor {
  switch (type) {
    case "Implements": return "ok";
    case "Modifies":   return "warn";
    case "Fixes":      return "err";
    case "References": return "info";
    default:           return "muted";
  }
}

export function evidencePhaseBadge(phase: string): BadgeColor {
  switch (phase) {
    case "Baseline": return "info";
    case "After":    return "ok";
    case "Review":   return "review";
    default:         return "muted";
  }
}

export function formatTime(iso: string): string {
  const d = new Date(iso);
  const now = new Date();
  const diffMs = now.getTime() - d.getTime();
  if (diffMs < 60_000) return "just now";
  if (diffMs < 3_600_000) return `${Math.floor(diffMs / 60_000)}m ago`;
  if (diffMs < 86_400_000) return `${Math.floor(diffMs / 3_600_000)}h ago`;
  return d.toLocaleDateString();
}

// ── Action definitions ──────────────────────────────────────────────────

export type TaskAction = "approve" | "requestChanges" | "reject" | "merge";

export function getAvailableActions(status: TaskStatus): TaskAction[] {
  switch (status) {
    case "InReview":
    case "AwaitingValidation":
      return ["approve", "requestChanges"];
    case "Approved":
      return ["merge", "reject"];
    case "Completed":
      return ["reject"];
    default:
      return [];
  }
}

export const ACTION_META: Record<TaskAction, { label: string; icon: React.ReactElement; appearance: "primary" | "subtle" | "outline"; command: string; needsReason: boolean }> = {
  approve:        { label: "Approve",         icon: <CheckmarkRegular />, appearance: "primary", command: "APPROVE_TASK",     needsReason: false },
  requestChanges: { label: "Request Changes", icon: <EditRegular />,     appearance: "outline", command: "REQUEST_CHANGES",  needsReason: true },
  reject:         { label: "Reject",          icon: <DismissRegular />,  appearance: "subtle",  command: "REJECT_TASK",      needsReason: true },
  merge:          { label: "Merge",           icon: <MergeRegular />,    appearance: "primary", command: "MERGE_TASK",        needsReason: false },
};

// ── Detail cache ────────────────────────────────────────────────────────

import type { SpecTaskLink, EvidenceRow, GateCheckResult, TaskComment } from "../api";

export interface DetailCacheEntry {
  updatedAt: string;
  specLinks?: SpecTaskLink[];
  evidence?: EvidenceRow[];
  gate?: GateCheckResult;
  comments?: TaskComment[];
}

const CACHE_MAX_SIZE = 50;
const detailCache = new Map<string, DetailCacheEntry>();

export function getCached(taskId: string, updatedAt: string): DetailCacheEntry {
  const c = detailCache.get(taskId);
  if (c && c.updatedAt === updatedAt) {
    detailCache.delete(taskId);
    detailCache.set(taskId, c);
    return c;
  }
  if (detailCache.size >= CACHE_MAX_SIZE) {
    const oldest = detailCache.keys().next().value;
    if (oldest !== undefined) detailCache.delete(oldest);
  }
  const fresh: DetailCacheEntry = { updatedAt };
  detailCache.set(taskId, fresh);
  return fresh;
}
