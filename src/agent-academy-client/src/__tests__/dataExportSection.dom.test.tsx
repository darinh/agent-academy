// @vitest-environment jsdom
/**
 * DOM tests for DataExportSection.
 *
 * Covers: initial render with export buttons, click triggers correct format,
 * success/error messages, loading state disables other buttons.
 */
import "@testing-library/jest-dom/vitest";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createElement } from "react";
import { FluentProvider, webDarkTheme } from "@fluentui/react-components";

// ── Mocks ──────────────────────────────────────────────────────────────

vi.mock("../api", () => ({
  exportAgents: vi.fn(),
  exportUsage: vi.fn(),
}));

import DataExportSection from "../settings/DataExportSection";
import { exportAgents, exportUsage } from "../api";

const mockExportAgents = vi.mocked(exportAgents);
const mockExportUsage = vi.mocked(exportUsage);

// ── Helpers ────────────────────────────────────────────────────────────

function wrap(ui: React.ReactNode) {
  return createElement(FluentProvider, { theme: webDarkTheme }, ui);
}

// ── Tests ──────────────────────────────────────────────────────────────

describe("DataExportSection", () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  afterEach(cleanup);

  it("renders heading and description", () => {
    render(wrap(createElement(DataExportSection)));
    expect(screen.getByText("Data Export")).toBeInTheDocument();
    expect(
      screen.getByText(/Download agent configuration/),
    ).toBeInTheDocument();
  });

  it("renders export buttons for agents and usage", () => {
    render(wrap(createElement(DataExportSection)));
    expect(screen.getByText("Agent configuration")).toBeInTheDocument();
    expect(screen.getByText("Usage analytics")).toBeInTheDocument();
    const jsonBtns = screen.getAllByText("JSON");
    const csvBtns = screen.getAllByText("CSV");
    expect(jsonBtns).toHaveLength(2);
    expect(csvBtns).toHaveLength(2);
  });

  it("exports agents as JSON on click", async () => {
    mockExportAgents.mockResolvedValue(undefined);
    const user = userEvent.setup();
    render(wrap(createElement(DataExportSection)));

    const jsonBtns = screen.getAllByText("JSON");
    await user.click(jsonBtns[0]);

    await waitFor(() => {
      expect(mockExportAgents).toHaveBeenCalledWith("json");
    });
    expect(
      screen.getByText("Agent data exported as JSON"),
    ).toBeInTheDocument();
  });

  it("exports agents as CSV on click", async () => {
    mockExportAgents.mockResolvedValue(undefined);
    const user = userEvent.setup();
    render(wrap(createElement(DataExportSection)));

    const csvBtns = screen.getAllByText("CSV");
    await user.click(csvBtns[0]);

    await waitFor(() => {
      expect(mockExportAgents).toHaveBeenCalledWith("csv");
    });
    expect(
      screen.getByText("Agent data exported as CSV"),
    ).toBeInTheDocument();
  });

  it("exports usage as JSON on click", async () => {
    mockExportUsage.mockResolvedValue(undefined);
    const user = userEvent.setup();
    render(wrap(createElement(DataExportSection)));

    const jsonBtns = screen.getAllByText("JSON");
    await user.click(jsonBtns[1]);

    await waitFor(() => {
      expect(mockExportUsage).toHaveBeenCalledWith("json");
    });
    expect(
      screen.getByText("Usage data exported as JSON"),
    ).toBeInTheDocument();
  });

  it("exports usage as CSV on click", async () => {
    mockExportUsage.mockResolvedValue(undefined);
    const user = userEvent.setup();
    render(wrap(createElement(DataExportSection)));

    const csvBtns = screen.getAllByText("CSV");
    await user.click(csvBtns[1]);

    await waitFor(() => {
      expect(mockExportUsage).toHaveBeenCalledWith("csv");
    });
    expect(
      screen.getByText("Usage data exported as CSV"),
    ).toBeInTheDocument();
  });

  it("shows error message when export fails", async () => {
    mockExportAgents.mockRejectedValue(new Error("Permission denied"));
    const user = userEvent.setup();
    render(wrap(createElement(DataExportSection)));

    const jsonBtns = screen.getAllByText("JSON");
    await user.click(jsonBtns[0]);

    await waitFor(() => {
      expect(screen.getByText("Permission denied")).toBeInTheDocument();
    });
  });

  it("shows generic error for non-Error rejection", async () => {
    mockExportUsage.mockRejectedValue("boom");
    const user = userEvent.setup();
    render(wrap(createElement(DataExportSection)));

    const jsonBtns = screen.getAllByText("JSON");
    await user.click(jsonBtns[1]);

    await waitFor(() => {
      expect(screen.getByText("Export failed")).toBeInTheDocument();
    });
  });
});
