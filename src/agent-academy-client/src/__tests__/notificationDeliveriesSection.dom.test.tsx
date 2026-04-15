// @vitest-environment jsdom
/**
 * DOM tests for NotificationDeliveriesSection.
 *
 * Covers: loading, error, empty state, delivery list with status badges,
 * stats summary, refresh button.
 */
import "@testing-library/jest-dom/vitest";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createElement } from "react";
import { FluentProvider, webDarkTheme } from "@fluentui/react-components";

// ── Mocks ──────────────────────────────────────────────────────────────

vi.mock("../api", () => ({
  getNotificationDeliveries: vi.fn(),
  getNotificationDeliveryStats: vi.fn(),
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

vi.mock("../panelUtils", () => ({
  formatTimestamp: (iso: string) => iso.slice(0, 10),
}));

vi.mock("../settings/settingsStyles", () => ({
  useSettingsStyles: () => ({
    sectionTitle: "mock-section-title",
    emptyState: "mock-empty-state",
    errorText: "mock-error-text",
  }),
}));

import NotificationDeliveriesSection from "../settings/NotificationDeliveriesSection";
import type {
  NotificationDeliveryDto,
  NotificationDeliveryStats,
} from "../api";
import {
  getNotificationDeliveries,
  getNotificationDeliveryStats,
} from "../api";

const mockGetDeliveries = vi.mocked(getNotificationDeliveries);
const mockGetStats = vi.mocked(getNotificationDeliveryStats);

// ── Helpers ────────────────────────────────────────────────────────────

function wrap(ui: React.ReactNode) {
  return createElement(FluentProvider, { theme: webDarkTheme }, ui);
}

function makeDelivery(
  overrides: Partial<NotificationDeliveryDto> = {},
): NotificationDeliveryDto {
  return {
    id: 1,
    channel: "discord",
    title: "Build succeeded",
    body: null,
    roomId: null,
    agentId: null,
    providerId: "discord-main",
    status: "Delivered",
    error: null,
    attemptedAt: "2026-04-10T12:00:00Z",
    ...overrides,
  };
}

function makeStats(
  data: Record<string, number> = {},
): NotificationDeliveryStats {
  return data;
}

// ── Tests ──────────────────────────────────────────────────────────────

describe("NotificationDeliveriesSection", () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  afterEach(cleanup);

  it("shows loading spinner while fetching", () => {
    mockGetDeliveries.mockReturnValue(new Promise(() => {}));
    mockGetStats.mockReturnValue(new Promise(() => {}));
    render(wrap(createElement(NotificationDeliveriesSection)));
    expect(screen.getByText("Loading deliveries…")).toBeInTheDocument();
  });

  it("shows error message on fetch failure", async () => {
    mockGetDeliveries.mockRejectedValue(new Error("Server down"));
    mockGetStats.mockRejectedValue(new Error("Server down"));
    render(wrap(createElement(NotificationDeliveriesSection)));
    await waitFor(() => {
      expect(screen.getByText("Server down")).toBeInTheDocument();
    });
  });

  it("shows generic error for non-Error rejection", async () => {
    mockGetDeliveries.mockRejectedValue("boom");
    mockGetStats.mockRejectedValue("boom");
    render(wrap(createElement(NotificationDeliveriesSection)));
    await waitFor(() => {
      expect(
        screen.getByText("Failed to load deliveries"),
      ).toBeInTheDocument();
    });
  });

  it("shows empty state when no deliveries exist", async () => {
    mockGetDeliveries.mockResolvedValue([]);
    mockGetStats.mockResolvedValue({});
    render(wrap(createElement(NotificationDeliveriesSection)));
    await waitFor(() => {
      expect(screen.getByText("No deliveries yet")).toBeInTheDocument();
    });
  });

  it("renders delivery list with status badges and content", async () => {
    mockGetDeliveries.mockResolvedValue([
      makeDelivery({ id: 1, title: "Build passed", status: "Delivered" }),
      makeDelivery({
        id: 2,
        title: "Deploy failed",
        status: "Failed",
        channel: "slack",
      }),
    ]);
    mockGetStats.mockResolvedValue({});
    render(wrap(createElement(NotificationDeliveriesSection)));
    await waitFor(() => {
      expect(screen.getByText("Build passed")).toBeInTheDocument();
    });
    expect(screen.getByText("Deploy failed")).toBeInTheDocument();
    expect(screen.getByText("discord")).toBeInTheDocument();
    expect(screen.getByText("slack")).toBeInTheDocument();
  });

  it("renders stats summary badges", async () => {
    mockGetDeliveries.mockResolvedValue([]);
    mockGetStats.mockResolvedValue(
      makeStats({ Delivered: 10, Failed: 2, Pending: 1 }),
    );
    render(wrap(createElement(NotificationDeliveriesSection)));
    await waitFor(() => {
      expect(screen.getByText("Delivered: 10")).toBeInTheDocument();
    });
    expect(screen.getByText("Failed: 2")).toBeInTheDocument();
    expect(screen.getByText("Pending: 1")).toBeInTheDocument();
  });

  it("renders Delivery History heading", async () => {
    mockGetDeliveries.mockResolvedValue([]);
    mockGetStats.mockResolvedValue({});
    render(wrap(createElement(NotificationDeliveriesSection)));
    await waitFor(() => {
      expect(screen.getByText("Delivery History")).toBeInTheDocument();
    });
  });

  it("refresh button re-fetches data", async () => {
    mockGetDeliveries.mockResolvedValue([]);
    mockGetStats.mockResolvedValue({});
    render(wrap(createElement(NotificationDeliveriesSection)));
    await waitFor(() => {
      expect(screen.getByText("No deliveries yet")).toBeInTheDocument();
    });
    expect(mockGetDeliveries).toHaveBeenCalledTimes(1);

    mockGetDeliveries.mockResolvedValue([
      makeDelivery({ title: "New notification after refresh" }),
    ]);
    const user = userEvent.setup();
    await user.click(screen.getByText("Refresh"));

    await waitFor(() => {
      expect(mockGetDeliveries).toHaveBeenCalledTimes(2);
    });
    await waitFor(() => {
      expect(screen.getByText("New notification after refresh")).toBeInTheDocument();
    });
  });

  it("shows body as fallback when title is null", async () => {
    mockGetDeliveries.mockResolvedValue([
      makeDelivery({ title: null, body: "Fallback body text" }),
    ]);
    mockGetStats.mockResolvedValue({});
    render(wrap(createElement(NotificationDeliveriesSection)));
    await waitFor(() => {
      expect(screen.getByText("Fallback body text")).toBeInTheDocument();
    });
  });

  it("shows dash when both title and body are null", async () => {
    mockGetDeliveries.mockResolvedValue([
      makeDelivery({ title: null, body: null }),
    ]);
    mockGetStats.mockResolvedValue({});
    render(wrap(createElement(NotificationDeliveriesSection)));
    await waitFor(() => {
      expect(screen.getByText("—")).toBeInTheDocument();
    });
  });
});
