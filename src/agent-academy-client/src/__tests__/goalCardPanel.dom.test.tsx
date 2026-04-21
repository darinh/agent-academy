// @vitest-environment jsdom
/**
 * Interactive RTL tests for GoalCardPanel.
 *
 * Uses @testing-library/react + jsdom.
 * Covers: loading state (SkeletonLoader), error state (ErrorState + retry),
 * empty state (no cards), stats row, status/verdict filter chips,
 * filter empty state with clear action, card rendering with badges,
 * card expand/collapse, task navigation link, refresh button,
 * refreshTrigger prop, room-scoped fetch, and long description truncation.
 */
import "@testing-library/jest-dom/vitest";
import {
  cleanup,
  render,
  screen,
  waitFor,
} from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createElement } from "react";
import { FluentProvider, webDarkTheme } from "@fluentui/react-components";

// ── Mocks ──────────────────────────────────────────────────────────────

const mockGetGoalCards = vi.fn();
const mockUpdateGoalCardStatus = vi.fn();

vi.mock("../api", () => ({
  getGoalCards: (...args: unknown[]) => mockGetGoalCards(...args),
  updateGoalCardStatus: (...args: unknown[]) => mockUpdateGoalCardStatus(...args),
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
  default: ({
    title,
    detail,
    action,
  }: {
    icon?: React.ReactNode;
    title: string;
    detail?: string;
    action?: { label: string; onClick: () => void };
  }) =>
    createElement(
      "div",
      { "data-testid": "empty-state" },
      createElement("span", null, title),
      detail && createElement("span", null, detail),
      action &&
        createElement("button", { onClick: action.onClick }, action.label),
    ),
}));

vi.mock("../ErrorState", () => ({
  default: ({
    message,
    onRetry,
  }: {
    message: string;
    onRetry?: () => void;
  }) =>
    createElement(
      "div",
      { "data-testid": "error-state" },
      createElement("span", null, message),
      onRetry && createElement("button", { onClick: onRetry }, "Retry"),
    ),
}));

vi.mock("../SkeletonLoader", () => ({
  default: ({ rows }: { rows: number }) =>
    createElement(
      "div",
      { "data-testid": "skeleton-loader" },
      `Loading ${rows} rows`,
    ),
}));

vi.mock("../goalCards", () => ({
  useGoalCardPanelStyles: () =>
    new Proxy(
      {},
      {
        get: (_t, prop) => `mock-${String(prop)}`,
      },
    ),
}));

vi.mock("../panelUtils", () => ({
  formatTimestamp: (iso: string) => iso.slice(0, 10),
}));

import GoalCardPanel from "../GoalCardPanel";
import type { GoalCard } from "../api";

// ── Factories ──────────────────────────────────────────────────────────

function makeCard(overrides: Partial<GoalCard> = {}): GoalCard {
  return {
    id: "gc-1",
    agentId: "architect",
    agentName: "Architect",
    roomId: "room-1",
    taskId: "task-1",
    taskDescription: "Implement authentication flow",
    intent: "Add JWT auth",
    divergence: "None identified",
    steelman: "Strong auth foundation",
    strawman: "Could over-engineer",
    verdict: "Proceed",
    freshEyes1: "First perspective",
    freshEyes2: "Second perspective",
    freshEyes3: "Third perspective",
    promptVersion: 1,
    status: "Active",
    createdAt: "2026-04-01T00:00:00Z",
    updatedAt: "2026-04-01T01:00:00Z",
    ...overrides,
  };
}

function makeCards(
  statuses: GoalCard["status"][] = [
    "Active",
    "Active",
    "Challenged",
    "Completed",
    "Abandoned",
  ],
): GoalCard[] {
  return statuses.map((status, i) =>
    makeCard({
      id: `gc-${i}`,
      agentName: `Agent-${i}`,
      status,
      verdict: i % 3 === 0 ? "Proceed" : i % 3 === 1 ? "ProceedWithCaveat" : "Challenge",
      taskDescription: `Task description for card ${i}`,
    }),
  );
}

// ── Helpers ─────────────────────────────────────────────────────────────

function renderPanel(
  props: {
    roomId?: string | null;
    refreshTrigger?: number;
    onNavigateToTask?: (taskId: string) => void;
  } = {},
) {
  return render(
    createElement(
      FluentProvider,
      { theme: webDarkTheme },
      createElement(GoalCardPanel, props),
    ),
  );
}

