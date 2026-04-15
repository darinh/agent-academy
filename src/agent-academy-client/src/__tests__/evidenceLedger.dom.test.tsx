// @vitest-environment jsdom
/**
 * DOM tests for EvidenceLedger.
 *
 * Covers: load button, loading state, empty state, table rendering with evidence rows.
 */
import "@testing-library/jest-dom/vitest";
import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { createElement } from "react";
import { FluentProvider, webDarkTheme } from "@fluentui/react-components";

vi.mock("../V3Badge", () => ({
  default: ({
    children,
    color,
  }: {
    children: React.ReactNode;
    color: string;
  }) => createElement("span", { "data-testid": `badge-${color}` }, children),
}));

import EvidenceLedger from "../taskList/EvidenceLedger";
import type { EvidenceRow } from "../api";

afterEach(cleanup);

function makeEvidence(overrides: Partial<EvidenceRow> = {}): EvidenceRow {
  return {
    id: "ev-1",
    phase: "after",
    checkName: "build",
    tool: "dotnet",
    command: "dotnet build",
    exitCode: 0,
    output: null,
    passed: true,
    agentName: "Hephaestus",
    createdAt: "2026-04-15T10:00:00Z",
    ...overrides,
  };
}

interface RenderOpts {
  evidence?: EvidenceRow[];
  loading?: boolean;
  loaded?: boolean;
  onLoad?: () => void;
}

function renderLedger(opts: RenderOpts = {}) {
  const {
    evidence = [],
    loading = false,
    loaded = false,
    onLoad = vi.fn(),
  } = opts;
  return render(
    createElement(
      FluentProvider,
      { theme: webDarkTheme },
      createElement(EvidenceLedger, { evidence, loading, loaded, onLoad }),
    ),
  );
}

describe("EvidenceLedger", () => {
  it("shows Load button when not yet loaded", () => {
    renderLedger();
    expect(screen.getByRole("button", { name: /Load/i })).toBeInTheDocument();
  });

  it("calls onLoad when Load button is clicked", async () => {
    const onLoad = vi.fn();
    renderLedger({ onLoad });
    await userEvent.click(screen.getByRole("button", { name: /Load/i }));
    expect(onLoad).toHaveBeenCalledOnce();
  });

  it("disables Load button while loading", () => {
    renderLedger({ loading: true });
    expect(screen.getByRole("button")).toBeDisabled();
  });

  it("hides Load button after data is loaded", () => {
    renderLedger({ loaded: true, evidence: [] });
    expect(screen.queryByRole("button", { name: /Load/i })).not.toBeInTheDocument();
  });

  it("shows empty message when loaded with no evidence", () => {
    renderLedger({ loaded: true, evidence: [] });
    expect(screen.getByText(/No evidence recorded/i)).toBeInTheDocument();
  });

  it("renders evidence table with rows", () => {
    const evidence = [
      makeEvidence({ id: "ev-1", phase: "baseline", checkName: "build", passed: true }),
      makeEvidence({ id: "ev-2", phase: "after", checkName: "test", passed: false, tool: "vitest" }),
    ];
    renderLedger({ loaded: true, evidence });

    expect(screen.getByText("build")).toBeInTheDocument();
    expect(screen.getByText("test")).toBeInTheDocument();
    expect(screen.getByText("Pass")).toBeInTheDocument();
    expect(screen.getByText("Fail")).toBeInTheDocument();
    expect(screen.getByText("dotnet")).toBeInTheDocument();
    expect(screen.getByText("vitest")).toBeInTheDocument();
  });

  it("renders table headers", () => {
    renderLedger({ loaded: true, evidence: [makeEvidence()] });
    expect(screen.getByText("Phase")).toBeInTheDocument();
    expect(screen.getByText("Check")).toBeInTheDocument();
    expect(screen.getByText("Result")).toBeInTheDocument();
    expect(screen.getByText("Tool")).toBeInTheDocument();
    expect(screen.getByText("Agent")).toBeInTheDocument();
  });

  it("shows agent name in evidence rows", () => {
    renderLedger({
      loaded: true,
      evidence: [makeEvidence({ agentName: "Athena" })],
    });
    expect(screen.getByText("Athena")).toBeInTheDocument();
  });
});
