// @vitest-environment jsdom
/**
 * DOM tests for ModelsTab.
 *
 * Covers: loading state, error state, empty models, model list rendering,
 * executor status badge (operational / degraded), model count.
 */
import "@testing-library/jest-dom/vitest";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createElement } from "react";
import { FluentProvider, webDarkTheme } from "@fluentui/react-components";

// ── Mocks ──────────────────────────────────────────────────────────────

vi.mock("../api", () => ({
  getAvailableModels: vi.fn(),
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

vi.mock("../settings/settingsStyles", () => ({
  useSettingsStyles: () => ({
    sectionTitle: "mock-section-title",
    emptyState: "mock-empty-state",
    errorText: "mock-error-text",
  }),
}));

import ModelsTab from "../settings/ModelsTab";
import type { ModelsResponse } from "../api";
import { getAvailableModels } from "../api";

const mockGetModels = vi.mocked(getAvailableModels);

// ── Helpers ────────────────────────────────────────────────────────────

function wrap(ui: React.ReactNode) {
  return createElement(FluentProvider, { theme: webDarkTheme }, ui);
}

function makeModelsResponse(
  count: number,
  operational = true,
): ModelsResponse {
  return {
    models: Array.from({ length: count }, (_, i) => ({
      id: `model-${i + 1}`,
      name: `Model ${i + 1}`,
    })),
    executorOperational: operational,
  };
}

// ── Tests ──────────────────────────────────────────────────────────────

describe("ModelsTab", () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  afterEach(cleanup);

  it("shows loading spinner while fetching", () => {
    mockGetModels.mockReturnValue(new Promise(() => {}));
    render(wrap(createElement(ModelsTab)));
    expect(screen.getByText("Loading models…")).toBeInTheDocument();
  });

  it("shows error message on fetch failure", async () => {
    mockGetModels.mockRejectedValue(new Error("Network error"));
    render(wrap(createElement(ModelsTab)));
    await waitFor(() => {
      expect(screen.getByText("Network error")).toBeInTheDocument();
    });
  });

  it("shows generic error for non-Error rejection", async () => {
    mockGetModels.mockRejectedValue("boom");
    render(wrap(createElement(ModelsTab)));
    await waitFor(() => {
      expect(screen.getByText("Failed to load models")).toBeInTheDocument();
    });
  });

  it("shows empty state when no models are configured", async () => {
    mockGetModels.mockResolvedValue(makeModelsResponse(0));
    render(wrap(createElement(ModelsTab)));
    await waitFor(() => {
      expect(screen.getByText("No models configured")).toBeInTheDocument();
    });
    expect(screen.getByText("0 models available")).toBeInTheDocument();
  });

  it("renders model list with names and IDs", async () => {
    mockGetModels.mockResolvedValue(makeModelsResponse(3));
    render(wrap(createElement(ModelsTab)));
    await waitFor(() => {
      expect(screen.getByText("Model 1")).toBeInTheDocument();
    });
    expect(screen.getByText("model-1")).toBeInTheDocument();
    expect(screen.getByText("Model 2")).toBeInTheDocument();
    expect(screen.getByText("Model 3")).toBeInTheDocument();
    expect(screen.getByText("3 models available")).toBeInTheDocument();
  });

  it("shows singular 'model' for count of 1", async () => {
    mockGetModels.mockResolvedValue(makeModelsResponse(1));
    render(wrap(createElement(ModelsTab)));
    await waitFor(() => {
      expect(screen.getByText("1 model available")).toBeInTheDocument();
    });
  });

  it("shows Operational badge when executor is operational", async () => {
    mockGetModels.mockResolvedValue(makeModelsResponse(1, true));
    render(wrap(createElement(ModelsTab)));
    await waitFor(() => {
      expect(screen.getByText("Operational")).toBeInTheDocument();
    });
    expect(screen.getByTestId("badge-ok")).toBeInTheDocument();
  });

  it("shows Degraded badge when executor is not operational", async () => {
    mockGetModels.mockResolvedValue(makeModelsResponse(1, false));
    render(wrap(createElement(ModelsTab)));
    await waitFor(() => {
      expect(screen.getByText("Degraded")).toBeInTheDocument();
    });
    expect(screen.getByTestId("badge-err")).toBeInTheDocument();
  });

  it("renders section title", async () => {
    mockGetModels.mockResolvedValue(makeModelsResponse(1));
    render(wrap(createElement(ModelsTab)));
    await waitFor(() => {
      expect(screen.getByText("Available Models")).toBeInTheDocument();
    });
  });
});
