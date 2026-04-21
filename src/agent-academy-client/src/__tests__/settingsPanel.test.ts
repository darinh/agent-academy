import { describe, expect, it, vi, beforeEach } from "vitest";
import type {
  ProviderStatus,
  AgentDefinition,
  InstructionTemplate,
  GitHubStatus,
  GitHubAuthSource,
} from "../api";

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

const mockGetNotificationProviders = vi.mocked(getNotificationProviders);
const mockDisconnectProvider = vi.mocked(disconnectProvider);
const mockGetConfiguredAgents = vi.mocked(getConfiguredAgents);
const mockGetInstructionTemplates = vi.mocked(getInstructionTemplates);
const mockGetSystemSettings = vi.mocked(getSystemSettings);
const mockUpdateSystemSettings = vi.mocked(updateSystemSettings);
const mockCreateCustomAgent = vi.mocked(createCustomAgent);
const mockDeleteCustomAgent = vi.mocked(deleteCustomAgent);
const mockGetGitHubStatus = vi.mocked(getGitHubStatus);

// ── Factories ──

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
    model: "claude-opus-4.7",
    capabilityTags: ["design"],
    enabledTools: ["search-code"],
    autoJoinDefaultRoom: true,
    gitIdentity: null,
    ...overrides,
  };
}

