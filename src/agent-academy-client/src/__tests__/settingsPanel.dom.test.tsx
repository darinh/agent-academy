// @vitest-environment jsdom
/**
 * Interactive RTL tests for SettingsPanel.
 *
 * Uses @testing-library/react + jsdom.
 * Covers: tab navigation, close (button + Escape), Custom Agents tab (create form
 * validation, ID preview, submit, error, delete, empty state), Built-in Agents tab
 * (loading, empty, agent cards), Templates tab (loading, template cards, create),
 * Notifications tab (loading, empty, provider cards, badges, disconnect, setup wizard),
 * GitHub tab (loading, error/retry, oauth/cli/none states, capabilities grid, refresh),
 * Advanced tab (epoch inputs, save, saved confirmation).
 */
import "@testing-library/jest-dom/vitest";
import {
  cleanup,
  render,
  screen,
  waitFor,
  fireEvent,
} from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createElement } from "react";
import { FluentProvider, webDarkTheme } from "@fluentui/react-components";

// ── Mocks ──────────────────────────────────────────────────────────────

vi.mock("../api", () => ({
  getNotificationProviders: vi.fn(),
  disconnectProvider: vi.fn(),
  getConfiguredAgents: vi.fn(),
  getInstructionTemplates: vi.fn(),
  getSystemSettings: vi.fn(),
  updateSystemSettings: vi.fn(),
  createCustomAgent: vi.fn(),
  deleteCustomAgent: vi.fn(),
  getGitHubStatus: vi.fn(),
}));

vi.mock("../NotificationSetupWizard", () => ({
  default: ({ providerId, onClose }: { providerId: string; onClose?: () => void }) =>
    createElement(
      "div",
      { "data-testid": `setup-wizard-${providerId}` },
      createElement("button", { onClick: onClose }, "Complete Setup"),
    ),
}));

vi.mock("../AgentConfigCard", () => ({
  default: ({
    agent,
    expanded,
    onToggle,
  }: {
    agent: { id: string; name: string };
    expanded: boolean;
    onToggle: () => void;
  }) =>
    createElement(
      "div",
      { "data-testid": `agent-card-${agent.id}`, onClick: onToggle },
      `${agent.name}${expanded ? " (expanded)" : ""}`,
    ),
}));

vi.mock("../TemplateCard", () => ({
  default: ({
    template,
    isNew,
    expanded,
    onToggle,
    onCancelNew: _onCancelNew,
  }: {
    template?: { id: string; name: string };
    isNew?: boolean;
    expanded: boolean;
    onToggle: () => void;
    onCancelNew?: () => void;
  }) =>
    createElement(
      "div",
      {
        "data-testid": isNew ? "template-card-new" : `template-card-${template?.id}`,
        onClick: onToggle,
      },
      isNew ? "New Template" : `${template?.name}${expanded ? " (expanded)" : ""}`,
    ),
}));

vi.mock("../V3Badge", () => ({
  default: ({ children, color }: { children: string; color: string }) =>
    createElement("span", { "data-testid": `badge-${color}` }, children),
}));

import SettingsPanel from "../SettingsPanel";
import {
  getNotificationProviders,
  disconnectProvider,
  getConfiguredAgents,
  getInstructionTemplates,
  getSystemSettings,
  updateSystemSettings,
  createCustomAgent,
  deleteCustomAgent,
  getGitHubStatus,
} from "../api";
import type {
  ProviderStatus,
  AgentDefinition,
  InstructionTemplate,
  GitHubStatus,
} from "../api";

const mockGetProviders = vi.mocked(getNotificationProviders);
const mockDisconnect = vi.mocked(disconnectProvider);
const mockGetAgents = vi.mocked(getConfiguredAgents);
const mockGetTemplates = vi.mocked(getInstructionTemplates);
const mockGetSettings = vi.mocked(getSystemSettings);
const mockUpdateSettings = vi.mocked(updateSystemSettings);
const mockCreateAgent = vi.mocked(createCustomAgent);
const mockDeleteAgent = vi.mocked(deleteCustomAgent);
const mockGetGitHubStatus = vi.mocked(getGitHubStatus);

// ── Factories ──────────────────────────────────────────────────────────

function makeProvider(overrides: Partial<ProviderStatus> = {}): ProviderStatus {
  return {
    providerId: "discord",
    displayName: "Discord",
    isConfigured: true,
    isConnected: true,
    lastError: null,
    ...overrides,
  };
}

function makeAgent(overrides: Partial<AgentDefinition> = {}): AgentDefinition {
  return {
    id: "architect",
    name: "Architect",
    role: "architect",
    summary: "System architect agent",
    startupPrompt: "You are an architect.",
    model: "claude-opus-4.6",
    capabilityTags: ["design"],
    enabledTools: ["search-code"],
    autoJoinDefaultRoom: true,
    gitIdentity: null,
    ...overrides,
  };
}

