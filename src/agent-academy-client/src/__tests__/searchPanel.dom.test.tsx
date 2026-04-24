// @vitest-environment jsdom
import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it, vi, beforeEach, afterEach } from "vitest";
import { FluentProvider, webDarkTheme } from "@fluentui/react-components";
import { createElement } from "react";
import { render, screen, fireEvent, waitFor, cleanup } from "@testing-library/react";

// ── Mocks ──────────────────────────────────────────────────────────────

vi.mock("../api", () => ({
  searchWorkspace: vi.fn(),
}));

import SearchPanel from "../SearchPanel";
import type { SearchResults } from "../api";
import { searchWorkspace } from "../api";

const mockSearch = vi.mocked(searchWorkspace);

function makeResults(overrides: Partial<SearchResults> = {}): SearchResults {
  return {
    messages: [],
    tasks: [],
    totalCount: 0,
    query: "test",
    ...overrides,
  };
}

function wrap(el: React.ReactElement) {
  return createElement(FluentProvider, { theme: webDarkTheme }, el);
}

beforeEach(() => {
  vi.useFakeTimers({ shouldAdvanceTime: true });
  mockSearch.mockReset();
});

afterEach(() => {
  cleanup();
  vi.useRealTimers();
});

// ── SSR Tests ──────────────────────────────────────────────────────────

describe("SearchPanel (SSR)", () => {
  it("renders without crashing", () => {
    const html = renderToStaticMarkup(
      wrap(createElement(SearchPanel)),
    );
    expect(html).toContain("Search messages");
  });

  it("renders scope filter buttons", () => {
    const html = renderToStaticMarkup(
      wrap(createElement(SearchPanel)),
    );
    expect(html).toContain("All");
    expect(html).toContain("Messages");
    expect(html).toContain("Tasks");
  });

  it("renders empty state initially", () => {
    const html = renderToStaticMarkup(
      wrap(createElement(SearchPanel)),
    );
    expect(html).toContain("Search across all room messages and tasks");
  });
});

// ── Interactive Tests ──────────────────────────────────────────────────

