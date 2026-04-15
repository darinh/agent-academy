// @vitest-environment jsdom
/**
 * DOM tests for WorktreeStatusPanel.
 *
 * Covers: loading state, error state, empty state, worktree cards with branch info,
 * agent names, task info, dirty files, diff stats, error per-worktree.
 */
import "@testing-library/jest-dom/vitest";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createElement } from "react";
import { FluentProvider, webDarkTheme } from "@fluentui/react-components";

// ── Mocks ──────────────────────────────────────────────────────────────

const mockGetWorktreeStatus = vi.fn();

vi.mock("../api", () => ({
  getWorktreeStatus: (...args: unknown[]) => mockGetWorktreeStatus(...args),
}));

vi.mock("../V3Badge", () => ({
  default: ({
    children,
    color,
  }: {
    children: React.ReactNode;
    color: string;
  }) => createElement("span", { "data-testid": `badge-${color}` }, children),
}));

vi.mock("../EmptyState", () => ({
  default: ({ title, detail }: { icon: string; title: string; detail: string }) =>
    createElement("div", { "data-testid": "empty-state" }, `${title} — ${detail}`),
}));

import WorktreeStatusPanel from "../WorktreeStatusPanel";
import type { WorktreeStatusSnapshot } from "../api";

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
  vi.restoreAllMocks();
});

function makeWorktree(overrides: Partial<WorktreeStatusSnapshot> = {}): WorktreeStatusSnapshot {
  return {
    branch: "feat/add-auth",
    relativePath: "worktrees/feat-add-auth",
    createdAt: "2026-04-15T08:00:00Z",
    statusAvailable: true,
    error: null,
    totalDirtyFiles: 2,
    dirtyFilesPreview: ["src/Auth.cs", "src/Program.cs"],
    filesChanged: 3,
    insertions: 42,
    deletions: 8,
    lastCommitSha: "abc1234567890",
    lastCommitMessage: "feat: add JWT auth",
    lastCommitAuthor: "Hephaestus",
    lastCommitDate: "2026-04-15T09:00:00Z",
    taskId: "task-1",
    taskTitle: "Implement auth",
    taskStatus: "InProgress",
    agentId: "agent-1",
    agentName: "Hephaestus",
    ...overrides,
  };
}

function renderPanel() {
  return render(
    createElement(
      FluentProvider,
      { theme: webDarkTheme },
      createElement(WorktreeStatusPanel, {}),
    ),
  );
}

describe("WorktreeStatusPanel", () => {
  beforeEach(() => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it("shows loading spinner initially", () => {
    mockGetWorktreeStatus.mockReturnValue(new Promise(() => {})); // never resolves
    renderPanel();
    expect(screen.getByText(/Loading worktrees/i)).toBeInTheDocument();
  });

  it("shows error state when fetch fails", async () => {
    mockGetWorktreeStatus.mockRejectedValue(new Error("Network error"));
    renderPanel();
    await waitFor(() => {
      expect(screen.getByText(/Network error/)).toBeInTheDocument();
    });
  });

  it("shows empty state when no worktrees", async () => {
    mockGetWorktreeStatus.mockResolvedValue([]);
    renderPanel();
    await waitFor(() => {
      expect(screen.getByTestId("empty-state")).toBeInTheDocument();
    });
  });

  it("renders worktree card with branch name", async () => {
    mockGetWorktreeStatus.mockResolvedValue([makeWorktree()]);
    renderPanel();
    await waitFor(() => {
      expect(screen.getByText("feat/add-auth")).toBeInTheDocument();
    });
  });

  it("shows agent name", async () => {
    mockGetWorktreeStatus.mockResolvedValue([makeWorktree()]);
    renderPanel();
    await waitFor(() => {
      expect(screen.getByText("Hephaestus")).toBeInTheDocument();
    });
  });

  it("shows dirty file count badge", async () => {
    mockGetWorktreeStatus.mockResolvedValue([makeWorktree({ totalDirtyFiles: 3 })]);
    renderPanel();
    await waitFor(() => {
      expect(screen.getByText("3 dirty")).toBeInTheDocument();
    });
  });

  it("shows clean badge when no dirty files", async () => {
    mockGetWorktreeStatus.mockResolvedValue([
      makeWorktree({ totalDirtyFiles: 0, dirtyFilesPreview: [] }),
    ]);
    renderPanel();
    await waitFor(() => {
      expect(screen.getByText("clean")).toBeInTheDocument();
    });
  });

  it("shows diff stats (files, insertions, deletions)", async () => {
    mockGetWorktreeStatus.mockResolvedValue([
      makeWorktree({ filesChanged: 5, insertions: 120, deletions: 30 }),
    ]);
    renderPanel();
    await waitFor(() => {
      expect(screen.getByText("5f")).toBeInTheDocument();
      expect(screen.getByText("+120")).toBeInTheDocument();
      expect(screen.getByText("−30")).toBeInTheDocument();
    });
  });

  it("shows dirty files preview", async () => {
    mockGetWorktreeStatus.mockResolvedValue([makeWorktree()]);
    renderPanel();
    await waitFor(() => {
      expect(screen.getByText("src/Auth.cs")).toBeInTheDocument();
      expect(screen.getByText("src/Program.cs")).toBeInTheDocument();
    });
  });

  it("shows overflow indicator for many dirty files", async () => {
    mockGetWorktreeStatus.mockResolvedValue([
      makeWorktree({
        totalDirtyFiles: 10,
        dirtyFilesPreview: ["file1.cs", "file2.cs"],
      }),
    ]);
    renderPanel();
    await waitFor(() => {
      expect(screen.getByText(/and 8 more/)).toBeInTheDocument();
    });
  });

  it("shows commit info", async () => {
    mockGetWorktreeStatus.mockResolvedValue([makeWorktree()]);
    renderPanel();
    await waitFor(() => {
      expect(screen.getByText(/abc1234/)).toBeInTheDocument();
      expect(screen.getByText(/feat: add JWT auth/)).toBeInTheDocument();
    });
  });

  it("shows task status badge", async () => {
    mockGetWorktreeStatus.mockResolvedValue([makeWorktree({ taskStatus: "InProgress" })]);
    renderPanel();
    await waitFor(() => {
      expect(screen.getByText("InProgress")).toBeInTheDocument();
    });
  });

  it("shows per-worktree error when status unavailable", async () => {
    mockGetWorktreeStatus.mockResolvedValue([
      makeWorktree({
        statusAvailable: false,
        error: "Worktree path missing",
        filesChanged: 0,
        insertions: 0,
        deletions: 0,
      }),
    ]);
    renderPanel();
    await waitFor(() => {
      expect(screen.getByText("Worktree path missing")).toBeInTheDocument();
    });
  });

  it("renders multiple worktree cards", async () => {
    mockGetWorktreeStatus.mockResolvedValue([
      makeWorktree({ branch: "feat/auth" }),
      makeWorktree({ branch: "fix/bug-123" }),
    ]);
    renderPanel();
    await waitFor(() => {
      expect(screen.getByText("feat/auth")).toBeInTheDocument();
      expect(screen.getByText("fix/bug-123")).toBeInTheDocument();
    });
  });
});