function makeCustomAgent(overrides: Partial<AgentDefinition> = {}): AgentDefinition {
  return makeAgent({
    id: "my-bot",
    name: "My Bot",
    role: "Custom",
    ...overrides,
  });
}

function makeTemplate(overrides: Partial<InstructionTemplate> = {}): InstructionTemplate {
  return {
    id: "tpl-1",
    name: "Default Template",
    content: "You are a helpful agent.",
    createdAt: "2026-01-01T00:00:00Z",
    updatedAt: "2026-01-01T00:00:00Z",
    ...overrides,
  };
}

function makeGitHubStatus(overrides: Partial<GitHubStatus> = {}): GitHubStatus {
  return {
    isConfigured: true,
    repository: "owner/repo",
    authSource: "oauth",
    ...overrides,
  };
}

// ── Helpers ─────────────────────────────────────────────────────────────

/** Default mock setup: all APIs resolve with empty/minimal data */
function setupDefaultMocks() {
  mockGetProviders.mockResolvedValue([]);
  mockGetAgents.mockResolvedValue([]);
  mockGetTemplates.mockResolvedValue([]);
  mockGetSettings.mockResolvedValue({});
  mockGetGitHubStatus.mockResolvedValue(makeGitHubStatus());
}

import type { DesktopNotificationControls } from "../useDesktopNotifications";

function renderPanel(onClose = vi.fn(), desktopNotifications?: DesktopNotificationControls) {
  const result = render(
    createElement(
      FluentProvider,
      { theme: webDarkTheme },
      createElement(SettingsPanel, { onClose, desktopNotifications }),
    ),
  );
  return { ...result, onClose };
}

async function renderPanelAndWait(onClose = vi.fn(), desktopNotifications?: DesktopNotificationControls) {
  const result = renderPanel(onClose, desktopNotifications);
  // Wait for initial data loads to settle
  await waitFor(() => {
    expect(mockGetAgents).toHaveBeenCalled();
  });
  return result;
}

function clickTab(name: string) {
  const buttons = screen.getAllByRole("button");
  const tab = buttons.find((b) => b.textContent?.includes(name));
  if (!tab) throw new Error(`Tab "${name}" not found`);
  fireEvent.click(tab);
}

// ── Tests ───────────────────────────────────────────────────────────────