describe("SearchPanel (interactive)", () => {
  it("renders search input with placeholder", () => {
    render(wrap(createElement(SearchPanel)));
    const inputs = screen.getAllByPlaceholderText(/search messages/i);
    expect(inputs.length).toBeGreaterThan(0);
  });

  it("renders all scope buttons", () => {
    render(wrap(createElement(SearchPanel)));
    expect(screen.getAllByText("All").length).toBeGreaterThan(0);
    expect(screen.getAllByText("Messages").length).toBeGreaterThan(0);
    expect(screen.getAllByText("Tasks").length).toBeGreaterThan(0);
  });

  it("shows no results message when search returns empty", async () => {
    mockSearch.mockResolvedValue(makeResults({ query: "nonexistent" }));
    render(wrap(createElement(SearchPanel)));

    const input = screen.getAllByPlaceholderText(/search messages/i)[0];
    fireEvent.change(input, { target: { value: "nonexistent" } });

    await waitFor(() => {
      expect(screen.getByText(/no results for/i)).toBeTruthy();
    });
  });

  it("calls searchWorkspace with typed query", async () => {
    mockSearch.mockResolvedValue(makeResults());
    render(wrap(createElement(SearchPanel)));

    const input = screen.getAllByPlaceholderText(/search messages/i)[0];
    fireEvent.change(input, { target: { value: "oauth" } });

    await waitFor(() => {
      expect(mockSearch).toHaveBeenCalledWith("oauth", expect.objectContaining({ scope: "all" }));
    });
  });

  it("displays message results with sender name and room name", async () => {
    mockSearch.mockResolvedValue(makeResults({
      messages: [{
        messageId: "m1",
        roomId: "r1",
        roomName: "Planning Room",
        senderName: "Hephaestus",
        senderKind: "Agent",
        senderRole: "SoftwareEngineer",
        snippet: "The authentication module",
        sentAt: "2026-04-12T00:00:00Z",
        sessionId: null,
        source: "room",
      }],
      totalCount: 1,
      query: "authentication",
    }));
    render(wrap(createElement(SearchPanel)));

    const input = screen.getAllByPlaceholderText(/search messages/i)[0];
    fireEvent.change(input, { target: { value: "authentication" } });

    await waitFor(() => {
      expect(screen.getByText("Hephaestus")).toBeTruthy();
      expect(screen.getByText("Planning Room")).toBeTruthy();
    });
  });

  it("displays task results with title and status badge", async () => {
    mockSearch.mockResolvedValue(makeResults({
      tasks: [{
        taskId: "t1",
        title: "Implement OAuth",
        status: "Active",
        assignedAgentName: "Hephaestus",
        snippet: "Add OAuth login flow",
        createdAt: "2026-04-12T00:00:00Z",
        roomId: "r1",
      }],
      totalCount: 1,
      query: "oauth",
    }));
    render(wrap(createElement(SearchPanel)));

    const input = screen.getAllByPlaceholderText(/search messages/i)[0];
    fireEvent.change(input, { target: { value: "oauth" } });

    await waitFor(() => {
      expect(screen.getByText("Implement OAuth")).toBeTruthy();
    });
  });

  it("shows breakout badge for breakout messages", async () => {
    mockSearch.mockResolvedValue(makeResults({
      messages: [{
        messageId: "m2",
        roomId: "r1",
        roomName: "Main",
        senderName: "Socrates",
        senderKind: "Agent",
        senderRole: "Reviewer",
        snippet: "Review completed",
        sentAt: "2026-04-12T00:00:00Z",
        sessionId: null,
        source: "breakout",
      }],
      totalCount: 1,
      query: "review",
    }));
    render(wrap(createElement(SearchPanel)));

    const input = screen.getAllByPlaceholderText(/search messages/i)[0];
    fireEvent.change(input, { target: { value: "review" } });

    await waitFor(() => {
      expect(screen.getByText("breakout")).toBeTruthy();
    });
  });

  it("shows total count in status bar", async () => {
    mockSearch.mockResolvedValue(makeResults({
      messages: [{
        messageId: "m1",
        roomId: "r1",
        roomName: "Main",
        senderName: "Agent",
        senderKind: "Agent",
        senderRole: null,
        snippet: "Found",
        sentAt: "2026-04-12T00:00:00Z",
        sessionId: null,
        source: "room",
      }],
      tasks: [{
        taskId: "t1",
        title: "Task",
        status: "Active",
        assignedAgentName: null,
        snippet: "Found",
        createdAt: "2026-04-12T00:00:00Z",
        roomId: null,
      }],
      totalCount: 2,
      query: "found",
    }));
    render(wrap(createElement(SearchPanel)));

    const input = screen.getAllByPlaceholderText(/search messages/i)[0];
    fireEvent.change(input, { target: { value: "found" } });

    await waitFor(() => {
      expect(screen.getByText(/2 results for "found"/)).toBeTruthy();
    });
  });

  it("renders message results as clickable cards with room context", async () => {
    mockSearch.mockResolvedValue(makeResults({
      messages: [{
        messageId: "m1",
        roomId: "room-42",
        roomName: "Main",
        senderName: "TestAgent",
        senderKind: "Agent",
        senderRole: null,
        snippet: "Click me now",
        sentAt: "2026-04-12T00:00:00Z",
        sessionId: null,
        source: "room",
      }],
      totalCount: 1,
      query: "click",
    }));
    render(wrap(createElement(SearchPanel, { onNavigateToRoom: vi.fn() })));

    const input = screen.getAllByPlaceholderText(/search messages/i)[0];
    fireEvent.change(input, { target: { value: "click" } });

    await waitFor(() => {
      expect(screen.getByText("TestAgent")).toBeTruthy();
      expect(screen.getByText("Main")).toBeTruthy();
      expect(screen.getByText("Click me now")).toBeTruthy();
    });
  });

  it("renders task results as clickable cards", async () => {
    mockSearch.mockResolvedValue(makeResults({
      tasks: [{
        taskId: "t1",
        title: "Click Task",
        status: "Active",
        assignedAgentName: null,
        snippet: "Details here",
        createdAt: "2026-04-12T00:00:00Z",
        roomId: null,
      }],
      totalCount: 1,
      query: "click",
    }));
    render(wrap(createElement(SearchPanel, { onNavigateToTasks: vi.fn() })));

    const input = screen.getAllByPlaceholderText(/search messages/i)[0];
    fireEvent.change(input, { target: { value: "click" } });

    await waitFor(() => {
      expect(screen.getByText("Click Task")).toBeTruthy();
      expect(screen.getByText("Details here")).toBeTruthy();
    });
  });

  it("shows assigned agent name on task results", async () => {
    mockSearch.mockResolvedValue(makeResults({
      tasks: [{
        taskId: "t1",
        title: "Task",
        status: "Active",
        assignedAgentName: "Hephaestus",
        snippet: "Details",
        createdAt: "2026-04-12T00:00:00Z",
        roomId: null,
      }],
      totalCount: 1,
      query: "task",
    }));
    render(wrap(createElement(SearchPanel)));

    const input = screen.getAllByPlaceholderText(/search messages/i)[0];
    fireEvent.change(input, { target: { value: "task" } });

    await waitFor(() => {
      expect(screen.getByText(/Hephaestus/)).toBeTruthy();
    });
  });

  it("calls onNavigateToTask with taskId when clicking a task result", async () => {
    const onNavigateToTask = vi.fn();
    mockSearch.mockResolvedValue(makeResults({
      tasks: [{
        taskId: "task-42",
        title: "Focus Task",
        status: "Active",
        assignedAgentName: null,
        snippet: "Should navigate",
        createdAt: "2026-04-12T00:00:00Z",
        roomId: null,
      }],
      totalCount: 1,
      query: "focus",
    }));
    render(wrap(createElement(SearchPanel, { onNavigateToTask })));

    const input = screen.getAllByPlaceholderText(/search messages/i)[0];
    fireEvent.change(input, { target: { value: "focus" } });

    await waitFor(() => {
      expect(screen.getByText("Focus Task")).toBeTruthy();
    });
    fireEvent.click(screen.getByText("Focus Task"));
    expect(onNavigateToTask).toHaveBeenCalledWith("task-42");
  });

  it("falls back to onNavigateToTasks when onNavigateToTask is not provided", async () => {
    const onNavigateToTasks = vi.fn();
    mockSearch.mockResolvedValue(makeResults({
      tasks: [{
        taskId: "task-99",
        title: "Fallback Task",
        status: "Active",
        assignedAgentName: null,
        snippet: "Should fallback",
        createdAt: "2026-04-12T00:00:00Z",
        roomId: null,
      }],
      totalCount: 1,
      query: "fallback",
    }));
    render(wrap(createElement(SearchPanel, { onNavigateToTasks })));

    const input = screen.getAllByPlaceholderText(/search messages/i)[0];
    fireEvent.change(input, { target: { value: "fallback" } });

    await waitFor(() => {
      expect(screen.getByText("Fallback Task")).toBeTruthy();
    });
    fireEvent.click(screen.getByText("Fallback Task"));
    expect(onNavigateToTasks).toHaveBeenCalled();
  });
});
