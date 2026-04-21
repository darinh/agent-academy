// @vitest-environment jsdom
/**
 * DOM tests for LoadExistingSection.
 *
 * Covers: loading state, empty state, error state with retry,
 * workspace list rendering, click to select, relative time display.
 */
import "@testing-library/jest-dom/vitest";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { createElement } from "react";

vi.mock("../api", () => ({
  listWorkspaces: vi.fn(),
}));

vi.mock("@fluentui/react-components", () => ({
  Button: ({ children, onClick, disabled, appearance, ...rest }: any) => (
    <button onClick={onClick} disabled={disabled} data-appearance={appearance} {...rest}>
      {children}
    </button>
  ),
  Spinner: ({ label }: any) => <span data-testid="spinner">{label ?? "Loading..."}</span>,
  makeStyles: () => () => ({}),
  shorthands: new Proxy({}, { get: () => () => ({}) }),
}));

import LoadExistingSection from "../projectSelector/LoadExistingSection";
import { listWorkspaces } from "../api";

const mockList = vi.mocked(listWorkspaces);

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

describe("LoadExistingSection", () => {
  const onProjectSelected = vi.fn();

  function renderIt() {
    return render(createElement(LoadExistingSection, { onProjectSelected }));
  }

  it("shows loading spinner initially", () => {
    mockList.mockReturnValue(new Promise(() => {})); // never resolves
    renderIt();
    expect(screen.getByTestId("spinner")).toBeInTheDocument();
    expect(screen.getByText(/loading workspaces/i)).toBeInTheDocument();
  });

  it("shows empty state when no workspaces", async () => {
    mockList.mockResolvedValueOnce([]);
    renderIt();
    await waitFor(() => {
      expect(screen.getByText(/no existing projects/i)).toBeInTheDocument();
    });
  });

  it("renders workspace cards when workspaces are returned", async () => {
    mockList.mockResolvedValueOnce([
      { path: "/home/user/projects/alpha", projectName: "Alpha", lastAccessedAt: null },
      { path: "/home/user/projects/beta", projectName: null, lastAccessedAt: null },
    ]);

    renderIt();
    await waitFor(() => {
      expect(screen.getByText("Alpha")).toBeInTheDocument();
      expect(screen.getByText("beta")).toBeInTheDocument(); // falls back to last path segment
    });
  });

  it("calls onProjectSelected when a workspace card is clicked", async () => {
    mockList.mockResolvedValueOnce([
      { path: "/proj/my-app", projectName: "My App", lastAccessedAt: null },
    ]);

    renderIt();
    await waitFor(() => {
      expect(screen.getByText("My App")).toBeInTheDocument();
    });
    await userEvent.click(screen.getByTitle("Open My App"));
    expect(onProjectSelected).toHaveBeenCalledWith("/proj/my-app");
  });

  it("shows error state with retry button on fetch failure", async () => {
    mockList.mockRejectedValueOnce(new Error("Network error"));

    renderIt();
    await waitFor(() => {
      expect(screen.getByText(/failed to load workspaces/i)).toBeInTheDocument();
    });

    // Retry should re-fetch
    mockList.mockResolvedValueOnce([
      { path: "/proj/recovery", projectName: "Recovery", lastAccessedAt: null },
    ]);
    await userEvent.click(screen.getByRole("button", { name: /retry/i }));
    await waitFor(() => {
      expect(screen.getByText("Recovery")).toBeInTheDocument();
    });
  });

  it("shows path on workspace cards", async () => {
    mockList.mockResolvedValueOnce([
      { path: "/data/projects/cool", projectName: "Cool", lastAccessedAt: null },
    ]);

    renderIt();
    await waitFor(() => {
      expect(screen.getByText("/data/projects/cool")).toBeInTheDocument();
    });
  });

  it("shows 'New workspace' when lastAccessedAt is null", async () => {
    mockList.mockResolvedValueOnce([
      { path: "/proj/new", projectName: "New", lastAccessedAt: null },
    ]);

    renderIt();
    await waitFor(() => {
      expect(screen.getByText("New workspace")).toBeInTheDocument();
    });
  });

  it("displays first character of project name as icon", async () => {
    mockList.mockResolvedValueOnce([
      { path: "/proj/zeta", projectName: "Zeta", lastAccessedAt: null },
    ]);

    renderIt();
    await waitFor(() => {
      expect(screen.getByText("Z")).toBeInTheDocument();
    });
  });
});