describe("SettingsPanel (interactive)", () => {
  beforeEach(() => {
    vi.resetAllMocks();
    setupDefaultMocks();
  });

  afterEach(() => {
    cleanup();
  });

  // ── Header & Close ──────────────────────────────────────────────────

  describe("header and close", () => {
    it("renders the Settings title", async () => {
      await renderPanelAndWait();
      expect(screen.getByText("Settings")).toBeInTheDocument();
    });

    it("renders the configuration subtitle", async () => {
      await renderPanelAndWait();
      expect(screen.getByText("// configuration")).toBeInTheDocument();
    });

    it("calls onClose when close button is clicked", async () => {
      const { onClose } = await renderPanelAndWait();
      const closeBtn = screen.getByRole("button", { name: "Close settings" });
      await userEvent.click(closeBtn);
      expect(onClose).toHaveBeenCalledTimes(1);
    });

    it("calls onClose when Escape is pressed", async () => {
      const { onClose } = await renderPanelAndWait();
      fireEvent.keyDown(window, { key: "Escape" });
      expect(onClose).toHaveBeenCalledTimes(1);
    });
  });

  // ── Tab Navigation ──────────────────────────────────────────────────

  describe("tab navigation", () => {
    it("starts on Custom Agents tab", async () => {
      await renderPanelAndWait();
      expect(screen.getByText("Custom Agents", { selector: "div" })).toBeInTheDocument();
    });

    it("switches to Built-in Agents tab", async () => {
      await renderPanelAndWait();
      clickTab("Built-in Agents");
      expect(screen.getByText("Built-in Agents", { selector: "div" })).toBeInTheDocument();
    });

    it("switches to Templates tab", async () => {
      await renderPanelAndWait();
      clickTab("Templates");
      expect(screen.getByText("Instruction Templates")).toBeInTheDocument();
    });

    it("switches to Notifications tab", async () => {
      await renderPanelAndWait();
      clickTab("Notifications");
      expect(screen.getByText("Notification Providers")).toBeInTheDocument();
    });

    it("switches to GitHub tab", async () => {
      await renderPanelAndWait();
      clickTab("GitHub");
      expect(screen.getByText("GitHub Integration")).toBeInTheDocument();
    });

    it("switches to Advanced tab", async () => {
      await renderPanelAndWait();
      clickTab("Advanced");
      expect(screen.getByText("Advanced Settings")).toBeInTheDocument();
    });

    it("supports switching between tabs freely", async () => {
      await renderPanelAndWait();
      clickTab("Advanced");
      expect(screen.getByText("Advanced Settings")).toBeInTheDocument();
      clickTab("GitHub");
      expect(screen.getByText("GitHub Integration")).toBeInTheDocument();
      clickTab("Custom Agents");
      expect(screen.getByText("Custom Agents", { selector: "div" })).toBeInTheDocument();
    });
  });

  // ── Custom Agents Tab ───────────────────────────────────────────────

  describe("custom agents tab", () => {
    it("shows empty state when no custom agents exist", async () => {
      await renderPanelAndWait();
      expect(screen.getByText(/No custom agents yet/)).toBeInTheDocument();
    });

    it("shows Add Custom Agent button", async () => {
      await renderPanelAndWait();
      expect(screen.getByText("Add Custom Agent")).toBeInTheDocument();
    });

    it("opens create form when Add Custom Agent is clicked", async () => {
      await renderPanelAndWait();
      await userEvent.click(screen.getByText("Add Custom Agent"));
      expect(screen.getByText("Agent Name")).toBeInTheDocument();
      expect(screen.getByText("Agent Prompt (agent.md)")).toBeInTheDocument();
      expect(screen.getByText("Model (optional)")).toBeInTheDocument();
    });

    it("shows ID preview when typing agent name", async () => {
      await renderPanelAndWait();
      await userEvent.click(screen.getByText("Add Custom Agent"));
      const nameInput = screen.getByPlaceholderText("e.g. Purview Expert");
      await userEvent.type(nameInput, "My Cool Agent");
      expect(screen.getByText("ID: my-cool-agent")).toBeInTheDocument();
    });

    it("disables Create button when name is empty", async () => {
      await renderPanelAndWait();
      await userEvent.click(screen.getByText("Add Custom Agent"));
      const createBtn = screen.getByText("Create Agent");
      expect(createBtn.closest("button")).toBeDisabled();
    });

    it("disables Create button when prompt is empty", async () => {
      await renderPanelAndWait();
      await userEvent.click(screen.getByText("Add Custom Agent"));
      const nameInput = screen.getByPlaceholderText("e.g. Purview Expert");
      await userEvent.type(nameInput, "Test Agent");
      const createBtn = screen.getByText("Create Agent");
      expect(createBtn.closest("button")).toBeDisabled();
    });

    it("enables Create button when both name and prompt are filled", async () => {
      await renderPanelAndWait();
      await userEvent.click(screen.getByText("Add Custom Agent"));
      const nameInput = screen.getByPlaceholderText("e.g. Purview Expert");
      const promptInput = screen.getByPlaceholderText(/You are a specialist/);
      await userEvent.type(nameInput, "Test Agent");
      await userEvent.type(promptInput, "You are helpful.");
      const createBtn = screen.getByText("Create Agent");
      expect(createBtn.closest("button")).not.toBeDisabled();
    });

    it("calls createCustomAgent on submit with correct data", async () => {
      mockCreateAgent.mockResolvedValue(makeCustomAgent({ id: "test-agent", name: "Test Agent" }));
      // After creation, agents list refreshes
      mockGetAgents.mockResolvedValue([makeCustomAgent({ id: "test-agent", name: "Test Agent" })]);
      mockGetTemplates.mockResolvedValue([]);

      await renderPanelAndWait();
      await userEvent.click(screen.getByText("Add Custom Agent"));
      await userEvent.type(screen.getByPlaceholderText("e.g. Purview Expert"), "Test Agent");
      await userEvent.type(screen.getByPlaceholderText(/You are a specialist/), "Be helpful.");
      await userEvent.type(
        screen.getByPlaceholderText(/leave empty for default/),
        "claude-sonnet-4.5",
      );
      await userEvent.click(screen.getByText("Create Agent"));

      await waitFor(() => {
        expect(mockCreateAgent).toHaveBeenCalledWith({
          name: "Test Agent",
          prompt: "Be helpful.",
          model: "claude-sonnet-4.5",
        });
      });
    });

    it("clears form and hides it after successful creation", async () => {
      mockCreateAgent.mockResolvedValue(makeCustomAgent());
      mockGetAgents.mockResolvedValue([makeCustomAgent()]);
      mockGetTemplates.mockResolvedValue([]);

      await renderPanelAndWait();
      await userEvent.click(screen.getByText("Add Custom Agent"));
      await userEvent.type(screen.getByPlaceholderText("e.g. Purview Expert"), "Bot");
      await userEvent.type(screen.getByPlaceholderText(/You are a specialist/), "Hello.");
      await userEvent.click(screen.getByText("Create Agent"));

      await waitFor(() => {
        expect(screen.queryByText("Agent Name")).not.toBeInTheDocument();
      });
    });

    it("shows error message when creation fails", async () => {
      mockCreateAgent.mockRejectedValue(new Error("Duplicate agent ID"));

      await renderPanelAndWait();
      await userEvent.click(screen.getByText("Add Custom Agent"));
      await userEvent.type(screen.getByPlaceholderText("e.g. Purview Expert"), "Bot");
      await userEvent.type(screen.getByPlaceholderText(/You are a specialist/), "Hello.");
      await userEvent.click(screen.getByText("Create Agent"));

      await waitFor(() => {
        expect(screen.getByText("Duplicate agent ID")).toBeInTheDocument();
      });
    });

    it("hides create form when Cancel is clicked", async () => {
      await renderPanelAndWait();
      await userEvent.click(screen.getByText("Add Custom Agent"));
      expect(screen.getByText("Agent Name")).toBeInTheDocument();
      await userEvent.click(screen.getByText("Cancel"));
      expect(screen.queryByText("Agent Name")).not.toBeInTheDocument();
    });

    it("renders custom agent cards with delete button", async () => {
      mockGetAgents.mockResolvedValue([
        makeCustomAgent({ id: "bot-1", name: "Bot One" }),
        makeCustomAgent({ id: "bot-2", name: "Bot Two" }),
      ]);

      await renderPanelAndWait();
      expect(await screen.findByText("Bot One")).toBeInTheDocument();
      expect(screen.getByText("Bot Two")).toBeInTheDocument();
      expect(screen.getByRole("button", { name: "Delete Bot One" })).toBeInTheDocument();
      expect(screen.getByRole("button", { name: "Delete Bot Two" })).toBeInTheDocument();
    });

    it("calls deleteCustomAgent when delete button is clicked", async () => {
      mockGetAgents.mockResolvedValue([makeCustomAgent({ id: "bot-1", name: "Bot One" })]);
      mockDeleteAgent.mockResolvedValue({ status: "deleted", agentId: "bot-1" });
      // After deletion, refresh returns empty
      mockGetAgents.mockResolvedValueOnce([makeCustomAgent({ id: "bot-1", name: "Bot One" })]);

      await renderPanelAndWait();
      const deleteBtn = await screen.findByRole("button", { name: "Delete Bot One" });
      await userEvent.click(deleteBtn);
      expect(mockDeleteAgent).toHaveBeenCalledWith("bot-1");
    });

    it("hides empty state when custom agents exist", async () => {
      mockGetAgents.mockResolvedValue([makeCustomAgent()]);
      await renderPanelAndWait();
      expect(screen.queryByText(/No custom agents yet/)).not.toBeInTheDocument();
    });

    it("omits model when left empty", async () => {
      mockCreateAgent.mockResolvedValue(makeCustomAgent());
      mockGetAgents.mockResolvedValue([]);
      mockGetTemplates.mockResolvedValue([]);

      await renderPanelAndWait();
      await userEvent.click(screen.getByText("Add Custom Agent"));
      await userEvent.type(screen.getByPlaceholderText("e.g. Purview Expert"), "Agent");
      await userEvent.type(screen.getByPlaceholderText(/You are a specialist/), "Prompt.");
      // Don't type in model field
      await userEvent.click(screen.getByText("Create Agent"));

      await waitFor(() => {
        expect(mockCreateAgent).toHaveBeenCalledWith({
          name: "Agent",
          prompt: "Prompt.",
          model: undefined,
        });
      });
    });
  });

  // ── Built-in Agents Tab ─────────────────────────────────────────────

  describe("built-in agents tab", () => {
    it("shows loading spinner while agents load", async () => {
      // Never resolve agents so loading stays true
      mockGetAgents.mockReturnValue(new Promise(() => {}));
      mockGetTemplates.mockReturnValue(new Promise(() => {}));
      renderPanel();
      clickTab("Built-in Agents");
      expect(screen.getByText("Loading agents…")).toBeInTheDocument();
    });

    it("shows empty state when no built-in agents", async () => {
      await renderPanelAndWait();
      clickTab("Built-in Agents");
      expect(screen.getByText("No agents configured.")).toBeInTheDocument();
    });

    it("renders agent config cards for built-in agents", async () => {
      mockGetAgents.mockResolvedValue([
        makeAgent({ id: "architect", name: "Architect" }),
        makeAgent({ id: "engineer", name: "Engineer", role: "engineer" }),
      ]);
      await renderPanelAndWait();
      clickTab("Built-in Agents");
      expect(screen.getByTestId("agent-card-architect")).toBeInTheDocument();
      expect(screen.getByTestId("agent-card-engineer")).toBeInTheDocument();
    });

    it("does not show custom agents in built-in tab", async () => {
      mockGetAgents.mockResolvedValue([
        makeAgent({ id: "architect", name: "Architect" }),
        makeCustomAgent({ id: "my-bot", name: "My Bot" }),
      ]);
      await renderPanelAndWait();
      clickTab("Built-in Agents");
      expect(screen.getByTestId("agent-card-architect")).toBeInTheDocument();
      expect(screen.queryByTestId("agent-card-my-bot")).not.toBeInTheDocument();
    });

    it("toggles agent card expansion on click", async () => {
      mockGetAgents.mockResolvedValue([makeAgent({ id: "architect", name: "Architect" })]);
      await renderPanelAndWait();
      clickTab("Built-in Agents");

      const card = screen.getByTestId("agent-card-architect");
      expect(card).toHaveTextContent("Architect");
      expect(card).not.toHaveTextContent("(expanded)");

      fireEvent.click(card);
      expect(card).toHaveTextContent("(expanded)");

      fireEvent.click(card);
      expect(card).not.toHaveTextContent("(expanded)");
    });
  });

  // ── Templates Tab ───────────────────────────────────────────────────

  describe("templates tab", () => {
    it("shows loading spinner while templates load", async () => {
      mockGetAgents.mockReturnValue(new Promise(() => {}));
      mockGetTemplates.mockReturnValue(new Promise(() => {}));
      renderPanel();
      clickTab("Templates");
      expect(screen.getByText("Loading templates…")).toBeInTheDocument();
    });

    it("renders template cards", async () => {
      mockGetTemplates.mockResolvedValue([
        makeTemplate({ id: "tpl-1", name: "Template One" }),
        makeTemplate({ id: "tpl-2", name: "Template Two" }),
      ]);
      await renderPanelAndWait();
      clickTab("Templates");
      expect(screen.getByTestId("template-card-tpl-1")).toBeInTheDocument();
      expect(screen.getByTestId("template-card-tpl-2")).toBeInTheDocument();
    });

    it("shows Create Template button", async () => {
      await renderPanelAndWait();
      clickTab("Templates");
      expect(screen.getByText("Create Template")).toBeInTheDocument();
    });

    it("shows new template card when Create Template is clicked", async () => {
      await renderPanelAndWait();
      clickTab("Templates");
      await userEvent.click(screen.getByText("Create Template"));
      expect(screen.getByTestId("template-card-new")).toBeInTheDocument();
    });

    it("toggles template card expansion", async () => {
      mockGetTemplates.mockResolvedValue([makeTemplate({ id: "tpl-1", name: "Template One" })]);
      await renderPanelAndWait();
      clickTab("Templates");

      const card = screen.getByTestId("template-card-tpl-1");
      expect(card).not.toHaveTextContent("(expanded)");

      fireEvent.click(card);
      expect(card).toHaveTextContent("(expanded)");
    });
  });

  // ── Notifications Tab ───────────────────────────────────────────────

  describe("notifications tab", () => {
    it("shows loading spinner while providers load", async () => {
      mockGetProviders.mockReturnValue(new Promise(() => {}));
      renderPanel();
      clickTab("Notifications");
      expect(screen.getByText("Loading providers…")).toBeInTheDocument();
    });

    it("shows empty state when no providers", async () => {
      await renderPanelAndWait();
      clickTab("Notifications");
      await waitFor(() => {
        expect(screen.getByText("No notification providers available.")).toBeInTheDocument();
      });
    });

    it("renders connected provider with badge and disconnect button", async () => {
      mockGetProviders.mockResolvedValue([
        makeProvider({ providerId: "discord", displayName: "Discord", isConnected: true }),
      ]);
      await renderPanelAndWait();
      clickTab("Notifications");

      await waitFor(() => {
        expect(screen.getByText("Discord")).toBeInTheDocument();
      });
      expect(screen.getByTestId("badge-ok")).toHaveTextContent("Connected");
      expect(screen.getByText("Disconnect")).toBeInTheDocument();
    });

    it("renders configured but disconnected provider with Set Up button", async () => {
      mockGetProviders.mockResolvedValue([
        makeProvider({ isConnected: false, isConfigured: true }),
      ]);
      await renderPanelAndWait();
      clickTab("Notifications");

      await waitFor(() => {
        expect(screen.getByTestId("badge-warn")).toHaveTextContent("Configured");
      });
      expect(screen.getByText("Set Up")).toBeInTheDocument();
    });

    it("renders unconfigured provider with Not set up badge", async () => {
      mockGetProviders.mockResolvedValue([
        makeProvider({ isConnected: false, isConfigured: false }),
      ]);
      await renderPanelAndWait();
      clickTab("Notifications");

      await waitFor(() => {
        expect(screen.getByTestId("badge-info")).toHaveTextContent("Not set up");
      });
    });

    it("shows last error for disconnected provider", async () => {
      mockGetProviders.mockResolvedValue([
        makeProvider({ isConnected: false, lastError: "Token expired" }),
      ]);
      await renderPanelAndWait();
      clickTab("Notifications");

      await waitFor(() => {
        expect(screen.getByText(/Token expired/)).toBeInTheDocument();
      });
    });

    it("calls disconnectProvider when Disconnect is clicked", async () => {
      mockGetProviders.mockResolvedValue([
        makeProvider({ providerId: "discord", isConnected: true }),
      ]);
      mockDisconnect.mockResolvedValue({ status: "disconnected", providerId: "discord" });
      // After disconnect, return updated list
      mockGetProviders.mockResolvedValueOnce([
        makeProvider({ providerId: "discord", isConnected: true }),
      ]);

      await renderPanelAndWait();
      clickTab("Notifications");

      await waitFor(() => {
        expect(screen.getByText("Disconnect")).toBeInTheDocument();
      });
      await userEvent.click(screen.getByText("Disconnect"));
      expect(mockDisconnect).toHaveBeenCalledWith("discord");
    });

    it("opens setup wizard when Set Up is clicked", async () => {
      mockGetProviders.mockResolvedValue([
        makeProvider({ providerId: "slack", displayName: "Slack", isConnected: false }),
      ]);
      await renderPanelAndWait();
      clickTab("Notifications");

      await waitFor(() => {
        expect(screen.getByText("Set Up")).toBeInTheDocument();
      });
      await userEvent.click(screen.getByText("Set Up"));
      expect(screen.getByTestId("setup-wizard-slack")).toBeInTheDocument();
    });

    it("toggles Set Up / Cancel for wizard", async () => {
      mockGetProviders.mockResolvedValue([
        makeProvider({ providerId: "slack", displayName: "Slack", isConnected: false }),
      ]);
      await renderPanelAndWait();
      clickTab("Notifications");

      await waitFor(() => {
        expect(screen.getByText("Set Up")).toBeInTheDocument();
      });
      await userEvent.click(screen.getByText("Set Up"));
      expect(screen.getByText("Cancel")).toBeInTheDocument();

      await userEvent.click(screen.getByText("Cancel"));
      expect(screen.queryByTestId("setup-wizard-slack")).not.toBeInTheDocument();
    });
  });

  // ── GitHub Tab ──────────────────────────────────────────────────────

  describe("github tab", () => {
    it("shows loading spinner while checking GitHub status", async () => {
      mockGetGitHubStatus.mockReturnValue(new Promise(() => {}));
      renderPanel();
      clickTab("GitHub");
      expect(screen.getByText("Checking GitHub status…")).toBeInTheDocument();
    });

    it("shows error state with retry button on failure", async () => {
      mockGetGitHubStatus.mockRejectedValue(new Error("Network error"));
      renderPanel();
      clickTab("GitHub");

      await waitFor(() => {
        expect(screen.getByText("Connection Error")).toBeInTheDocument();
      });
      expect(screen.getByText("Network error")).toBeInTheDocument();
      expect(screen.getByText("Retry")).toBeInTheDocument();
    });

    it("retries on Retry click", async () => {
      mockGetGitHubStatus.mockRejectedValueOnce(new Error("Network error"));
      mockGetGitHubStatus.mockResolvedValueOnce(makeGitHubStatus());
      renderPanel();
      clickTab("GitHub");

      await waitFor(() => {
        expect(screen.getByText("Retry")).toBeInTheDocument();
      });
      await userEvent.click(screen.getByText("Retry"));

      await waitFor(() => {
        expect(screen.getByText("Connected")).toBeInTheDocument();
      });
    });

    it("shows Connected status for configured repo", async () => {
      mockGetGitHubStatus.mockResolvedValue(makeGitHubStatus({ isConfigured: true }));
      renderPanel();
      clickTab("GitHub");

      await waitFor(() => {
        expect(screen.getByText("Connected")).toBeInTheDocument();
      });
    });

    it("shows Not Connected when not configured", async () => {
      mockGetGitHubStatus.mockResolvedValue(
        makeGitHubStatus({ isConfigured: false, authSource: "none", repository: null }),
      );
      renderPanel();
      clickTab("GitHub");

      await waitFor(() => {
        expect(screen.getByText("Not Connected")).toBeInTheDocument();
      });
    });

    it("displays repository name", async () => {
      mockGetGitHubStatus.mockResolvedValue(makeGitHubStatus({ repository: "acme/widget" }));
      renderPanel();
      clickTab("GitHub");

      await waitFor(() => {
        expect(screen.getByText("acme/widget")).toBeInTheDocument();
      });
    });

    it("shows dash when no repository", async () => {
      mockGetGitHubStatus.mockResolvedValue(makeGitHubStatus({ repository: null }));
      renderPanel();
      clickTab("GitHub");

      await waitFor(() => {
        expect(screen.getByText("—")).toBeInTheDocument();
      });
    });

    it("displays auth source badge", async () => {
      mockGetGitHubStatus.mockResolvedValue(makeGitHubStatus({ authSource: "oauth" }));
      renderPanel();
      clickTab("GitHub");

      await waitFor(() => {
        expect(screen.getByText("oauth")).toBeInTheDocument();
      });
    });

    it("shows oauth explanation for oauth auth", async () => {
      mockGetGitHubStatus.mockResolvedValue(makeGitHubStatus({ authSource: "oauth" }));
      renderPanel();
      clickTab("GitHub");

      await waitFor(() => {
        expect(screen.getByText(/Authenticated via browser OAuth/)).toBeInTheDocument();
      });
    });

    it("shows CLI explanation for cli auth", async () => {
      mockGetGitHubStatus.mockResolvedValue(makeGitHubStatus({ authSource: "cli" }));
      renderPanel();
      clickTab("GitHub");

      await waitFor(() => {
        expect(screen.getByText(/Authenticated via server-side/)).toBeInTheDocument();
      });
    });

    it("shows not-configured explanation with login button for none auth", async () => {
      mockGetGitHubStatus.mockResolvedValue(
        makeGitHubStatus({ isConfigured: false, authSource: "none" }),
      );
      renderPanel();
      clickTab("GitHub");

      await waitFor(() => {
        expect(screen.getByText(/GitHub is not configured/)).toBeInTheDocument();
      });
      expect(screen.getByText("Login with GitHub")).toBeInTheDocument();
    });

    it("shows PR capabilities grid when configured", async () => {
      mockGetGitHubStatus.mockResolvedValue(makeGitHubStatus({ isConfigured: true }));
      renderPanel();
      clickTab("GitHub");

      await waitFor(() => {
        expect(screen.getByText("PR Capabilities")).toBeInTheDocument();
      });
      expect(screen.getByText("Create PRs")).toBeInTheDocument();
      expect(screen.getByText("Post reviews")).toBeInTheDocument();
      expect(screen.getByText("Merge PRs")).toBeInTheDocument();
      expect(screen.getByText("Status sync")).toBeInTheDocument();
    });

    it("shows refresh button and calls fetchGitHubStatus on click", async () => {
      mockGetGitHubStatus.mockResolvedValue(makeGitHubStatus());
      renderPanel();
      clickTab("GitHub");

      await waitFor(() => {
        expect(screen.getByRole("button", { name: "Refresh GitHub status" })).toBeInTheDocument();
      });

      mockGetGitHubStatus.mockResolvedValueOnce(makeGitHubStatus({ repository: "new/repo" }));
      await userEvent.click(screen.getByRole("button", { name: "Refresh GitHub status" }));

      await waitFor(() => {
        expect(screen.getByText("new/repo")).toBeInTheDocument();
      });
    });
  });

  // ── Advanced Tab ────────────────────────────────────────────────────

  describe("advanced tab", () => {
    it("shows epoch management settings", async () => {
      await renderPanelAndWait();
      clickTab("Advanced");
      expect(screen.getByText("Conversation Epoch Management")).toBeInTheDocument();
      expect(screen.getByText("Main room")).toBeInTheDocument();
      expect(screen.getByText("Breakout room")).toBeInTheDocument();
    });

    it("populates epoch sizes from API", async () => {
      mockGetSettings.mockResolvedValue({
        "conversation.mainRoomEpochSize": "75",
        "conversation.breakoutEpochSize": "40",
      });
      await renderPanelAndWait();
      clickTab("Advanced");

      await waitFor(() => {
        const inputs = screen.getAllByRole("spinbutton");
        expect(inputs).toHaveLength(2);
        expect(inputs[0]).toHaveValue(75);
        expect(inputs[1]).toHaveValue(40);
      });
    });

    it("uses default values when API returns empty", async () => {
      mockGetSettings.mockResolvedValue({});
      await renderPanelAndWait();
      clickTab("Advanced");

      const inputs = screen.getAllByRole("spinbutton");
      expect(inputs[0]).toHaveValue(50);
      expect(inputs[1]).toHaveValue(30);
    });

    it("allows editing epoch sizes", async () => {
      await renderPanelAndWait();
      clickTab("Advanced");

      const inputs = screen.getAllByRole("spinbutton");
      await userEvent.clear(inputs[0]);
      await userEvent.type(inputs[0], "100");
      expect(inputs[0]).toHaveValue(100);
    });

    it("calls updateSystemSettings on Save", async () => {
      mockUpdateSettings.mockResolvedValue({
        "conversation.mainRoomEpochSize": "100",
        "conversation.breakoutEpochSize": "30",
      });
      await renderPanelAndWait();
      clickTab("Advanced");

      const inputs = screen.getAllByRole("spinbutton");
      await userEvent.clear(inputs[0]);
      await userEvent.type(inputs[0], "100");
      await userEvent.click(screen.getByText("Save"));

      await waitFor(() => {
        expect(mockUpdateSettings).toHaveBeenCalledWith({
          "conversation.mainRoomEpochSize": "100",
          "conversation.breakoutEpochSize": "30",
          "sprint.autoStartOnCompletion": "false",
        });
      });
    });

    it("shows saved confirmation after successful save", async () => {
      mockUpdateSettings.mockResolvedValue({});
      await renderPanelAndWait();
      clickTab("Advanced");

      await userEvent.click(screen.getByText("Save"));

      await waitFor(() => {
        expect(screen.getByText("✓ Saved")).toBeInTheDocument();
      });
    });

    it("shows desktop notifications toggle when controls provided", async () => {
      const controls: DesktopNotificationControls = {
        enabled: false,
        setEnabled: vi.fn(),
        permission: "default",
        supported: true,
        notify: vi.fn(),
      };
      await renderPanelAndWait(vi.fn(), controls);
      clickTab("Advanced");
      expect(screen.getByText("Desktop Notifications")).toBeInTheDocument();
      expect(screen.getByText("Enable desktop notifications")).toBeInTheDocument();
    });

    it("shows blocked message when permission denied", async () => {
      const controls: DesktopNotificationControls = {
        enabled: false,
        setEnabled: vi.fn(),
        permission: "denied",
        supported: true,
        notify: vi.fn(),
      };
      await renderPanelAndWait(vi.fn(), controls);
      clickTab("Advanced");
      expect(screen.getByText(/Blocked by browser/)).toBeInTheDocument();
    });

    it("shows unsupported message when not supported", async () => {
      const controls: DesktopNotificationControls = {
        enabled: false,
        setEnabled: vi.fn(),
        permission: "unsupported",
        supported: false,
        notify: vi.fn(),
      };
      await renderPanelAndWait(vi.fn(), controls);
      clickTab("Advanced");
      expect(screen.getByText(/Not supported in this browser/)).toBeInTheDocument();
    });

    it("checkbox reflects enabled state", async () => {
      const controls: DesktopNotificationControls = {
        enabled: true,
        setEnabled: vi.fn(),
        permission: "granted",
        supported: true,
        notify: vi.fn(),
      };
      await renderPanelAndWait(vi.fn(), controls);
      clickTab("Advanced");
      const checkbox = screen.getByRole("checkbox", { name: /desktop notifications/i });
      expect(checkbox).toBeChecked();
    });

    it("calls setEnabled when checkbox toggled", async () => {
      const setEnabled = vi.fn();
      const controls: DesktopNotificationControls = {
        enabled: false,
        setEnabled,
        permission: "granted",
        supported: true,
        notify: vi.fn(),
      };
      await renderPanelAndWait(vi.fn(), controls);
      clickTab("Advanced");
      const checkbox = screen.getByRole("checkbox", { name: /desktop notifications/i });
      await userEvent.click(checkbox);
      expect(setEnabled).toHaveBeenCalledWith(true);
    });

    it("shows fallback when no controls provided", async () => {
      await renderPanelAndWait();
      clickTab("Advanced");
      expect(screen.getByText("Desktop Notifications")).toBeInTheDocument();
      expect(screen.getByText("Not available")).toBeInTheDocument();
    });

    it("shows sprint automation section", async () => {
      await renderPanelAndWait();
      clickTab("Advanced");
      expect(screen.getByText("Sprint Automation")).toBeInTheDocument();
      expect(screen.getByText("Auto-start next sprint on completion")).toBeInTheDocument();
    });

    it("sprint auto-start defaults to unchecked", async () => {
      mockGetSettings.mockResolvedValue({});
      await renderPanelAndWait();
      clickTab("Advanced");
      const checkbox = screen.getByRole("checkbox", { name: /auto-start/i });
      expect(checkbox).not.toBeChecked();
    });

    it("sprint auto-start reflects API value", async () => {
      mockGetSettings.mockResolvedValue({
        "sprint.autoStartOnCompletion": "True",
      });
      await renderPanelAndWait();
      clickTab("Advanced");
      await waitFor(() => {
        const checkbox = screen.getByRole("checkbox", { name: /auto-start/i });
        expect(checkbox).toBeChecked();
      });
    });

    it("toggling sprint auto-start updates state", async () => {
      mockGetSettings.mockResolvedValue({});
      await renderPanelAndWait();
      clickTab("Advanced");
      const checkbox = screen.getByRole("checkbox", { name: /auto-start/i });
      expect(checkbox).not.toBeChecked();
      await userEvent.click(checkbox);
      expect(checkbox).toBeChecked();
    });

    it("saves sprint auto-start setting", async () => {
      mockUpdateSettings.mockResolvedValue({});
      mockGetSettings.mockResolvedValue({});
      await renderPanelAndWait();
      clickTab("Advanced");

      const checkbox = screen.getByRole("checkbox", { name: /auto-start/i });
      await userEvent.click(checkbox);
      await userEvent.click(screen.getByText("Save"));

      await waitFor(() => {
        expect(mockUpdateSettings).toHaveBeenCalledWith(
          expect.objectContaining({
            "sprint.autoStartOnCompletion": "true",
          }),
        );
      });
    });
  });
});
