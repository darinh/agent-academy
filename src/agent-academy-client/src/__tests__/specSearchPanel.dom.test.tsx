// @vitest-environment jsdom
/**
 * DOM tests for SpecSearchPanel.
 *
 * Covers: initial empty state, search input, debounced search, results
 * rendering with scores, no-results state, error state, Enter key shortcut.
 */
import "@testing-library/jest-dom/vitest";
import { cleanup, render, screen, waitFor, act } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createElement } from "react";
import { FluentProvider, webDarkTheme } from "@fluentui/react-components";

// ── Mocks ──────────────────────────────────────────────────────────────

vi.mock("../api", () => ({
  searchSpecs: vi.fn(),
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
  default: ({ title, detail }: { title: string; detail?: string }) =>
    createElement(
      "div",
      { "data-testid": "empty-state" },
      createElement("span", null, title),
      detail && createElement("span", null, detail),
    ),
}));

import SpecSearchPanel from "../SpecSearchPanel";
import type { SpecSearchResult } from "../api";
import { searchSpecs } from "../api";

const mockSearchSpecs = vi.mocked(searchSpecs);

// ── Helpers ────────────────────────────────────────────────────────────

function wrap(ui: React.ReactNode) {
  return createElement(FluentProvider, { theme: webDarkTheme }, ui);
}

function makeResult(
  overrides: Partial<SpecSearchResult> = {},
): SpecSearchResult {
  return {
    id: "spec-1",
    heading: "Authentication Flow",
    summary: "Describes the OAuth2 authentication flow",
    filePath: "specs/auth.md",
    score: 0.95,
    matchedTerms: "auth, oauth",
    ...overrides,
  };
}

// ── Tests ──────────────────────────────────────────────────────────────

describe("SpecSearchPanel", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.useFakeTimers({ shouldAdvanceTime: true });
  });

  afterEach(() => {
    vi.useRealTimers();
    cleanup();
  });

  it("shows initial empty state with search prompt", () => {
    render(wrap(createElement(SpecSearchPanel)));
    expect(screen.getByText("Search specifications")).toBeInTheDocument();
  });

  it("renders search input with placeholder", () => {
    render(wrap(createElement(SpecSearchPanel)));
    expect(
      screen.getByPlaceholderText("Search specifications…"),
    ).toBeInTheDocument();
  });

  it("searches after debounce delay", async () => {
    mockSearchSpecs.mockResolvedValue([makeResult()]);
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    render(wrap(createElement(SpecSearchPanel)));

    await user.type(screen.getByLabelText("Search specifications"), "auth");
    // Before debounce fires, search should NOT have been called
    expect(mockSearchSpecs).not.toHaveBeenCalled();

    // Advance past debounce
    await act(async () => {
      vi.advanceTimersByTime(500);
    });

    await waitFor(() => {
      expect(mockSearchSpecs).toHaveBeenCalledWith("auth", 20);
    });
    expect(screen.getByText("Authentication Flow")).toBeInTheDocument();
  });

  it("shows result score as percentage", async () => {
    mockSearchSpecs.mockResolvedValue([makeResult({ score: 0.87 })]);
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    render(wrap(createElement(SpecSearchPanel)));

    await user.type(screen.getByLabelText("Search specifications"), "flow");
    await act(async () => {
      vi.advanceTimersByTime(500);
    });

    await waitFor(() => {
      expect(screen.getByText("87%")).toBeInTheDocument();
    });
  });

  it("shows result details: heading, summary, file path, matched terms", async () => {
    mockSearchSpecs.mockResolvedValue([
      makeResult({
        heading: "API Contracts",
        summary: "Defines REST endpoints",
        filePath: "specs/api.md",
        matchedTerms: "rest, endpoint",
      }),
    ]);
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    render(wrap(createElement(SpecSearchPanel)));

    await user.type(screen.getByLabelText("Search specifications"), "api");
    await act(async () => {
      vi.advanceTimersByTime(500);
    });

    await waitFor(() => {
      expect(screen.getByText("API Contracts")).toBeInTheDocument();
    });
    expect(screen.getByText("Defines REST endpoints")).toBeInTheDocument();
    expect(screen.getByText("specs/api.md")).toBeInTheDocument();
    expect(screen.getByText("matched: rest, endpoint")).toBeInTheDocument();
  });

  it("shows no-results state when search returns empty", async () => {
    mockSearchSpecs.mockResolvedValue([]);
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    render(wrap(createElement(SpecSearchPanel)));

    await user.type(
      screen.getByLabelText("Search specifications"),
      "nonexistent",
    );
    await act(async () => {
      vi.advanceTimersByTime(500);
    });

    await waitFor(() => {
      expect(screen.getByText("No results")).toBeInTheDocument();
    });
  });

  it("shows error message on search failure", async () => {
    mockSearchSpecs.mockRejectedValue(new Error("Search index unavailable"));
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    render(wrap(createElement(SpecSearchPanel)));

    await user.type(screen.getByLabelText("Search specifications"), "test");
    await act(async () => {
      vi.advanceTimersByTime(500);
    });

    await waitFor(() => {
      expect(
        screen.getByText(/Search index unavailable/),
      ).toBeInTheDocument();
    });
  });

  it("searches immediately on Enter key without waiting for debounce", async () => {
    mockSearchSpecs.mockResolvedValue([makeResult()]);
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    render(wrap(createElement(SpecSearchPanel)));

    const input = screen.getByLabelText("Search specifications");
    await user.type(input, "auth{Enter}");

    // Should have been called exactly once — the Enter path, not debounce
    await waitFor(() => {
      expect(mockSearchSpecs).toHaveBeenCalledWith("auth", 20);
    });
    expect(mockSearchSpecs).toHaveBeenCalledTimes(1);

    // Advance past debounce — should NOT trigger a second call
    await act(async () => {
      vi.advanceTimersByTime(500);
    });
    expect(mockSearchSpecs).toHaveBeenCalledTimes(1);
  });

  it("renders multiple results", async () => {
    mockSearchSpecs.mockResolvedValue([
      makeResult({ id: "s1", heading: "Auth" }),
      makeResult({ id: "s2", heading: "Database" }),
      makeResult({ id: "s3", heading: "Events" }),
    ]);
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    render(wrap(createElement(SpecSearchPanel)));

    await user.type(screen.getByLabelText("Search specifications"), "all");
    await act(async () => {
      vi.advanceTimersByTime(500);
    });

    await waitFor(() => {
      expect(screen.getByText("Auth")).toBeInTheDocument();
    });
    expect(screen.getByText("Database")).toBeInTheDocument();
    expect(screen.getByText("Events")).toBeInTheDocument();
  });
});
