// @vitest-environment jsdom
/**
 * DOM tests for ArtifactList.
 *
 * Covers: empty state, artifact rendering with badges, content
 * truncation + expand/collapse, markdown rendering, agent badge.
 */
import "@testing-library/jest-dom/vitest";
import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { createElement } from "react";
import type { SprintArtifact } from "../api";

vi.mock("react-markdown", () => ({
  default: ({ children }: any) => createElement("div", { "data-testid": "markdown" }, children),
}));
vi.mock("remark-gfm", () => ({
  default: {},
}));
vi.mock("@fluentui/react-components", () => ({
  makeStyles: () => () => ({
    detailSection: "",
    sectionTitle: "",
    artifactCard: "",
    artifactHeader: "",
    artifactTitle: "",
    artifactMeta: "",
    artifactContent: "",
    empty: "",
    expandToggle: "",
  }),
  shorthands: new Proxy({}, { get: () => () => ({}) }),
}));
vi.mock("../V3Badge", () => ({
  default: ({ children, color }: any) =>
    createElement("span", { "data-testid": `badge-${color}` }, children),
}));
vi.mock("../sprint/sprintConstants", () => ({
  STAGE_META: {
    Intake: { label: "Intake", icon: "📥", description: "..." },
    Planning: { label: "Planning", icon: "📋", description: "..." },
    Discussion: { label: "Discussion", icon: "💬", description: "..." },
    Validation: { label: "Validation", icon: "✅", description: "..." },
    Implementation: { label: "Implementation", icon: "🔨", description: "..." },
  },
  artifactTypeLabel: (type: string) =>
    type === "RequirementsDoc"
      ? "Requirements Doc"
      : type === "SprintPlan"
        ? "Sprint Plan"
        : type,
}));
vi.mock("../panelUtils", () => ({
  formatElapsed: () => "2h ago",
}));

import ArtifactList from "../sprint/ArtifactList";

afterEach(cleanup);

function makeArtifact(overrides: Partial<SprintArtifact> = {}): SprintArtifact {
  return {
    id: 1,
    sprintId: "sprint-1",
    stage: "Planning",
    type: "SprintPlan" as any,
    content: "Short content",
    createdByAgentId: null,
    createdAt: "2026-04-15T10:00:00Z",
    updatedAt: null,
    ...overrides,
  };
}

describe("ArtifactList", () => {
  it("shows empty state when no artifacts", () => {
    render(
      createElement(ArtifactList, {
        selectedStage: "Planning",
        artifacts: [],
      }),
    );
    expect(screen.getByText(/no artifacts for this stage/i)).toBeInTheDocument();
  });

  it("renders stage label in section title", () => {
    render(
      createElement(ArtifactList, {
        selectedStage: "Planning",
        artifacts: [],
      }),
    );
    expect(screen.getByText(/planning artifacts/i)).toBeInTheDocument();
  });

  it("renders artifact card with type label and stage badge", () => {
    render(
      createElement(ArtifactList, {
        selectedStage: "Planning",
        artifacts: [makeArtifact()],
      }),
    );
    expect(screen.getByText("Sprint Plan")).toBeInTheDocument();
    expect(screen.getByTestId("badge-info")).toHaveTextContent("Planning");
  });

  it("renders agent badge when createdByAgentId is present", () => {
    render(
      createElement(ArtifactList, {
        selectedStage: "Planning",
        artifacts: [makeArtifact({ createdByAgentId: "agent-alpha" })],
      }),
    );
    expect(screen.getByTestId("badge-tool")).toHaveTextContent("agent-alpha");
  });

  it("does not render agent badge when createdByAgentId is null", () => {
    render(
      createElement(ArtifactList, {
        selectedStage: "Planning",
        artifacts: [makeArtifact({ createdByAgentId: null })],
      }),
    );
    expect(screen.queryByTestId("badge-tool")).not.toBeInTheDocument();
  });

  it("shows elapsed time", () => {
    render(
      createElement(ArtifactList, {
        selectedStage: "Planning",
        artifacts: [makeArtifact()],
      }),
    );
    expect(screen.getByText("2h ago")).toBeInTheDocument();
  });

  it("truncates long content and shows expand toggle", () => {
    const longContent = "A".repeat(250);
    render(
      createElement(ArtifactList, {
        selectedStage: "Planning",
        artifacts: [makeArtifact({ content: longContent })],
      }),
    );
    expect(screen.getByText("Show full content")).toBeInTheDocument();
    // Content should be truncated to 200 chars + ellipsis
    const markdowns = screen.getAllByTestId("markdown");
    expect(markdowns[0].textContent!.length).toBeLessThan(longContent.length);
  });

  it("expands content on toggle click", async () => {
    const longContent = "B".repeat(250);
    render(
      createElement(ArtifactList, {
        selectedStage: "Planning",
        artifacts: [makeArtifact({ content: longContent })],
      }),
    );

    await userEvent.click(screen.getByText("Show full content"));
    expect(screen.getByText("Collapse")).toBeInTheDocument();
    const markdowns = screen.getAllByTestId("markdown");
    expect(markdowns[0].textContent).toBe(longContent);
  });

  it("collapses back after expanding", async () => {
    const longContent = "C".repeat(250);
    render(
      createElement(ArtifactList, {
        selectedStage: "Planning",
        artifacts: [makeArtifact({ content: longContent })],
      }),
    );

    await userEvent.click(screen.getByText("Show full content"));
    expect(screen.getByText("Collapse")).toBeInTheDocument();

    await userEvent.click(screen.getByText("Collapse"));
    expect(screen.getByText("Show full content")).toBeInTheDocument();
  });

  it("does not show expand toggle for short content", () => {
    render(
      createElement(ArtifactList, {
        selectedStage: "Planning",
        artifacts: [makeArtifact({ content: "Short" })],
      }),
    );
    expect(screen.queryByText("Show full content")).not.toBeInTheDocument();
    expect(screen.queryByText("Collapse")).not.toBeInTheDocument();
  });

  it("renders multiple artifacts", () => {
    render(
      createElement(ArtifactList, {
        selectedStage: "Planning",
        artifacts: [
          makeArtifact({ id: 1, type: "SprintPlan" as any }),
          makeArtifact({ id: 2, type: "RequirementsDoc" as any }),
        ],
      }),
    );
    expect(screen.getByText("Sprint Plan")).toBeInTheDocument();
    expect(screen.getByText("Requirements Doc")).toBeInTheDocument();
  });
});