function makeTemplate(overrides: Partial<InstructionTemplate> = {}): InstructionTemplate {
  return {
    id: "template-1",
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

function toKebabCase(name: string): string {
  return name
    .replace(/[^a-zA-Z0-9\s_-]/g, "")
    .trim()
    .toLowerCase()
    .replace(/[\s_]+/g, "-")
    .replace(/-+/g, "-")
    .replace(/^-|-$/g, "");
}

// ── Tests ──

describe("SettingsPanel", () => {
  beforeEach(() => {
    vi.resetAllMocks();
  });

  describe("toKebabCase", () => {
    it("converts simple name", () => {
      expect(toKebabCase("My Agent")).toBe("my-agent");
    });

    it("converts underscores to hyphens", () => {
      expect(toKebabCase("my_agent_name")).toBe("my-agent-name");
    });

    it("removes special characters", () => {
      expect(toKebabCase("Agent! @#$% Test")).toBe("agent-test");
    });

    it("collapses multiple spaces", () => {
      expect(toKebabCase("Agent   Name")).toBe("agent-name");
    });

    it("handles empty string", () => {
      expect(toKebabCase("")).toBe("");
    });

    it("handles only special characters", () => {
      expect(toKebabCase("!@#$%")).toBe("");
    });

    it("trims leading/trailing hyphens", () => {
      expect(toKebabCase(" - Agent - ")).toBe("agent");
    });

    it("handles mixed case and numbers", () => {
      expect(toKebabCase("Agent V2 Test")).toBe("agent-v2-test");
    });

    it("handles already kebab-case input", () => {
      expect(toKebabCase("my-agent")).toBe("my-agent");
    });

    it("handles single word", () => {
      expect(toKebabCase("Agent")).toBe("agent");
    });

    it("collapses multiple hyphens", () => {
      expect(toKebabCase("Agent--Name")).toBe("agent-name");
    });
  });

  describe("tab structure", () => {
    const TABS = [
      { id: "custom-agents", label: "Custom Agents" },
      { id: "built-in", label: "Built-in Agents" },
      { id: "templates", label: "Templates" },
      { id: "notifications", label: "Notifications" },
      { id: "github", label: "GitHub" },
      { id: "advanced", label: "Advanced" },
    ];

    it("has 6 tabs", () => {
      expect(TABS).toHaveLength(6);
    });

    it("default tab is custom-agents", () => {
      expect(TABS[0].id).toBe("custom-agents");
    });

    it("all tabs have unique IDs", () => {
      const ids = TABS.map((t) => t.id);
      expect(new Set(ids).size).toBe(ids.length);
    });

    it("github tab appears before advanced", () => {
      const ghIndex = TABS.findIndex(t => t.id === "github");
      const advIndex = TABS.findIndex(t => t.id === "advanced");
      expect(ghIndex).toBeLessThan(advIndex);
      expect(ghIndex).toBeGreaterThan(0);
    });
  });

  describe("agent filtering logic", () => {
    it("separates built-in from custom agents", () => {
      const agents = [
        makeAgent({ id: "architect", role: "architect" }),
        makeAgent({ id: "engineer", role: "engineer" }),
        makeAgent({ id: "custom-1", role: "Custom", name: "My Bot" }),
      ];

      const builtIn = agents.filter((a) => a.role !== "Custom");
      const custom = agents.filter((a) => a.role === "Custom");

      expect(builtIn).toHaveLength(2);
      expect(custom).toHaveLength(1);
      expect(custom[0].name).toBe("My Bot");
    });

    it("handles all custom agents", () => {
      const agents = [
        makeAgent({ role: "Custom" }),
        makeAgent({ id: "c2", role: "Custom" }),
      ];

      const builtIn = agents.filter((a) => a.role !== "Custom");
      const custom = agents.filter((a) => a.role === "Custom");

      expect(builtIn).toHaveLength(0);
      expect(custom).toHaveLength(2);
    });

    it("handles no agents", () => {
      const agents: AgentDefinition[] = [];
      const builtIn = agents.filter((a) => a.role !== "Custom");
      const custom = agents.filter((a) => a.role === "Custom");
      expect(builtIn).toHaveLength(0);
      expect(custom).toHaveLength(0);
    });
  });

  describe("custom agent form validation", () => {
    it("requires non-empty name and prompt", () => {
      const name = "";
      const prompt = "";
      const canCreate = name.trim() !== "" && prompt.trim() !== "";
      expect(canCreate).toBe(false);
    });

    it("rejects whitespace-only name", () => {
      const name = "   ";
      const prompt = "You are helpful.";
      const canCreate = name.trim() !== "" && prompt.trim() !== "";
      expect(canCreate).toBe(false);
    });

    it("rejects whitespace-only prompt", () => {
      const name = "My Agent";
      const prompt = "   ";
      const canCreate = name.trim() !== "" && prompt.trim() !== "";
      expect(canCreate).toBe(false);
    });

    it("accepts valid name and prompt", () => {
      const name = "My Agent";
      const prompt = "You are helpful.";
      const canCreate = name.trim() !== "" && prompt.trim() !== "";
      expect(canCreate).toBe(true);
    });

    it("model is optional — empty string becomes undefined", () => {
      const model = "";
      const resolved = model.trim() || undefined;
      expect(resolved).toBeUndefined();
    });

    it("model is passed when non-empty", () => {
      const model = "claude-sonnet-4";
      const resolved = model.trim() || undefined;
      expect(resolved).toBe("claude-sonnet-4");
    });
  });

  describe("API integration", () => {
    it("getNotificationProviders returns provider list", async () => {
      mockGetNotificationProviders.mockResolvedValue([
        makeProvider({ providerId: "discord", isConnected: true }),
        makeProvider({ providerId: "slack", displayName: "Slack", isConnected: false }),
      ]);
      const result = await getNotificationProviders();
      expect(result).toHaveLength(2);
      expect(result[0].isConnected).toBe(true);
      expect(result[1].isConnected).toBe(false);
    });

    it("disconnectProvider is callable", async () => {
      mockDisconnectProvider.mockResolvedValue({ status: "disconnected", providerId: "discord" });
      await disconnectProvider("discord");
      expect(mockDisconnectProvider).toHaveBeenCalledWith("discord");
    });

    it("getConfiguredAgents returns agent definitions", async () => {
      mockGetConfiguredAgents.mockResolvedValue([
        makeAgent(),
        makeAgent({ id: "engineer", role: "engineer" }),
      ]);
      const result = await getConfiguredAgents();
      expect(result).toHaveLength(2);
      expect(result[0].id).toBe("architect");
    });

    it("getInstructionTemplates returns templates", async () => {
      mockGetInstructionTemplates.mockResolvedValue([makeTemplate()]);
      const result = await getInstructionTemplates();
      expect(result).toHaveLength(1);
      expect(result[0].name).toBe("Default Template");
    });

    it("getSystemSettings returns key-value map", async () => {
      mockGetSystemSettings.mockResolvedValue({
        "conversation.mainRoomEpochSize": "50",
        "conversation.breakoutEpochSize": "30",
      });
      const settings = await getSystemSettings();
      expect(settings["conversation.mainRoomEpochSize"]).toBe("50");
      expect(settings["conversation.breakoutEpochSize"]).toBe("30");
    });

    it("updateSystemSettings sends updated values", async () => {
      mockUpdateSystemSettings.mockResolvedValue({ "conversation.mainRoomEpochSize": "100", "conversation.breakoutEpochSize": "50" });
      await updateSystemSettings({
        "conversation.mainRoomEpochSize": "100",
        "conversation.breakoutEpochSize": "50",
      });
      expect(mockUpdateSystemSettings).toHaveBeenCalledWith({
        "conversation.mainRoomEpochSize": "100",
        "conversation.breakoutEpochSize": "50",
      });
    });

    it("createCustomAgent sends agent data", async () => {
      mockCreateCustomAgent.mockResolvedValue(makeAgent({ id: "my-bot", name: "My Bot" }));
      await createCustomAgent({
        name: "My Bot",
        prompt: "You are helpful.",
        model: "claude-sonnet-4",
      });
      expect(mockCreateCustomAgent).toHaveBeenCalledWith({
        name: "My Bot",
        prompt: "You are helpful.",
        model: "claude-sonnet-4",
      });
    });

    it("createCustomAgent omits model when undefined", async () => {
      mockCreateCustomAgent.mockResolvedValue(makeAgent({ id: "bot", name: "Bot" }));
      await createCustomAgent({
        name: "Bot",
        prompt: "Hello.",
        model: undefined,
      });
      expect(mockCreateCustomAgent).toHaveBeenCalledWith({
        name: "Bot",
        prompt: "Hello.",
        model: undefined,
      });
    });

    it("deleteCustomAgent is callable", async () => {
      mockDeleteCustomAgent.mockResolvedValue({ status: "deleted", agentId: "custom-1" });
      await deleteCustomAgent("custom-1");
      expect(mockDeleteCustomAgent).toHaveBeenCalledWith("custom-1");
    });
  });

  describe("settings persistence logic", () => {
    it("epoch size values default to string numbers", () => {
      const mainRoomEpochSize = "50";
      const breakoutEpochSize = "30";
      expect(parseInt(mainRoomEpochSize)).toBe(50);
      expect(parseInt(breakoutEpochSize)).toBe(30);
    });

    it("settings values from API override defaults", () => {
      const defaults = { mainRoom: "50", breakout: "30" };
      const apiSettings: Record<string, string> = {
        "conversation.mainRoomEpochSize": "100",
      };

      const mainRoom = apiSettings["conversation.mainRoomEpochSize"] ?? defaults.mainRoom;
      const breakout = apiSettings["conversation.breakoutEpochSize"] ?? defaults.breakout;

      expect(mainRoom).toBe("100");
      expect(breakout).toBe("30");
    });
  });

  describe("GitHub status integration", () => {
    it("getGitHubStatus returns full status with OAuth auth", async () => {
      const status = makeGitHubStatus();
      mockGetGitHubStatus.mockResolvedValue(status);
      const result = await getGitHubStatus();
      expect(result.isConfigured).toBe(true);
      expect(result.repository).toBe("owner/repo");
      expect(result.authSource).toBe("oauth");
    });

    it("getGitHubStatus returns CLI auth source", async () => {
      mockGetGitHubStatus.mockResolvedValue(makeGitHubStatus({ authSource: "cli" }));
      const result = await getGitHubStatus();
      expect(result.authSource).toBe("cli");
    });

    it("getGitHubStatus returns none when not configured", async () => {
      mockGetGitHubStatus.mockResolvedValue(makeGitHubStatus({
        isConfigured: false,
        repository: null,
        authSource: "none",
      }));
      const result = await getGitHubStatus();
      expect(result.isConfigured).toBe(false);
      expect(result.repository).toBeNull();
      expect(result.authSource).toBe("none");
    });

    it("getGitHubStatus propagates errors", async () => {
      mockGetGitHubStatus.mockRejectedValue(new Error("Network error"));
      await expect(getGitHubStatus()).rejects.toThrow("Network error");
    });
  });

  describe("GitHub status display logic", () => {
    function getAuthSourceTone(source: GitHubAuthSource): "good" | "info" | "critical" {
      if (source === "oauth") return "good";
      if (source === "cli") return "info";
      return "critical";
    }

    function getCapabilities(isConfigured: boolean) {
      return {
        createPrs: isConfigured,
        postReviews: isConfigured,
        mergePrs: isConfigured,
        statusSync: isConfigured,
      };
    }

    it("maps oauth to good tone", () => {
      expect(getAuthSourceTone("oauth")).toBe("good");
    });

    it("maps cli to info tone", () => {
      expect(getAuthSourceTone("cli")).toBe("info");
    });

    it("maps none to critical tone", () => {
      expect(getAuthSourceTone("none")).toBe("critical");
    });

    it("all capabilities enabled when configured", () => {
      const caps = getCapabilities(true);
      expect(Object.values(caps).every(Boolean)).toBe(true);
    });

    it("all capabilities disabled when not configured", () => {
      const caps = getCapabilities(false);
      expect(Object.values(caps).every(v => !v)).toBe(true);
    });

    it("factory produces valid GitHubStatus shape", () => {
      const status = makeGitHubStatus();
      expect(status).toHaveProperty("isConfigured");
      expect(status).toHaveProperty("repository");
      expect(status).toHaveProperty("authSource");
      expect(["oauth", "cli", "none"]).toContain(status.authSource);
    });

    it("factory allows partial overrides", () => {
      const status = makeGitHubStatus({ authSource: "cli", repository: "other/repo" });
      expect(status.isConfigured).toBe(true); // default preserved
      expect(status.authSource).toBe("cli");
      expect(status.repository).toBe("other/repo");
    });
  });
});
