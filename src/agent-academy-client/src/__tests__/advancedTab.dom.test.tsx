// @vitest-environment jsdom
/**
 * DOM tests for AdvancedTab.
 *
 * Covers: initial render, epoch management fields, sprint auto-start,
 * sprint schedule loading/saving/deleting, cron validation hint,
 * desktop notifications section, settings save flow.
 */
import "@testing-library/jest-dom/vitest";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { createElement } from "react";

vi.mock("../api", () => ({
  getSystemSettings: vi.fn(),
  updateSystemSettings: vi.fn(),
  getSprintSchedule: vi.fn(),
  upsertSprintSchedule: vi.fn(),
  deleteSprintSchedule: vi.fn(),
}));

vi.mock("@fluentui/react-components", () => ({
  Button: ({ children, onClick, disabled, appearance, size, ...rest }: any) => (
    <button onClick={onClick} disabled={disabled} data-appearance={appearance} data-size={size} {...rest}>
      {children}
    </button>
  ),
  Spinner: ({ label }: any) => <span data-testid="spinner">{label ?? "Loading..."}</span>,
  makeStyles: () => () => ({}),
  shorthands: new Proxy({}, { get: () => () => ({}) }),
}));

vi.mock("../settings/settingsStyles", () => ({
  useSettingsStyles: () => ({
    sectionTitle: "section-title",
    fieldLabel: "field-label",
    inputField: "input-field",
  }),
}));

import AdvancedTab from "../settings/AdvancedTab";
import {
  getSystemSettings,
  updateSystemSettings,
  getSprintSchedule,
  upsertSprintSchedule,
  deleteSprintSchedule,
} from "../api";

const mockGetSettings = vi.mocked(getSystemSettings);
const mockUpdateSettings = vi.mocked(updateSystemSettings);
const mockGetSchedule = vi.mocked(getSprintSchedule);
const mockUpsertSchedule = vi.mocked(upsertSprintSchedule);
const mockDeleteSchedule = vi.mocked(deleteSprintSchedule);

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

function renderTab(props: { desktopNotifications?: any } = {}) {
  mockGetSettings.mockResolvedValue({});
  mockGetSchedule.mockResolvedValue(null);
  return render(createElement(AdvancedTab, props));
}

