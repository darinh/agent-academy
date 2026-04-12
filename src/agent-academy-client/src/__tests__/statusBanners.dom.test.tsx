// @vitest-environment jsdom
import "@testing-library/jest-dom/vitest";
import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, beforeAll, describe, expect, it, vi } from "vitest";
import { FluentProvider, webDarkTheme } from "@fluentui/react-components";
import StatusBanners, { type StatusBannersProps } from "../StatusBanners";

// Fluent UI MessageBar uses ResizeObserver internally.
beforeAll(() => {
  globalThis.ResizeObserver ??= class {
    observe() {}
    unobserve() {}
    disconnect() {}
  } as unknown as typeof ResizeObserver;
});

// Mock child banners to simple elements with data-testids.
vi.mock("../RecoveryBanner", () => ({
  default: (props: { state: { message: string } }) => (
    <div data-testid="recovery-banner">{props.state.message}</div>
  ),
}));
vi.mock("../CircuitBreakerBanner", () => ({
  default: (props: { state: unknown }) => (
    <div data-testid="circuit-breaker-banner">{String(props.state ?? "null")}</div>
  ),
}));

const EMPTY_STYLES: Record<string, string> = { errorBar: "", recoveryBannerGlobal: "" };

function renderBanners(overrides: Partial<StatusBannersProps> = {}) {
  const defaults: StatusBannersProps = {
    err: null,
    switchError: "",
    recoveryBanner: null,
    circuitBreakerState: null,
    connectionDetail: null,
    styles: EMPTY_STYLES,
  };
  return render(
    <FluentProvider theme={webDarkTheme}>
      <StatusBanners {...defaults} {...overrides} />
    </FluentProvider>,
  );
}

afterEach(() => {
  cleanup();
  document.body.innerHTML = "";
});

describe("StatusBanners", () => {
  // ── Error bar ──────────────────────────────────────────────────────
  it("shows error message when err is set", () => {
    renderBanners({ err: "Something broke" });
    expect(screen.getByText("Error")).toBeInTheDocument();
    expect(screen.getByText("Something broke")).toBeInTheDocument();
  });

  it("shows switch error when switchError is set", () => {
    renderBanners({ switchError: "Switch failure" });
    expect(screen.getByText("Error")).toBeInTheDocument();
    expect(screen.getByText("Switch failure")).toBeInTheDocument();
  });

  it("does not show error bar when both err and switchError are null/empty", () => {
    renderBanners({ err: null, switchError: "" });
    expect(screen.queryByText("Error")).not.toBeInTheDocument();
  });

  it("shows err text (not switchError) when both are set", () => {
    renderBanners({ err: "Primary error", switchError: "Secondary error" });
    expect(screen.getByText("Primary error")).toBeInTheDocument();
    expect(screen.queryByText("Secondary error")).not.toBeInTheDocument();
  });

  // ── Recovery banner ────────────────────────────────────────────────
  it("shows recovery banner when recoveryBanner is provided", () => {
    renderBanners({
      recoveryBanner: { tone: "reconnecting", message: "Reconnecting…" },
    });
    expect(screen.getByTestId("recovery-banner")).toHaveTextContent("Reconnecting…");
  });

  it("does not show recovery banner when null", () => {
    renderBanners({ recoveryBanner: null });
    expect(screen.queryByTestId("recovery-banner")).not.toBeInTheDocument();
  });

  // ── Circuit breaker banner ─────────────────────────────────────────
  it("always renders circuit breaker banner", () => {
    renderBanners();
    expect(screen.getByTestId("circuit-breaker-banner")).toBeInTheDocument();
  });

  // ── Offline warning ────────────────────────────────────────────────
  it("shows offline warning with connectionDetail text", () => {
    renderBanners({ connectionDetail: "Lost connection to server" });
    expect(screen.getByText("Offline")).toBeInTheDocument();
    expect(screen.getByText("Lost connection to server")).toBeInTheDocument();
  });

  it("does not show offline warning when connectionDetail is null", () => {
    renderBanners({ connectionDetail: null });
    expect(screen.queryByText("Offline")).not.toBeInTheDocument();
  });
});
