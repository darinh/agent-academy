import { describe, expect, it } from "vitest";
import { renderToStaticMarkup } from "react-dom/server";
import { createElement } from "react";
import WorkspaceHeader from "../WorkspaceHeader";
import type { HeaderModel } from "../WorkspaceHeader";
import { FluentProvider, webDarkTheme } from "@fluentui/react-components";

const emptyStyles: Record<string, string> = {
  workspaceHeader: "",
  workspaceHeaderBody: "",
  workspaceHeaderTopRow: "",
  workspaceTitle: "",
  headerDivider: "",
  workspaceMetaText: "",
  workspaceHeaderSignals: "",
  workspaceSignal: "",
  workspaceSignalWarning: "",
  phasePill: "",
  phasePillDot: "",
};

function baseModel(overrides?: Partial<HeaderModel>): HeaderModel {
  return {
    title: "Test Workspace",
    meta: null,
    showPhasePill: false,
    workspaceLimited: false,
    degradedEyebrow: null,
    circuitBreakerState: null,
    ...overrides,
  };
}

function render(model: HeaderModel) {
  return renderToStaticMarkup(
    createElement(FluentProvider, { theme: webDarkTheme },
      createElement(WorkspaceHeader, { model, styles: emptyStyles }),
    ),
  );
}

describe("WorkspaceHeader", () => {
  it("renders title", () => {
    const html = render(baseModel());
    expect(html).toContain("Test Workspace");
  });

  it("renders meta when provided", () => {
    const html = render(baseModel({ meta: "3 agents active" }));
    expect(html).toContain("3 agents active");
  });

  it("does not render meta divider when meta is null", () => {
    const html = render(baseModel({ meta: null }));
    expect(html).not.toContain("3 agents active");
  });

  it("shows 'Limited mode' when workspaceLimited is true", () => {
    const html = render(baseModel({ workspaceLimited: true }));
    expect(html).toContain("Limited mode");
  });

  it("shows custom degradedEyebrow text instead of 'Limited mode'", () => {
    const html = render(baseModel({ workspaceLimited: true, degradedEyebrow: "Degraded" }));
    expect(html).toContain("Degraded");
    expect(html).not.toContain("Limited mode");
  });

  it("does not show limited badge when workspaceLimited is false", () => {
    const html = render(baseModel({ workspaceLimited: false }));
    expect(html).not.toContain("Limited mode");
  });

  it("shows 'Circuit open' when circuitBreakerState is Open", () => {
    const html = render(baseModel({ circuitBreakerState: "Open" }));
    expect(html).toContain("Circuit open");
  });

  it("shows 'Circuit probing' when circuitBreakerState is HalfOpen", () => {
    const html = render(baseModel({ circuitBreakerState: "HalfOpen" }));
    expect(html).toContain("Circuit probing");
  });

  it("does not show circuit text when state is Closed", () => {
    const html = render(baseModel({ circuitBreakerState: "Closed" }));
    expect(html).not.toContain("Circuit open");
    expect(html).not.toContain("Circuit probing");
  });

  it("shows 'Connected' when showPhasePill is true", () => {
    const html = render(baseModel({ showPhasePill: true }));
    expect(html).toContain("Connected");
  });
});
