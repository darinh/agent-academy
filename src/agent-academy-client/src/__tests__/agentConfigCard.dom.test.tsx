// @vitest-environment jsdom
/**
 * Interactive RTL tests for AgentConfigCard.
 *
 * Uses @testing-library/react + jsdom.
 * Covers: collapsed/expanded toggle, loading state, config form (model override,
 * startup prompt, template selector, custom instructions), save/reset actions,
 * dirty state detection, quota section (display, edit, save, remove), dialogs
 * (reset confirmation, remove quota confirmation), error handling, and badges.
 */
import "@testing-library/jest-dom/vitest";
import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createElement } from "react";
import { FluentProvider, webDarkTheme } from "@fluentui/react-components";

// ── Mocks ──────────────────────────────────────────────────────────────

vi.mock("../api", () => ({
  getAgentConfig: vi.fn(),
  upsertAgentConfig: vi.fn(),
  resetAgentConfig: vi.fn(),
  getAgentQuota: vi.fn(),
  updateAgentQuota: vi.fn(),
  removeAgentQuota: vi.fn(),
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

import AgentConfigCard from "../AgentConfigCard";
import type {
  AgentDefinition,
  AgentConfigResponse,
  InstructionTemplate,
  QuotaStatus,
} from "../api";
import {
  getAgentConfig,
  upsertAgentConfig,
  resetAgentConfig,
  getAgentQuota,
  updateAgentQuota,
  removeAgentQuota,
} from "../api";

const mockGetConfig = vi.mocked(getAgentConfig);
const mockUpsertConfig = vi.mocked(upsertAgentConfig);
const mockResetConfig = vi.mocked(resetAgentConfig);
const mockGetQuota = vi.mocked(getAgentQuota);
const mockUpdateQuota = vi.mocked(updateAgentQuota);
const mockRemoveQuota = vi.mocked(removeAgentQuota);

// ── Factories ──────────────────────────────────────────────────────────

function makeAgent(overrides: Partial<AgentDefinition> = {}): AgentDefinition {
  return {
    id: "architect",
    name: "Athena",
    role: "Architect",
    summary: "System architect",
    startupPrompt: "You are an architect agent.",
    model: "gpt-5",
    capabilityTags: ["design", "review"],
    enabledTools: ["RUN_BUILD"],
    autoJoinDefaultRoom: true,
    gitIdentity: null,
    ...overrides,
  };
}

function makeConfig(overrides: Partial<AgentConfigResponse> = {}): AgentConfigResponse {
  return {
    agentId: "architect",
    effectiveModel: "gpt-5",
    effectiveStartupPrompt: "You are an architect agent.",
    hasOverride: false,
    override: null,
    ...overrides,
  };
}

function makeConfigWithOverride(): AgentConfigResponse {
  return makeConfig({
    hasOverride: true,
    override: {
      modelOverride: "claude-opus-4",
      startupPromptOverride: "Custom prompt",
      customInstructions: "Be thorough",
      instructionTemplateId: "tmpl-1",
      instructionTemplateName: "Engineering",
      updatedAt: "2026-04-10T12:00:00Z",
    },
  });
}

function makeQuota(overrides: Partial<QuotaStatus> = {}): QuotaStatus {
  return {
    agentId: "architect",
    isAllowed: true,
    deniedReason: null,
    retryAfterSeconds: null,
    configuredQuota: null,
    currentUsage: null,
    ...overrides,
  };
}

function makeQuotaWithLimits(): QuotaStatus {
  return makeQuota({
    configuredQuota: {
      maxRequestsPerHour: 100,
      maxTokensPerHour: 500000,
      maxCostPerHour: 5.0,
    },
    currentUsage: {
      requestCount: 42,
      totalTokens: 150000,
      totalCost: 1.5,
    },
  });
}

function makeTemplates(): InstructionTemplate[] {
  return [
    {
      id: "tmpl-1",
      name: "Engineering",
      description: "Standard engineering template",
      content: "Follow engineering best practices.",
      createdAt: "2026-04-01T00:00:00Z",
      updatedAt: "2026-04-01T00:00:00Z",
    },
    {
      id: "tmpl-2",
      name: "Reviewer",
      description: "Review template",
      content: "Focus on code quality.",
      createdAt: "2026-04-01T00:00:00Z",
      updatedAt: "2026-04-01T00:00:00Z",
    },
  ];
}

// ── Helpers ─────────────────────────────────────────────────────────────

function renderCard(props: {
  agent?: AgentDefinition;
  templates?: InstructionTemplate[];
  expanded?: boolean;
  onToggle?: () => void;
  onSaved?: () => void;
} = {}) {
  const {
    agent = makeAgent(),
    templates = makeTemplates(),
    expanded = false,
    onToggle = vi.fn(),
    onSaved = vi.fn(),
  } = props;

  return {
    ...render(
      createElement(
        FluentProvider,
        { theme: webDarkTheme },
        createElement(AgentConfigCard, { agent, templates, expanded, onToggle, onSaved }),
      ),
    ),
    onToggle,
    onSaved,
  };
}

function setupDefaultMocks(
  config: AgentConfigResponse = makeConfig(),
  quota: QuotaStatus = makeQuota(),
) {
  mockGetConfig.mockResolvedValue(config);
  mockGetQuota.mockResolvedValue(quota);
}

// ── Tests ───────────────────────────────────────────────────────────────

describe("AgentConfigCard (interactive)", () => {
  beforeEach(() => {
    vi.resetAllMocks();
  });

  afterEach(() => {
    cleanup();
    // Fluent UI Dialog renders via portals appended to document.body.
    // React cleanup() unmounts the component tree but portal container
    // divs may linger, causing subsequent tests to find stale elements
    // or fail to locate newly-rendered dialogs under heavy parallel load.
    document.body.innerHTML = "";
  });

  // ── Collapsed state ──

  describe("collapsed state", () => {
    it("shows agent name, role, and model badge", () => {
      renderCard();
      expect(screen.getByText("Athena")).toBeInTheDocument();
      expect(screen.getByText("Architect")).toBeInTheDocument();
      expect(screen.getByText("gpt-5")).toBeInTheDocument();
    });

    it("calls onToggle when header is clicked", async () => {
      const user = userEvent.setup();
      const { onToggle } = renderCard();
      await user.click(screen.getByText("Athena"));
      expect(onToggle).toHaveBeenCalledTimes(1);
    });

    it("does not fetch config or quota when collapsed", () => {
      renderCard();
      expect(mockGetConfig).not.toHaveBeenCalled();
      expect(mockGetQuota).not.toHaveBeenCalled();
    });

    it("shows default when model is null", () => {
      renderCard({ agent: makeAgent({ model: null }) });
      expect(screen.getByText("default")).toBeInTheDocument();
    });
  });

  // ── Loading ──

  describe("loading state", () => {
    it("shows spinner when expanded and fetching", () => {
      mockGetConfig.mockReturnValue(new Promise(() => {}));
      mockGetQuota.mockReturnValue(new Promise(() => {}));
      renderCard({ expanded: true });
      expect(screen.getByText("Loading configuration…")).toBeInTheDocument();
    });
  });

  // ── Error handling ──

  describe("error handling", () => {
    it("shows error when config fails to load", async () => {
      mockGetConfig.mockRejectedValue(new Error("Config unavailable"));
      mockGetQuota.mockResolvedValue(makeQuota());
      renderCard({ expanded: true });
      await waitFor(() => {
        expect(screen.getByText("Config unavailable")).toBeInTheDocument();
      });
    });

    it("loads config even if quota fails", async () => {
      setupDefaultMocks();
      mockGetQuota.mockRejectedValue(new Error("Quota down"));
      renderCard({ expanded: true });
      await waitFor(() => {
        expect(screen.getByText("Model Override")).toBeInTheDocument();
      });
      // quota failure is non-fatal — no error shown for it alone
    });

    it("shows error on save failure", async () => {
      setupDefaultMocks();
      mockUpsertConfig.mockRejectedValue(new Error("Save failed"));
      const user = userEvent.setup();
      renderCard({ expanded: true });
      await waitFor(() => {
        expect(screen.getByText("Model Override")).toBeInTheDocument();
      });

      // Type into model override to create dirty state
      // Fluent UI Input: find by label association
      const modelInput = screen.getByPlaceholderText("gpt-5");
      await user.type(modelInput, "claude-opus-4");
      await user.click(screen.getByText("Save"));
      await waitFor(() => {
        expect(screen.getByText("Save failed")).toBeInTheDocument();
      });
    });
  });

  // ── Config form ──

  describe("config form", () => {
    it("renders all form fields when expanded", async () => {
      setupDefaultMocks();
      renderCard({ expanded: true });
      await waitFor(() => {
        expect(screen.getByText("Model Override")).toBeInTheDocument();
      });
      expect(screen.getByText("Startup Prompt Override")).toBeInTheDocument();
      expect(screen.getByText("Instruction Template")).toBeInTheDocument();
      expect(screen.getByText("Custom Instructions")).toBeInTheDocument();
    });

    it("populates form with existing override values", async () => {
      setupDefaultMocks(makeConfigWithOverride());
      renderCard({ expanded: true });
      await waitFor(() => {
        expect(screen.getByDisplayValue("claude-opus-4")).toBeInTheDocument();
      });
      expect(screen.getByDisplayValue("Custom prompt")).toBeInTheDocument();
      expect(screen.getByDisplayValue("Be thorough")).toBeInTheDocument();
    });

    it("shows template options in selector", async () => {
      setupDefaultMocks();
      renderCard({ expanded: true });
      await waitFor(() => {
        expect(screen.getByText("Instruction Template")).toBeInTheDocument();
      });
      const select = screen.getByRole("combobox");
      expect(select).toBeInTheDocument();
      // Verify template options are rendered
      expect(within(select).getByText("None")).toBeInTheDocument();
      expect(within(select).getByText("Engineering")).toBeInTheDocument();
      expect(within(select).getByText("Reviewer")).toBeInTheDocument();
    });
  });

  // ── Save action ──

  describe("save action", () => {
    it("Save button is disabled when no changes", async () => {
      setupDefaultMocks();
      renderCard({ expanded: true });
      await waitFor(() => {
        expect(screen.getByText("Save")).toBeInTheDocument();
      });
      expect(screen.getByText("Save").closest("button")).toBeDisabled();
    });

    it("Save button enables when form is dirty", async () => {
      setupDefaultMocks();
      const user = userEvent.setup();
      renderCard({ expanded: true });
      await waitFor(() => {
        expect(screen.getByText("Model Override")).toBeInTheDocument();
      });
      const modelInput = screen.getByPlaceholderText("gpt-5");
      await user.type(modelInput, "gpt-4");
      expect(screen.getByText("Save").closest("button")).not.toBeDisabled();
    });

    it("calls upsertAgentConfig on save with correct data", async () => {
      setupDefaultMocks();
      mockUpsertConfig.mockResolvedValue(makeConfig());
      const user = userEvent.setup();
      const { onSaved } = renderCard({ expanded: true });
      await waitFor(() => {
        expect(screen.getByText("Model Override")).toBeInTheDocument();
      });

      const modelInput = screen.getByPlaceholderText("gpt-5");
      await user.type(modelInput, "gpt-4");
      await user.click(screen.getByText("Save"));

      await waitFor(() => {
        expect(mockUpsertConfig).toHaveBeenCalledWith("architect", {
          modelOverride: "gpt-4",
          startupPromptOverride: null,
          customInstructions: null,
          instructionTemplateId: null,
        });
      });
      expect(onSaved).toHaveBeenCalled();
    });
  });

  // ── Reset action ──

  describe("reset action", () => {
    it("hides Reset button when no override exists", async () => {
      setupDefaultMocks();
      renderCard({ expanded: true });
      await waitFor(() => {
        expect(screen.getByText("Save")).toBeInTheDocument();
      });
      expect(screen.queryByText("Reset to Defaults")).not.toBeInTheDocument();
    });

    it("shows Reset button when override exists", async () => {
      setupDefaultMocks(makeConfigWithOverride());
      renderCard({ expanded: true });
      await waitFor(() => {
        expect(screen.getByText("Reset to Defaults")).toBeInTheDocument();
      });
    });

    it("shows confirmation dialog on Reset click", async () => {
      setupDefaultMocks(makeConfigWithOverride());
      const user = userEvent.setup();
      renderCard({ expanded: true });
      await waitFor(() => {
        expect(screen.getByText("Reset to Defaults")).toBeInTheDocument();
      });
      await user.click(screen.getByText("Reset to Defaults"));
      expect(screen.getByText(/Reset Athena's Configuration/)).toBeInTheDocument();
      expect(screen.getByText(/remove all overrides/)).toBeInTheDocument();
    });

    it("calls resetAgentConfig when confirmed", async () => {
      setupDefaultMocks(makeConfigWithOverride());
      mockResetConfig.mockResolvedValue(makeConfig());
      const user = userEvent.setup();
      const { onSaved } = renderCard({ expanded: true });
      await waitFor(() => {
        expect(screen.getByText("Reset to Defaults")).toBeInTheDocument();
      });
      await user.click(screen.getByText("Reset to Defaults"));

      // Wait for dialog content, then scope interaction via role
      await waitFor(() => {
        expect(screen.getByText(/Reset Athena's Configuration/)).toBeInTheDocument();
      });
      const dialog = screen.getByRole("dialog");
      await user.click(within(dialog).getByText("Reset"));

      await waitFor(() => {
        expect(mockResetConfig).toHaveBeenCalledWith("architect");
      });
      expect(onSaved).toHaveBeenCalled();
    });

    it("cancels reset when Cancel is clicked", async () => {
      setupDefaultMocks(makeConfigWithOverride());
      const user = userEvent.setup();
      renderCard({ expanded: true });
      await waitFor(() => {
        expect(screen.getByText("Reset to Defaults")).toBeInTheDocument();
      });
      await user.click(screen.getByText("Reset to Defaults"));
      await waitFor(() => {
        expect(screen.getByText(/Reset Athena's Configuration/)).toBeInTheDocument();
      });
      const dialog = screen.getByRole("dialog");
      await user.click(within(dialog).getByText("Cancel"));
      expect(mockResetConfig).not.toHaveBeenCalled();
    });
  });

  // ── Badges ──

  describe("badges", () => {
    it("shows Customized badge when override exists", async () => {
      setupDefaultMocks(makeConfigWithOverride());
      renderCard({ expanded: true });
      await waitFor(() => {
        expect(screen.getByText("Customized")).toBeInTheDocument();
      });
    });

    it("shows Quota badge when limits are configured", async () => {
      setupDefaultMocks(makeConfig(), makeQuotaWithLimits());
      renderCard({ expanded: true });
      await waitFor(() => {
        expect(screen.getByText("Quota")).toBeInTheDocument();
      });
    });

    it("hides Customized badge when no override", async () => {
      setupDefaultMocks();
      renderCard({ expanded: true });
      await waitFor(() => {
        expect(screen.getByText("Model Override")).toBeInTheDocument();
      });
      expect(screen.queryByText("Customized")).not.toBeInTheDocument();
    });
  });

  // ── Quota section ──

  describe("quota section", () => {
    it("shows 'No limits configured' when no quota set", async () => {
      setupDefaultMocks();
      renderCard({ expanded: true });
      await waitFor(() => {
        expect(screen.getByText("Resource Quotas")).toBeInTheDocument();
      });
      expect(screen.getByText(/No limits configured/)).toBeInTheDocument();
    });

    it("populates quota inputs with existing values", async () => {
      setupDefaultMocks(makeConfig(), makeQuotaWithLimits());
      renderCard({ expanded: true });
      await waitFor(() => {
        expect(screen.getByDisplayValue("100")).toBeInTheDocument();
      });
      expect(screen.getByDisplayValue("500000")).toBeInTheDocument();
      expect(screen.getByDisplayValue("5")).toBeInTheDocument();
    });

    it("shows current usage stats", async () => {
      setupDefaultMocks(makeConfig(), makeQuotaWithLimits());
      renderCard({ expanded: true });
      await waitFor(() => {
        expect(screen.getByText(/42 requests this hour/)).toBeInTheDocument();
      });
      expect(screen.getByText(/150,000 tokens this hour/)).toBeInTheDocument();
      expect(screen.getByText(/\$1\.5000 this hour/)).toBeInTheDocument();
    });

    it("Save Quotas button is disabled when no changes", async () => {
      setupDefaultMocks(makeConfig(), makeQuotaWithLimits());
      renderCard({ expanded: true });
      await waitFor(() => {
        expect(screen.getByText("Save Quotas")).toBeInTheDocument();
      });
      expect(screen.getByText("Save Quotas").closest("button")).toBeDisabled();
    });

    it("calls updateAgentQuota on save with correct data", async () => {
      setupDefaultMocks(makeConfig(), makeQuotaWithLimits());
      mockUpdateQuota.mockResolvedValue(makeQuotaWithLimits());
      const user = userEvent.setup();
      renderCard({ expanded: true });
      await waitFor(() => {
        expect(screen.getByDisplayValue("100")).toBeInTheDocument();
      });

      // Change max requests
      const reqInput = screen.getByDisplayValue("100");
      await user.clear(reqInput);
      await user.type(reqInput, "200");
      await user.click(screen.getByText("Save Quotas"));

      await waitFor(() => {
        expect(mockUpdateQuota).toHaveBeenCalledWith("architect", {
          maxRequestsPerHour: 200,
          maxTokensPerHour: 500000,
          maxCostPerHour: 5,
        });
      });
    });

    it("shows Remove Limits button when quota exists", async () => {
      setupDefaultMocks(makeConfig(), makeQuotaWithLimits());
      renderCard({ expanded: true });
      await waitFor(() => {
        expect(screen.getByText("Remove Limits")).toBeInTheDocument();
      });
    });

    it("hides Remove Limits button when no quota exists", async () => {
      setupDefaultMocks();
      renderCard({ expanded: true });
      await waitFor(() => {
        expect(screen.getByText("Resource Quotas")).toBeInTheDocument();
      });
      expect(screen.queryByText("Remove Limits")).not.toBeInTheDocument();
    });

    it("shows confirmation dialog on Remove Limits click", async () => {
      setupDefaultMocks(makeConfig(), makeQuotaWithLimits());
      const user = userEvent.setup();
      renderCard({ expanded: true });
      await waitFor(() => {
        expect(screen.getByText("Remove Limits")).toBeInTheDocument();
      });
      await user.click(screen.getByText("Remove Limits"));
      expect(screen.getByText(/Remove Athena's Quotas/)).toBeInTheDocument();
    });

    it("calls removeAgentQuota when confirmed", async () => {
      setupDefaultMocks(makeConfig(), makeQuotaWithLimits());
      mockRemoveQuota.mockResolvedValue({ status: "removed", agentId: "architect" });
      // After removal, getAgentQuota is called again — return empty quota
      // Use mockResolvedValueOnce so it doesn't override the initial mock
      const user = userEvent.setup();
      renderCard({ expanded: true });
      await waitFor(() => {
        expect(screen.getByText("Remove Limits")).toBeInTheDocument();
      });
      // Override getQuota for the post-removal fetch
      mockGetQuota.mockResolvedValueOnce(makeQuota());
      // Click the "Remove Limits" button to open dialog
      await user.click(screen.getByRole("button", { name: /Remove Limits/ }));
      // Wait for dialog to appear, then confirm
      await waitFor(() => {
        expect(screen.getByText(/Remove Athena's Quotas/)).toBeInTheDocument();
      });
      // The dialog has its own "Remove Limits" button — grab all and click the last one
      const removeBtns = screen.getAllByRole("button", { name: /Remove Limits/ });
      await user.click(removeBtns[removeBtns.length - 1]);
      await waitFor(() => {
        expect(mockRemoveQuota).toHaveBeenCalledWith("architect");
      });
    });

    it("shows error for invalid quota values", async () => {
      setupDefaultMocks(makeConfig(), makeQuota());
      const user = userEvent.setup();
      renderCard({ expanded: true });
      await waitFor(() => {
        expect(screen.getByText("Resource Quotas")).toBeInTheDocument();
      });

      // Type negative value (parseQuotaInt returns NaN for negative numbers)
      const inputs = screen.getAllByPlaceholderText("Unlimited");
      await user.type(inputs[0], "-5");
      await user.click(screen.getByText("Save Quotas"));
      await waitFor(() => {
        expect(screen.getByText(/must be non-negative/)).toBeInTheDocument();
      });
    });
  });

  // ── Cleanup on unmount ──

  describe("cleanup", () => {
    it("does not emit React warnings when stale config resolves after collapse", async () => {
      const consoleErrorSpy = vi.spyOn(console, "error").mockImplementation(() => {});
      let resolveConfig: (v: AgentConfigResponse) => void;
      const configPromise = new Promise<AgentConfigResponse>((r) => { resolveConfig = r; });
      mockGetConfig.mockReturnValue(configPromise);
      mockGetQuota.mockResolvedValue(makeQuota());

      const { rerender } = renderCard({ expanded: true });
      expect(screen.getByText("Loading configuration…")).toBeInTheDocument();

      // Collapse by re-rendering with expanded=false
      rerender(
        createElement(
          FluentProvider,
          { theme: webDarkTheme },
          createElement(AgentConfigCard, {
            agent: makeAgent(),
            templates: makeTemplates(),
            expanded: false,
            onToggle: vi.fn(),
            onSaved: vi.fn(),
          }),
        ),
      );

      // Resolve the stale promise — should be ignored via cancelled flag
      resolveConfig!(makeConfig());
      // Flush microtasks
      await new Promise((r) => setTimeout(r, 50));

      // No "state update" or "act()" warnings should be emitted
      const stateWarnings = consoleErrorSpy.mock.calls.filter(
        (args) => args.some((a) => typeof a === "string" && /state update|act\(/.test(a)),
      );
      expect(stateWarnings).toHaveLength(0);
      consoleErrorSpy.mockRestore();
    });
  });
});