// ── Tests ───────────────────────────────────────────────────────────────

describe("GoalCardPanel (interactive)", () => {
  beforeEach(() => {
    vi.resetAllMocks();
  });

  afterEach(() => {
    cleanup();
  });

  // ── Loading state ──

  describe("loading state", () => {
    it("shows SkeletonLoader while fetching data", () => {
      mockGetGoalCards.mockReturnValue(new Promise(() => {}));
      renderPanel();
      expect(screen.getByTestId("skeleton-loader")).toBeInTheDocument();
      expect(screen.getByText("Loading 6 rows")).toBeInTheDocument();
    });
  });

  // ── Error state ──

  describe("error state", () => {
    it("shows ErrorState when API fails", async () => {
      mockGetGoalCards.mockRejectedValue(new Error("Network error"));
      renderPanel();
      await waitFor(() => {
        expect(screen.getByTestId("error-state")).toBeInTheDocument();
      });
      expect(screen.getByText("Network error")).toBeInTheDocument();
    });

    it("shows generic message for non-Error rejections", async () => {
      mockGetGoalCards.mockRejectedValue("oops");
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("Failed to load goal cards")).toBeInTheDocument();
      });
    });

    it("retries on ErrorState retry click", async () => {
      mockGetGoalCards.mockRejectedValueOnce(new Error("Fail"));
      mockGetGoalCards.mockResolvedValueOnce([makeCard()]);
      const user = userEvent.setup();
      renderPanel();
      await waitFor(() => {
        expect(screen.getByTestId("error-state")).toBeInTheDocument();
      });
      await user.click(screen.getByText("Retry"));
      await waitFor(() => {
        expect(screen.getByText("Architect")).toBeInTheDocument();
      });
      expect(mockGetGoalCards).toHaveBeenCalledTimes(2);
    });
  });

  // ── Empty state ──

  describe("empty state", () => {
    it("shows empty state when no cards returned", async () => {
      mockGetGoalCards.mockResolvedValue([]);
      renderPanel();
      await waitFor(() => {
        expect(screen.getByTestId("empty-state")).toBeInTheDocument();
      });
      expect(screen.getByText("No goal cards yet")).toBeInTheDocument();
    });
  });

  // ── Stats row ──

  describe("stats row", () => {
    it("renders correct status counts", async () => {
      mockGetGoalCards.mockResolvedValue(makeCards());
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("Goal Cards")).toBeInTheDocument();
      });
      // Status labels appear in stats row AND filter chips — verify at least 1 exists
      expect(screen.getAllByText("Active").length).toBeGreaterThanOrEqual(1);
      expect(screen.getAllByText("Challenged").length).toBeGreaterThanOrEqual(1);
      expect(screen.getAllByText("Completed").length).toBeGreaterThanOrEqual(1);
      expect(screen.getAllByText("Abandoned").length).toBeGreaterThanOrEqual(1);
      // 2 Active, 1 Challenged, 1 Completed, 1 Abandoned
      expect(screen.getAllByText("2").length).toBeGreaterThanOrEqual(1);
      expect(screen.getAllByText("1").length).toBeGreaterThanOrEqual(3);
    });

    it("shows total count in header", async () => {
      mockGetGoalCards.mockResolvedValue(makeCards());
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("(5)")).toBeInTheDocument();
      });
    });
  });

  // ── Filter chips ──

  describe("filter chips", () => {
    it("filters by status when chip is clicked", async () => {
      const cards = makeCards();
      mockGetGoalCards.mockResolvedValue(cards);
      const user = userEvent.setup();
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("Goal Cards")).toBeInTheDocument();
      });

      // All 5 cards visible initially
      for (const card of cards) {
        expect(screen.getByText(card.agentName)).toBeInTheDocument();
      }

      // Click "Completed" filter chip (use exact role match — filter chips are the
      // only role="button" elements whose full accessible name IS the status text)
      await user.click(screen.getByRole("button", { name: "Completed" }));
      expect(screen.getByText("Agent-3")).toBeInTheDocument();
      expect(screen.queryByText("Agent-0")).not.toBeInTheDocument();
      expect(screen.queryByText("Agent-2")).not.toBeInTheDocument();
    });

    it("filters by verdict when chip is clicked", async () => {
      mockGetGoalCards.mockResolvedValue(makeCards());
      const user = userEvent.setup();
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("Goal Cards")).toBeInTheDocument();
      });

      // Click "Challenge" verdict filter
      await user.click(screen.getByRole("button", { name: "Challenge" }));
      expect(screen.getByText("Agent-2")).toBeInTheDocument();
      expect(screen.queryByText("Agent-1")).not.toBeInTheDocument();
    });

    it("returns to all cards when 'All' filter is clicked", async () => {
      mockGetGoalCards.mockResolvedValue(makeCards());
      const user = userEvent.setup();
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("Goal Cards")).toBeInTheDocument();
      });

      // Filter, then unfilter
      await user.click(screen.getByRole("button", { name: "Completed" }));
      expect(screen.queryByText("Agent-0")).not.toBeInTheDocument();

      await user.click(screen.getByRole("button", { name: "All" }));
      expect(screen.getByText("Agent-0")).toBeInTheDocument();
      expect(screen.getByText("Agent-3")).toBeInTheDocument();
    });

    it("shows 'no matching cards' empty state when filter matches nothing", async () => {
      // All Active cards
      mockGetGoalCards.mockResolvedValue([
        makeCard({ id: "gc-a", status: "Active" }),
      ]);
      const user = userEvent.setup();
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("Goal Cards")).toBeInTheDocument();
      });

      await user.click(screen.getByRole("button", { name: "Abandoned" }));
      expect(screen.getByTestId("empty-state")).toBeInTheDocument();
      expect(screen.getByText("No matching cards")).toBeInTheDocument();
    });

    it("clears filters from 'no matching' empty state action", async () => {
      mockGetGoalCards.mockResolvedValue([
        makeCard({ id: "gc-a", status: "Active", agentName: "Solo" }),
      ]);
      const user = userEvent.setup();
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("Solo")).toBeInTheDocument();
      });

      await user.click(screen.getByRole("button", { name: "Abandoned" }));
      expect(screen.getByTestId("empty-state")).toBeInTheDocument();

      await user.click(screen.getByText("Clear filters"));
      expect(screen.getByText("Solo")).toBeInTheDocument();
    });

    it("combines status + verdict filters", async () => {
      // Agent-0: Active + Proceed, Agent-1: Active + ProceedWithCaveat
      mockGetGoalCards.mockResolvedValue(makeCards());
      const user = userEvent.setup();
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("Goal Cards")).toBeInTheDocument();
      });

      // Filter by Active status
      await user.click(screen.getByRole("button", { name: "Active" }));
      // Agent-0 and Agent-1 should be visible
      expect(screen.getByText("Agent-0")).toBeInTheDocument();
      expect(screen.getByText("Agent-1")).toBeInTheDocument();

      // Additionally filter by Proceed verdict — only Agent-0 has both Active + Proceed
      await user.click(screen.getByRole("button", { name: "Proceed" }));
      expect(screen.getByText("Agent-0")).toBeInTheDocument();
      expect(screen.queryByText("Agent-1")).not.toBeInTheDocument();
    });
  });

  // ── Card rendering ──

  describe("card rendering", () => {
    it("renders agent name, status badge, and verdict badge", async () => {
      mockGetGoalCards.mockResolvedValue([makeCard()]);
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("Architect")).toBeInTheDocument();
      });
      expect(screen.getByTestId("badge-active")).toBeInTheDocument();
      expect(screen.getByTestId("badge-ok")).toBeInTheDocument();
      // Badge text rendered via mock
      expect(screen.getByTestId("badge-ok").textContent).toBe("Proceed");
    });

    it("renders timestamp from createdAt", async () => {
      mockGetGoalCards.mockResolvedValue([
        makeCard({ createdAt: "2026-04-15T10:30:00Z" }),
      ]);
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("2026-04-15")).toBeInTheDocument();
      });
    });

    it("truncates long task descriptions in collapsed view", async () => {
      const longDesc = "A".repeat(200);
      mockGetGoalCards.mockResolvedValue([
        makeCard({ taskDescription: longDesc }),
      ]);
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("Architect")).toBeInTheDocument();
      });
      // Should be truncated to 120 chars + "…"
      const preview = screen.getByText(/^A+…$/);
      expect(preview.textContent).toHaveLength(121); // 120 + "…"
    });

    it("shows full description for short text without ellipsis", async () => {
      mockGetGoalCards.mockResolvedValue([
        makeCard({ taskDescription: "Short task" }),
      ]);
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("Short task")).toBeInTheDocument();
      });
    });
  });

  // ── Card expand/collapse ──

  describe("card expand/collapse", () => {
    it("expands card on click to show all sections", async () => {
      mockGetGoalCards.mockResolvedValue([
        makeCard({
          intent: "Build auth module",
          divergence: "Scope creep risk",
          steelman: "Strong foundation",
          strawman: "Over-engineering",
          freshEyes1: "Perspective one",
          freshEyes2: "Perspective two",
          freshEyes3: "Perspective three",
        }),
      ]);
      const user = userEvent.setup();
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("Architect")).toBeInTheDocument();
      });

      // Expanded sections should not be visible yet
      expect(screen.queryByText("Task Description")).not.toBeInTheDocument();

      // Click the card to expand
      await user.click(screen.getByText("Architect"));

      expect(screen.getByText("Task Description")).toBeInTheDocument();
      expect(screen.getByText("Intent")).toBeInTheDocument();
      expect(screen.getByText("Build auth module")).toBeInTheDocument();
      expect(screen.getByText("Divergence")).toBeInTheDocument();
      expect(screen.getByText("Scope creep risk")).toBeInTheDocument();
      expect(screen.getByText("Steelman")).toBeInTheDocument();
      expect(screen.getByText("Strawman")).toBeInTheDocument();
      expect(screen.getByText("Fresh Eyes")).toBeInTheDocument();
      expect(screen.getByText("Perspective one")).toBeInTheDocument();
      expect(screen.getByText("Perspective two")).toBeInTheDocument();
      expect(screen.getByText("Perspective three")).toBeInTheDocument();
    });

    it("collapses card on second click", async () => {
      mockGetGoalCards.mockResolvedValue([makeCard({ intent: "Build auth" })]);
      const user = userEvent.setup();
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("Architect")).toBeInTheDocument();
      });

      // Expand
      await user.click(screen.getByText("Architect"));
      expect(screen.getByText("Build auth")).toBeInTheDocument();

      // Collapse
      await user.click(screen.getByText("Architect"));
      expect(screen.queryByText("Intent")).not.toBeInTheDocument();
    });

    it("shows metadata (ID, prompt version, updated timestamp) when expanded", async () => {
      mockGetGoalCards.mockResolvedValue([
        makeCard({
          id: "gc-test-123",
          promptVersion: 3,
          updatedAt: "2026-04-02T12:00:00Z",
        }),
      ]);
      const user = userEvent.setup();
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("Architect")).toBeInTheDocument();
      });

      await user.click(screen.getByText("Architect"));
      expect(screen.getByText("ID: gc-test-123")).toBeInTheDocument();
      expect(screen.getByText("Prompt v3")).toBeInTheDocument();
      expect(screen.getByText(/Updated: 2026-04-02/)).toBeInTheDocument();
    });

    it("only expands one card at a time", async () => {
      mockGetGoalCards.mockResolvedValue([
        makeCard({ id: "gc-1", agentName: "Alpha", intent: "Alpha intent" }),
        makeCard({ id: "gc-2", agentName: "Beta", intent: "Beta intent" }),
      ]);
      const user = userEvent.setup();
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("Alpha")).toBeInTheDocument();
      });

      // Expand Alpha
      await user.click(screen.getByText("Alpha"));
      expect(screen.getByText("Alpha intent")).toBeInTheDocument();

      // Expand Beta — Alpha should collapse
      await user.click(screen.getByText("Beta"));
      expect(screen.getByText("Beta intent")).toBeInTheDocument();
      // Alpha's expanded content should be gone (Intent label only shows once for Beta)
      expect(screen.queryByText("Alpha intent")).not.toBeInTheDocument();
    });
  });

  // ── Task navigation ──

  describe("task navigation", () => {
    it("renders task link when taskId and onNavigateToTask are provided", async () => {
      mockGetGoalCards.mockResolvedValue([makeCard({ taskId: "task-42" })]);
      const onNav = vi.fn();
      renderPanel({ onNavigateToTask: onNav });
      await waitFor(() => {
        expect(screen.getByText("→ task")).toBeInTheDocument();
      });
    });

    it("does not render task link when taskId is null", async () => {
      mockGetGoalCards.mockResolvedValue([makeCard({ taskId: null })]);
      const onNav = vi.fn();
      renderPanel({ onNavigateToTask: onNav });
      await waitFor(() => {
        expect(screen.getByText("Architect")).toBeInTheDocument();
      });
      expect(screen.queryByText("→ task")).not.toBeInTheDocument();
    });

    it("does not render task link when onNavigateToTask is not provided", async () => {
      mockGetGoalCards.mockResolvedValue([makeCard({ taskId: "task-42" })]);
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("Architect")).toBeInTheDocument();
      });
      expect(screen.queryByText("→ task")).not.toBeInTheDocument();
    });

    it("calls onNavigateToTask with taskId on click", async () => {
      mockGetGoalCards.mockResolvedValue([makeCard({ taskId: "task-42" })]);
      const onNav = vi.fn();
      const user = userEvent.setup();
      renderPanel({ onNavigateToTask: onNav });
      await waitFor(() => {
        expect(screen.getByText("→ task")).toBeInTheDocument();
      });
      await user.click(screen.getByText("→ task"));
      expect(onNav).toHaveBeenCalledWith("task-42");
    });

    it("does not toggle card expand when task link is clicked", async () => {
      mockGetGoalCards.mockResolvedValue([
        makeCard({ taskId: "task-42", intent: "Auth flow" }),
      ]);
      const onNav = vi.fn();
      const user = userEvent.setup();
      renderPanel({ onNavigateToTask: onNav });
      await waitFor(() => {
        expect(screen.getByText("→ task")).toBeInTheDocument();
      });
      await user.click(screen.getByText("→ task"));
      // Card should NOT expand (stopPropagation)
      expect(screen.queryByText("Intent")).not.toBeInTheDocument();
    });
  });

  // ── Refresh ──

  describe("refresh", () => {
    it("re-fetches data when refresh button is clicked", async () => {
      mockGetGoalCards.mockResolvedValueOnce([makeCard({ agentName: "V1" })]);
      mockGetGoalCards.mockResolvedValueOnce([makeCard({ agentName: "V2" })]);
      const user = userEvent.setup();
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("V1")).toBeInTheDocument();
      });
      expect(mockGetGoalCards).toHaveBeenCalledTimes(1);

      // The refresh button is a Fluent UI icon-only Button — the only <button> element
      // (filter chips are <span role="button">)
      const buttons = screen.getAllByRole("button");
      const refreshBtn = buttons.find((el) => el.tagName === "BUTTON");
      expect(refreshBtn).toBeDefined();
      await user.click(refreshBtn!);
      await waitFor(() => {
        expect(screen.getByText("V2")).toBeInTheDocument();
      });
      expect(mockGetGoalCards).toHaveBeenCalledTimes(2);
    });
  });

  // ── refreshTrigger prop ──

  describe("refreshTrigger prop", () => {
    it("re-fetches when refreshTrigger changes", async () => {
      mockGetGoalCards
        .mockResolvedValueOnce([makeCard({ agentName: "Initial" })])
        .mockResolvedValueOnce([makeCard({ agentName: "Updated" })]);

      const { rerender } = render(
        createElement(
          FluentProvider,
          { theme: webDarkTheme },
          createElement(GoalCardPanel, { refreshTrigger: 0 }),
        ),
      );
      await waitFor(() => {
        expect(screen.getByText("Initial")).toBeInTheDocument();
      });

      rerender(
        createElement(
          FluentProvider,
          { theme: webDarkTheme },
          createElement(GoalCardPanel, { refreshTrigger: 1 }),
        ),
      );
      await waitFor(() => {
        expect(screen.getByText("Updated")).toBeInTheDocument();
      });
      expect(mockGetGoalCards).toHaveBeenCalledTimes(2);
    });

    it("does not re-fetch when refreshTrigger stays the same on rerender", async () => {
      mockGetGoalCards.mockResolvedValue([makeCard()]);
      const { rerender } = render(
        createElement(
          FluentProvider,
          { theme: webDarkTheme },
          createElement(GoalCardPanel, { refreshTrigger: 5 }),
        ),
      );
      await waitFor(() => {
        expect(screen.getByText("Architect")).toBeInTheDocument();
      });

      rerender(
        createElement(
          FluentProvider,
          { theme: webDarkTheme },
          createElement(GoalCardPanel, { refreshTrigger: 5 }),
        ),
      );

      // Should still be called only once (initial fetch)
      expect(mockGetGoalCards).toHaveBeenCalledTimes(1);
    });
  });

  // ── Room scoping ──

  describe("room scoping", () => {
    it("passes roomId to getGoalCards", async () => {
      mockGetGoalCards.mockResolvedValue([]);
      renderPanel({ roomId: "room-42" });
      await waitFor(() => {
        expect(mockGetGoalCards).toHaveBeenCalledWith({ roomId: "room-42" });
      });
    });

    it("passes undefined roomId when prop is null", async () => {
      mockGetGoalCards.mockResolvedValue([]);
      renderPanel({ roomId: null });
      await waitFor(() => {
        expect(mockGetGoalCards).toHaveBeenCalledWith({ roomId: undefined });
      });
    });

    it("passes undefined roomId when prop is omitted", async () => {
      mockGetGoalCards.mockResolvedValue([]);
      renderPanel();
      await waitFor(() => {
        expect(mockGetGoalCards).toHaveBeenCalledWith({ roomId: undefined });
      });
    });
  });

  // ── Badge mapping ──

  describe("badge mapping", () => {
    it("maps all status values to correct badge colors", async () => {
      mockGetGoalCards.mockResolvedValue([
        makeCard({ id: "a", status: "Active", agentName: "A" }),
        makeCard({ id: "b", status: "Challenged", agentName: "B" }),
        makeCard({ id: "c", status: "Completed", agentName: "C" }),
        makeCard({ id: "d", status: "Abandoned", agentName: "D" }),
      ]);
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("A")).toBeInTheDocument();
      });
      expect(screen.getByTestId("badge-active")).toBeInTheDocument();
      expect(screen.getByTestId("badge-warn")).toBeInTheDocument();
      expect(screen.getByTestId("badge-done")).toBeInTheDocument();
      expect(screen.getByTestId("badge-cancel")).toBeInTheDocument();
    });

    it("maps verdict values to correct badge colors", async () => {
      mockGetGoalCards.mockResolvedValue([
        makeCard({ id: "a", verdict: "Proceed", agentName: "A" }),
        makeCard({ id: "b", verdict: "ProceedWithCaveat", agentName: "B" }),
        makeCard({ id: "c", verdict: "Challenge", agentName: "C" }),
      ]);
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("A")).toBeInTheDocument();
      });
      expect(screen.getByTestId("badge-ok")).toBeInTheDocument();
      expect(screen.getByTestId("badge-review")).toBeInTheDocument();
      expect(screen.getByTestId("badge-err")).toBeInTheDocument();
    });
  });

  // ── Stale fetch guard ──

  describe("stale fetch guard", () => {
    it("ignores results from superseded fetches", async () => {
      let resolveFirst: (v: GoalCard[]) => void;
      const firstPromise = new Promise<GoalCard[]>((r) => {
        resolveFirst = r;
      });
      const secondCards = [makeCard({ agentName: "Latest" })];

      mockGetGoalCards
        .mockReturnValueOnce(firstPromise)
        .mockResolvedValueOnce(secondCards);

      const { rerender } = render(
        createElement(
          FluentProvider,
          { theme: webDarkTheme },
          createElement(GoalCardPanel, { refreshTrigger: 0 }),
        ),
      );

      // Trigger second fetch before first resolves
      rerender(
        createElement(
          FluentProvider,
          { theme: webDarkTheme },
          createElement(GoalCardPanel, { refreshTrigger: 1 }),
        ),
      );

      await waitFor(() => {
        expect(screen.getByText("Latest")).toBeInTheDocument();
      });

      // Now resolve the stale first fetch — it should be ignored
      resolveFirst!([makeCard({ agentName: "Stale" })]);

      // Wait a tick and verify "Stale" never appears
      await new Promise((r) => setTimeout(r, 50));
      expect(screen.queryByText("Stale")).not.toBeInTheDocument();
      expect(screen.getByText("Latest")).toBeInTheDocument();
    });
  });

  // ── Status mutation ──

  describe("status mutation", () => {
    it("shows Complete and Abandon buttons for Active cards when expanded", async () => {
      mockGetGoalCards.mockResolvedValue([makeCard({ status: "Active" })]);
      const user = userEvent.setup();
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("Architect")).toBeInTheDocument();
      });

      // Expand the card
      await user.click(screen.getByText("Architect"));
      expect(screen.getByText("Complete")).toBeInTheDocument();
      expect(screen.getByText("Abandon")).toBeInTheDocument();
    });

    it("shows Reactivate and Abandon buttons for Challenged cards when expanded", async () => {
      mockGetGoalCards.mockResolvedValue([
        makeCard({ status: "Challenged" }),
      ]);
      const user = userEvent.setup();
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("Architect")).toBeInTheDocument();
      });

      await user.click(screen.getByText("Architect"));
      expect(screen.getByText("Reactivate")).toBeInTheDocument();
      expect(screen.getByText("Abandon")).toBeInTheDocument();
    });

    it("shows no action buttons for Completed cards", async () => {
      mockGetGoalCards.mockResolvedValue([
        makeCard({ status: "Completed" }),
      ]);
      const user = userEvent.setup();
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("Architect")).toBeInTheDocument();
      });

      await user.click(screen.getByText("Architect"));
      expect(screen.queryByText("Complete")).not.toBeInTheDocument();
      expect(screen.queryByText("Abandon")).not.toBeInTheDocument();
      expect(screen.queryByText("Reactivate")).not.toBeInTheDocument();
    });

    it("shows no action buttons for Abandoned cards", async () => {
      mockGetGoalCards.mockResolvedValue([
        makeCard({ status: "Abandoned" }),
      ]);
      const user = userEvent.setup();
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("Architect")).toBeInTheDocument();
      });

      await user.click(screen.getByText("Architect"));
      expect(screen.queryByText("Complete")).not.toBeInTheDocument();
      expect(screen.queryByText("Abandon")).not.toBeInTheDocument();
      expect(screen.queryByText("Reactivate")).not.toBeInTheDocument();
    });

    it("optimistically updates status badge on Complete click", async () => {
      const card = makeCard({ status: "Active" });
      mockGetGoalCards.mockResolvedValue([card]);
      mockUpdateGoalCardStatus.mockResolvedValue({
        ...card,
        status: "Completed",
        updatedAt: "2026-04-20T19:00:00Z",
      });
      const user = userEvent.setup();
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("Architect")).toBeInTheDocument();
      });

      // Expand and click Complete
      await user.click(screen.getByText("Architect"));
      await user.click(screen.getByText("Complete"));

      // Badge should update
      await waitFor(() => {
        expect(screen.getByTestId("badge-done")).toBeInTheDocument();
      });
      expect(mockUpdateGoalCardStatus).toHaveBeenCalledWith("gc-1", "Completed");
    });

    it("reverts on API failure and shows error message", async () => {
      const card = makeCard({ status: "Active" });
      mockGetGoalCards.mockResolvedValue([card]);
      mockUpdateGoalCardStatus.mockRejectedValue(new Error("Server error"));
      const user = userEvent.setup();
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("Architect")).toBeInTheDocument();
      });

      await user.click(screen.getByText("Architect"));
      await user.click(screen.getByText("Complete"));

      // Should revert to Active badge and show error
      await waitFor(() => {
        expect(screen.getByText("Server error")).toBeInTheDocument();
      });
      expect(screen.getByTestId("badge-active")).toBeInTheDocument();
    });

    it("calls API with correct args for Abandon", async () => {
      const card = makeCard({ status: "Active" });
      mockGetGoalCards.mockResolvedValue([card]);
      mockUpdateGoalCardStatus.mockResolvedValue({
        ...card,
        status: "Abandoned",
      });
      const user = userEvent.setup();
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("Architect")).toBeInTheDocument();
      });

      await user.click(screen.getByText("Architect"));
      await user.click(screen.getByText("Abandon"));

      await waitFor(() => {
        expect(mockUpdateGoalCardStatus).toHaveBeenCalledWith("gc-1", "Abandoned");
      });
    });

    it("does not expand/collapse card when action button is clicked", async () => {
      mockGetGoalCards.mockResolvedValue([makeCard({ status: "Active", intent: "Auth flow" })]);
      mockUpdateGoalCardStatus.mockResolvedValue(makeCard({ status: "Completed" }));
      const user = userEvent.setup();
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("Architect")).toBeInTheDocument();
      });

      // Expand
      await user.click(screen.getByText("Architect"));
      expect(screen.getByText("Auth flow")).toBeInTheDocument();

      // Click Complete — card should stay expanded (stopPropagation)
      await user.click(screen.getByText("Complete"));
      expect(screen.getByText("Intent")).toBeInTheDocument();
    });
  });
});
