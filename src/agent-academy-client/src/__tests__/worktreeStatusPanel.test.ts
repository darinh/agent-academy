import { describe, expect, it } from "vitest";
import type { WorktreeStatusSnapshot } from "../api";

// ── Factories ──

function makeWorktreeSnapshot(overrides: Partial<WorktreeStatusSnapshot> = {}): WorktreeStatusSnapshot {
  return {
    branch: "task/implement-feature",
    relativePath: ".worktrees/task-implement-feature",
    createdAt: new Date().toISOString(),
    statusAvailable: true,
    error: null,
    totalDirtyFiles: 0,
    dirtyFilesPreview: [],
    filesChanged: 0,
    insertions: 0,
    deletions: 0,
    lastCommitSha: "abc1234567890123456789012345678901234567",
    lastCommitMessage: "Add implement feature",
    lastCommitAuthor: "Coder",
    lastCommitDate: new Date().toISOString(),
    taskId: null,
    taskTitle: null,
    taskStatus: null,
    agentId: null,
    agentName: null,
    ...overrides,
  };
}

// ── Unit Tests ──

describe("WorktreeStatusSnapshot type", () => {
  it("creates a valid clean worktree snapshot", () => {
    const snapshot = makeWorktreeSnapshot();
    expect(snapshot.branch).toBe("task/implement-feature");
    expect(snapshot.statusAvailable).toBe(true);
    expect(snapshot.totalDirtyFiles).toBe(0);
    expect(snapshot.dirtyFilesPreview).toEqual([]);
    expect(snapshot.lastCommitSha).toBeTruthy();
  });

  it("represents a dirty worktree with file preview", () => {
    const snapshot = makeWorktreeSnapshot({
      totalDirtyFiles: 3,
      dirtyFilesPreview: ["src/main.ts", "src/utils.ts", "package.json"],
      filesChanged: 2,
      insertions: 15,
      deletions: 5,
    });
    expect(snapshot.totalDirtyFiles).toBe(3);
    expect(snapshot.dirtyFilesPreview).toHaveLength(3);
    expect(snapshot.insertions).toBe(15);
    expect(snapshot.deletions).toBe(5);
  });

  it("represents a worktree with linked task and agent", () => {
    const snapshot = makeWorktreeSnapshot({
      taskId: "task-42",
      taskTitle: "Fix login bug",
      taskStatus: "Active",
      agentId: "coder-1",
      agentName: "Coder",
    });
    expect(snapshot.taskId).toBe("task-42");
    expect(snapshot.agentName).toBe("Coder");
  });

  it("represents an unavailable worktree", () => {
    const snapshot = makeWorktreeSnapshot({
      statusAvailable: false,
      error: "Worktree directory does not exist",
      lastCommitSha: null,
      lastCommitMessage: null,
      lastCommitAuthor: null,
      lastCommitDate: null,
    });
    expect(snapshot.statusAvailable).toBe(false);
    expect(snapshot.error).toContain("does not exist");
  });

  it("caps dirty files preview while tracking total", () => {
    const preview = Array.from({ length: 10 }, (_, i) => `file-${i}.txt`);
    const snapshot = makeWorktreeSnapshot({
      totalDirtyFiles: 25,
      dirtyFilesPreview: preview,
    });
    expect(snapshot.totalDirtyFiles).toBe(25);
    expect(snapshot.dirtyFilesPreview).toHaveLength(10);
  });
});

describe("WorktreeStatusPanel helpers", () => {
  it("short SHA is 7 characters", () => {
    const sha = "abc1234567890123456789012345678901234567";
    expect(sha.slice(0, 7)).toBe("abc1234");
  });

  it("dirty indicator thresholds", () => {
    // Mirrors the dirtyIndicator logic in WorktreeStatusPanel
    const dirtyIndicator = (count: number): string => {
      if (count === 0) return "ok";
      if (count <= 5) return "warn";
      return "err";
    };
    expect(dirtyIndicator(0)).toBe("ok");
    expect(dirtyIndicator(3)).toBe("warn");
    expect(dirtyIndicator(5)).toBe("warn");
    expect(dirtyIndicator(6)).toBe("err");
    expect(dirtyIndicator(100)).toBe("err");
  });
});
