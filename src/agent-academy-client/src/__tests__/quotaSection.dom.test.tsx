// @vitest-environment jsdom
import "@testing-library/jest-dom/vitest";
import { describe, expect, it, vi, afterEach } from "vitest";
import { render, screen, cleanup, fireEvent } from "@testing-library/react";
import { FluentProvider, webDarkTheme } from "@fluentui/react-components";
import QuotaSection from "../agentConfig/QuotaSection";
import { useAgentConfigCardStyles } from "../agentConfig/AgentConfigCardStyles";
import type { QuotaStatus } from "../api";

function makeQuota(overrides: Partial<QuotaStatus> = {}): QuotaStatus {
  return {
    agentId: "agent-1",
    isAllowed: true,
    configuredQuota: { maxRequestsPerHour: 100, maxTokensPerHour: 50000, maxCostPerHour: 5.0 },
    currentUsage: { requestCount: 20, totalTokens: 10000, totalCost: 1.5 },
    ...overrides,
  };
}

// We need real styles; wrap in a component that calls the hook
function TestWrapper(props: {
  quota: QuotaStatus | null;
  hasQuotaConfigured: boolean;
  hasQuotaChanges: boolean;
  maxRequestsPerHour: string;
  maxTokensPerHour: string;
  maxCostPerHour: string;
  onMaxRequestsChange: (v: string) => void;
  onMaxTokensChange: (v: string) => void;
  onMaxCostChange: (v: string) => void;
  quotaSaving: boolean;
  onSave: () => void;
  onRemove: () => void;
}) {
  const s = useAgentConfigCardStyles();
  return <QuotaSection s={s} {...props} />;
}

const defaultTestProps = {
  quota: makeQuota(),
  hasQuotaConfigured: true,
  hasQuotaChanges: false,
  maxRequestsPerHour: "100",
  maxTokensPerHour: "50000",
  maxCostPerHour: "5",
  onMaxRequestsChange: vi.fn(),
  onMaxTokensChange: vi.fn(),
  onMaxCostChange: vi.fn(),
  quotaSaving: false,
  onSave: vi.fn(),
  onRemove: vi.fn(),
};

function renderSection(overrides: Partial<typeof defaultTestProps> = {}) {
  return render(
    <FluentProvider theme={webDarkTheme}>
      <TestWrapper {...defaultTestProps} {...overrides} />
    </FluentProvider>,
  );
}

describe("QuotaSection", () => {
  afterEach(() => { cleanup(); document.body.innerHTML = ""; vi.clearAllMocks(); });

  // ── Labels ──

  it("renders quota section header", () => {
    renderSection();
    expect(screen.getByText("Resource Quotas")).toBeInTheDocument();
  });

  it("renders input labels", () => {
    renderSection();
    expect(screen.getByText("Max Requests / Hour")).toBeInTheDocument();
    expect(screen.getByText("Max Tokens / Hour")).toBeInTheDocument();
    expect(screen.getByText("Max Cost / Hour ($)")).toBeInTheDocument();
  });

  // ── Current usage display ──

  it("shows current usage stats", () => {
    renderSection({
      quota: makeQuota({
        currentUsage: { requestCount: 20, totalTokens: 10000, totalCost: 1.5 },
      }),
    });
    expect(screen.getByText("Current: 20 requests this hour")).toBeInTheDocument();
    expect(screen.getByText(/Current: 10,000 tokens this hour/)).toBeInTheDocument();
    expect(screen.getByText("Current: $1.5000 this hour")).toBeInTheDocument();
  });

  // ── Remove limits button ──

  it("shows Remove Limits when quota is configured", () => {
    renderSection({ hasQuotaConfigured: true });
    expect(screen.getByText("Remove Limits")).toBeInTheDocument();
  });

  it("hides Remove Limits when no quota configured", () => {
    renderSection({ hasQuotaConfigured: false });
    expect(screen.queryByText("Remove Limits")).not.toBeInTheDocument();
  });

  it("calls onRemove when Remove Limits clicked", () => {
    const fn = vi.fn();
    renderSection({ hasQuotaConfigured: true, onRemove: fn });
    fireEvent.click(screen.getByText("Remove Limits"));
    expect(fn).toHaveBeenCalledTimes(1);
  });

  // ── Unlimited text ──

  it("shows unlimited text when no quota configured and no changes", () => {
    renderSection({ hasQuotaConfigured: false, hasQuotaChanges: false });
    expect(screen.getByText("No limits configured — agent has unlimited access.")).toBeInTheDocument();
  });

  it("hides unlimited text when changes exist", () => {
    renderSection({ hasQuotaConfigured: false, hasQuotaChanges: true });
    expect(screen.queryByText("No limits configured — agent has unlimited access.")).not.toBeInTheDocument();
  });

  // ── Save button ──

  it("disables Save when no changes", () => {
    renderSection({ hasQuotaChanges: false });
    expect(screen.getByText("Save Quotas").closest("button")).toBeDisabled();
  });

  it("enables Save when changes exist", () => {
    renderSection({ hasQuotaChanges: true });
    expect(screen.getByText("Save Quotas").closest("button")).not.toBeDisabled();
  });

  it("calls onSave when Save clicked", () => {
    const fn = vi.fn();
    renderSection({ hasQuotaChanges: true, onSave: fn });
    fireEvent.click(screen.getByText("Save Quotas"));
    expect(fn).toHaveBeenCalledTimes(1);
  });

  it("disables Save button when saving", () => {
    renderSection({ hasQuotaConfigured: false, hasQuotaChanges: true, quotaSaving: true });
    // With hasQuotaConfigured=false, Remove Limits is hidden — only Save button exists
    // When quotaSaving=true, Save shows spinner instead of text but should be disabled
    const allButtons = screen.getAllByRole("button");
    // The only button should be the Save button (spinner replaces text)
    expect(allButtons).toHaveLength(1);
    expect(allButtons[0]).toBeDisabled();
  });

  it("disables Remove Limits when saving", () => {
    renderSection({ hasQuotaConfigured: true, quotaSaving: true });
    expect(screen.getByText("Remove Limits").closest("button")).toBeDisabled();
  });
});