describe("AdvancedTab", () => {
  describe("initial render", () => {
    it("shows section headings", async () => {
      renderTab();
      expect(screen.getByText("Advanced Settings")).toBeInTheDocument();
      expect(screen.getByText("Conversation Epoch Management")).toBeInTheDocument();
      expect(screen.getByText("Sprint Automation")).toBeInTheDocument();
      expect(screen.getByText("Sprint Schedule")).toBeInTheDocument();
      expect(screen.getByText("Desktop Notifications")).toBeInTheDocument();
    });

    it("shows loading spinner while fetching schedule", () => {
      mockGetSettings.mockResolvedValue({});
      mockGetSchedule.mockReturnValue(new Promise(() => {})); // never resolves
      render(createElement(AdvancedTab, {}));
      expect(screen.getByText(/loading schedule/i)).toBeInTheDocument();
    });
  });

  describe("epoch management", () => {
    it("renders main room and breakout room inputs with defaults", async () => {
      renderTab();
      await waitFor(() => {
        const inputs = screen.getAllByRole("spinbutton");
        expect(inputs.length).toBeGreaterThanOrEqual(2);
      });
    });

    it("populates from loaded settings", async () => {
      mockGetSettings.mockResolvedValue({
        "conversation.mainRoomEpochSize": "75",
        "conversation.breakoutEpochSize": "40",
      });
      mockGetSchedule.mockResolvedValue(null);
      render(createElement(AdvancedTab, {}));

      await waitFor(() => {
        const inputs = screen.getAllByRole("spinbutton");
        expect(inputs[0]).toHaveValue(75);
        expect(inputs[1]).toHaveValue(40);
      });
    });
  });

  describe("save settings", () => {
    it("calls updateSystemSettings on save click", async () => {
      mockUpdateSettings.mockResolvedValueOnce(undefined as any);
      renderTab();

      await waitFor(() => {
        expect(screen.getByText("Save")).toBeInTheDocument();
      });
      await userEvent.click(screen.getByText("Save"));

      await waitFor(() => {
        expect(mockUpdateSettings).toHaveBeenCalledWith(
          expect.objectContaining({
            "conversation.mainRoomEpochSize": "50",
            "conversation.breakoutEpochSize": "30",
          })
        );
      });
    });

    it("shows saved confirmation after successful save", async () => {
      mockUpdateSettings.mockResolvedValueOnce(undefined as any);
      renderTab();

      await waitFor(() => screen.getByText("Save"));
      await userEvent.click(screen.getByText("Save"));

      await waitFor(() => {
        expect(screen.getByText("✓ Saved")).toBeInTheDocument();
      });
    });
  });

  describe("sprint schedule", () => {
    it("shows create button when no schedule exists", async () => {
      renderTab();
      await waitFor(() => {
        expect(screen.getByText("Create Schedule")).toBeInTheDocument();
      });
    });

    it("shows update and delete buttons when schedule exists", async () => {
      mockGetSettings.mockResolvedValue({});
      mockGetSchedule.mockResolvedValue({
        id: "s1",
        workspacePath: "/test",
        cronExpression: "0 9 * * MON-FRI",
        timeZoneId: "UTC",
        enabled: true,
        nextRunAtUtc: "2026-04-17T09:00:00Z",
        lastTriggeredAt: null,
        lastEvaluatedAt: null,
        lastOutcome: null,
        createdAt: "2026-04-15T00:00:00Z",
        updatedAt: "2026-04-15T00:00:00Z",
      });
      render(createElement(AdvancedTab, {}));

      await waitFor(() => {
        expect(screen.getByText("Update Schedule")).toBeInTheDocument();
        expect(screen.getByText("Delete Schedule")).toBeInTheDocument();
      });
    });

    it("calls upsertSprintSchedule on save", async () => {
      mockUpsertSchedule.mockResolvedValue({
        id: "s1",
        cronExpression: "0 9 * * MON-FRI",
        timeZoneId: "UTC",
        enabled: true,
      } as any);
      renderTab();

      await waitFor(() => screen.getByText("Create Schedule"));
      const cronInput = screen.getByPlaceholderText("0 9 * * MON-FRI");
      await userEvent.type(cronInput, "0 9 * * MON-FRI");
      await userEvent.click(screen.getByText("Create Schedule"));

      await waitFor(() => {
        expect(mockUpsertSchedule).toHaveBeenCalledWith(
          expect.objectContaining({
            cronExpression: "0 9 * * MON-FRI",
          })
        );
      });
    });

    it("shows cron validation hint for wrong field count", async () => {
      renderTab();
      await waitFor(() => screen.getByPlaceholderText("0 9 * * MON-FRI"));

      const cronInput = screen.getByPlaceholderText("0 9 * * MON-FRI");
      await userEvent.type(cronInput, "0 9 *");

      expect(screen.getByText(/cron requires 5 fields/i)).toBeInTheDocument();
    });

    it("disables Create button when cron expression is empty", async () => {
      renderTab();
      await waitFor(() => {
        const createBtn = screen.getByText("Create Schedule");
        expect(createBtn).toBeDisabled();
      });
    });

    it("shows delete confirmation on first click then deletes on second", async () => {
      mockGetSettings.mockResolvedValue({});
      mockGetSchedule.mockResolvedValue({
        id: "s1",
        workspacePath: "/test",
        cronExpression: "0 9 * * MON-FRI",
        timeZoneId: "UTC",
        enabled: true,
        nextRunAtUtc: null,
        lastTriggeredAt: null,
        lastEvaluatedAt: null,
        lastOutcome: null,
        createdAt: "2026-04-15T00:00:00Z",
        updatedAt: "2026-04-15T00:00:00Z",
      });
      mockDeleteSchedule.mockResolvedValueOnce();
      render(createElement(AdvancedTab, {}));

      await waitFor(() => screen.getByText("Delete Schedule"));
      await userEvent.click(screen.getByText("Delete Schedule"));
      expect(screen.getByText("Confirm Delete?")).toBeInTheDocument();

      await userEvent.click(screen.getByText("Confirm Delete?"));
      await waitFor(() => {
        expect(mockDeleteSchedule).toHaveBeenCalled();
      });
    });

    it("shows schedule error on save failure", async () => {
      mockUpsertSchedule.mockRejectedValueOnce(new Error("Invalid cron"));
      renderTab();

      await waitFor(() => screen.getByPlaceholderText("0 9 * * MON-FRI"));
      await userEvent.type(screen.getByPlaceholderText("0 9 * * MON-FRI"), "0 9 * * MON-FRI");
      await userEvent.click(screen.getByText("Create Schedule"));

      await waitFor(() => {
        expect(screen.getByText("Invalid cron")).toBeInTheDocument();
      });
    });
  });

  describe("sprint auto-start", () => {
    it("renders auto-start checkbox", async () => {
      renderTab();
      await waitFor(() => {
        expect(screen.getByText(/auto-start next sprint/i)).toBeInTheDocument();
      });
    });

    it("reflects loaded auto-start setting", async () => {
      mockGetSettings.mockResolvedValue({
        "sprint.autoStartOnCompletion": "true",
      });
      mockGetSchedule.mockResolvedValue(null);
      render(createElement(AdvancedTab, {}));

      await waitFor(() => {
        const checkbox = screen.getByRole("checkbox", { name: /auto-start/i });
        expect(checkbox).toBeChecked();
      });
    });
  });

  describe("desktop notifications", () => {
    it("shows 'Not available' when no desktopNotifications prop", async () => {
      renderTab();
      expect(screen.getByText("Not available")).toBeInTheDocument();
    });

    it("shows desktop notification toggle when prop is provided", async () => {
      renderTab({
        desktopNotifications: {
          enabled: false,
          setEnabled: vi.fn(),
          permission: "default",
        },
      });
      expect(screen.getByText(/enable desktop notifications/i)).toBeInTheDocument();
    });

    it("shows blocked message when permission is denied", async () => {
      renderTab({
        desktopNotifications: {
          enabled: false,
          setEnabled: vi.fn(),
          permission: "denied",
        },
      });
      expect(screen.getByText(/blocked by browser/i)).toBeInTheDocument();
    });

    it("shows unsupported message when permission is unsupported", async () => {
      renderTab({
        desktopNotifications: {
          enabled: false,
          setEnabled: vi.fn(),
          permission: "unsupported",
        },
      });
      expect(screen.getByText(/not supported/i)).toBeInTheDocument();
    });
  });
});
