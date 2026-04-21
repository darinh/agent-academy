// @vitest-environment jsdom
/**
 * DOM tests for TaskDetail sub-sections: SpecLinksSection, DependenciesSection,
 * CommentsSection, and StatusBadge.
 *
 * Covers: loading/empty/data states, interactions, error handling.
 */
import "@testing-library/jest-dom/vitest";
import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { createElement } from "react";
import { FluentProvider, webDarkTheme } from "@fluentui/react-components";

vi.mock("../V3Badge", () => ({
  default: ({
    children,
    color,
  }: {
    children: React.ReactNode;
    color: string;
  }) => createElement("span", { "data-testid": `badge-${color}` }, children),
}));

import SpecLinksSection from "../taskList/SpecLinksSection";
import DependenciesSection from "../taskList/DependenciesSection";
import CommentsSection from "../taskList/CommentsSection";
import { StatusBadge } from "../notificationWizard/StatusBadge";
import type { SpecTaskLink, TaskDependencySummary, TaskComment } from "../api";

afterEach(cleanup);

function wrap(el: React.ReactElement) {
  return render(createElement(FluentProvider, { theme: webDarkTheme }, el));
}

// ── SpecLinksSection ───────────────────────────────────────────────

describe("SpecLinksSection", () => {
  it("shows loading spinner", () => {
    wrap(createElement(SpecLinksSection, { specLinks: [], loading: true }));
    expect(screen.getByText(/Loading spec links/)).toBeInTheDocument();
  });

  it("shows empty state when no links", () => {
    wrap(createElement(SpecLinksSection, { specLinks: [], loading: false }));
    expect(screen.getByText(/No spec links/)).toBeInTheDocument();
  });

  it("renders spec links with link type and section", () => {
    const links: SpecTaskLink[] = [
      {
        id: "sl-1",
        taskId: "task-1",
        specSectionId: "3.2.1",
        linkType: "Implements",
        linkedByAgentId: "agent-1",
        linkedByAgentName: "Athena",
        note: "Core auth flow",
        createdAt: "2026-04-15T10:00:00Z",
      },
    ];
    wrap(createElement(SpecLinksSection, { specLinks: links, loading: false }));
    expect(screen.getByText("Implements")).toBeInTheDocument();
    expect(screen.getByText("3.2.1")).toBeInTheDocument();
    expect(screen.getByText(/by Athena/)).toBeInTheDocument();
    expect(screen.getByText("Core auth flow")).toBeInTheDocument();
  });

  it("shows count in header", () => {
    const links: SpecTaskLink[] = [
      {
        id: "sl-1", taskId: "t-1", specSectionId: "1.0", linkType: "Fixes",
        linkedByAgentId: "a-1", linkedByAgentName: "Hermes", createdAt: "2026-01-01T00:00:00Z",
      },
      {
        id: "sl-2", taskId: "t-1", specSectionId: "2.0", linkType: "References",
        linkedByAgentId: "a-2", linkedByAgentName: "Apollo", createdAt: "2026-01-01T00:00:00Z",
      },
    ];
    wrap(createElement(SpecLinksSection, { specLinks: links, loading: false }));
    expect(screen.getByText(/\(2\)/)).toBeInTheDocument();
  });
});

// ── DependenciesSection ────────────────────────────────────────────

describe("DependenciesSection", () => {
  it("shows loading spinner", () => {
    wrap(createElement(DependenciesSection, { dependsOn: [], dependedOnBy: [], loading: true }));
    expect(screen.getByText(/Loading dependencies/)).toBeInTheDocument();
  });

  it("shows empty state when no dependencies", () => {
    wrap(createElement(DependenciesSection, { dependsOn: [], dependedOnBy: [], loading: false }));
    expect(screen.getByText(/No dependencies/)).toBeInTheDocument();
  });

  it("renders upstream dependencies", () => {
    const deps: TaskDependencySummary[] = [
      { taskId: "task-abc-1234", title: "Set up database", status: "Completed", isSatisfied: true },
    ];
    wrap(createElement(DependenciesSection, { dependsOn: deps, dependedOnBy: [], loading: false }));
    expect(screen.getByText("Depends on")).toBeInTheDocument();
    expect(screen.getByText("Set up database")).toBeInTheDocument();
    expect(screen.getByText("Completed")).toBeInTheDocument();
  });

  it("renders downstream dependencies", () => {
    const deps: TaskDependencySummary[] = [
      { taskId: "task-def-5678", title: "Deploy frontend", status: "Queued", isSatisfied: false },
    ];
    wrap(createElement(DependenciesSection, { dependsOn: [], dependedOnBy: deps, loading: false }));
    expect(screen.getByText("Depended on by")).toBeInTheDocument();
    expect(screen.getByText("Deploy frontend")).toBeInTheDocument();
  });

  it("shows counts in header", () => {
    const up: TaskDependencySummary[] = [
      { taskId: "t-1", title: "A", status: "Completed", isSatisfied: true },
    ];
    const down: TaskDependencySummary[] = [
      { taskId: "t-2", title: "B", status: "Queued", isSatisfied: false },
      { taskId: "t-3", title: "C", status: "Active", isSatisfied: false },
    ];
    wrap(createElement(DependenciesSection, { dependsOn: up, dependedOnBy: down, loading: false }));
    expect(screen.getByText(/1 upstream · 2 downstream/)).toBeInTheDocument();
  });

  it("calls onSelectTask when clicking a dependency", async () => {
    const onSelect = vi.fn();
    const deps: TaskDependencySummary[] = [
      { taskId: "task-xyz", title: "My dep", status: "Completed", isSatisfied: true },
    ];
    wrap(createElement(DependenciesSection, {
      dependsOn: deps, dependedOnBy: [], loading: false, onSelectTask: onSelect,
    }));
    await userEvent.click(screen.getByText("My dep"));
    expect(onSelect).toHaveBeenCalledWith("task-xyz");
  });
});

// ── CommentsSection ────────────────────────────────────────────────

describe("CommentsSection", () => {
  it("shows loading spinner", () => {
    wrap(createElement(CommentsSection, {
      comments: [], commentCount: 0, loading: true, error: false, onRetry: vi.fn(),
    }));
    expect(screen.getByText(/Loading comments/)).toBeInTheDocument();
  });

  it("shows empty state when no comments", () => {
    wrap(createElement(CommentsSection, {
      comments: [], commentCount: 0, loading: false, error: false, onRetry: vi.fn(),
    }));
    expect(screen.getByText(/No comments yet/)).toBeInTheDocument();
  });

  it("shows error state with retry button", async () => {
    const onRetry = vi.fn();
    wrap(createElement(CommentsSection, {
      comments: [], commentCount: 0, loading: false, error: true, onRetry,
    }));
    expect(screen.getByText(/Failed to load comments/)).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: /Retry/i }));
    expect(onRetry).toHaveBeenCalledOnce();
  });

  it("renders comment cards", () => {
    const comments: TaskComment[] = [
      {
        id: "c-1", taskId: "t-1", agentId: "a-1", agentName: "Athena",
        commentType: "Finding", content: "Found a potential issue", createdAt: "2026-04-15T10:00:00Z",
      },
    ];
    wrap(createElement(CommentsSection, {
      comments, commentCount: 1, loading: false, error: false, onRetry: vi.fn(),
    }));
    expect(screen.getByText("Athena")).toBeInTheDocument();
    expect(screen.getByText("Finding")).toBeInTheDocument();
    expect(screen.getByText("Found a potential issue")).toBeInTheDocument();
  });

  it("shows comment count in header", () => {
    wrap(createElement(CommentsSection, {
      comments: [], commentCount: 5, loading: false, error: false, onRetry: vi.fn(),
    }));
    expect(screen.getByText(/\(5\)/)).toBeInTheDocument();
  });
});

// ── StatusBadge ────────────────────────────────────────────────────

describe("StatusBadge", () => {
  it("renders nothing for idle status", () => {
    const { container } = render(createElement(StatusBadge, { status: "idle" }));
    expect(container.firstChild).toBeNull();
  });

  it("renders loading state", () => {
    wrap(createElement(StatusBadge, { status: "loading" }));
    expect(screen.getByText("Working…")).toBeInTheDocument();
  });

  it("renders success state", () => {
    wrap(createElement(StatusBadge, { status: "success" }));
    expect(screen.getByText("Done")).toBeInTheDocument();
  });

  it("renders error state", () => {
    wrap(createElement(StatusBadge, { status: "error" }));
    expect(screen.getByText("Failed")).toBeInTheDocument();
  });

  it("sets data-status attribute", () => {
    const { container } = wrap(createElement(StatusBadge, { status: "success" }));
    expect(container.querySelector("[data-status='success']")).toBeInTheDocument();
  });
});
