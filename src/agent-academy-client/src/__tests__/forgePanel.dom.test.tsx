// @vitest-environment jsdom
import "@testing-library/jest-dom/vitest";
import { describe, expect, it, vi, beforeEach, afterEach } from "vitest";
import { render, screen, cleanup, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { FluentProvider, webDarkTheme } from "@fluentui/react-components";
import ForgePanel from "../ForgePanel";
import type {
  ForgeStatus,
  ForgeJobSummary,
  ForgeRunSummary,
  ForgeRunTrace,
  ForgePhaseRunTrace,
} from "../api";

vi.mock("../api", () => ({
  getForgeStatus: vi.fn(),
  listForgeJobs: vi.fn(),
  listForgeRuns: vi.fn(),
  getForgeRun: vi.fn(),
  getForgeRunPhases: vi.fn(),
  getForgeArtifact: vi.fn(),
  startForgeRun: vi.fn(),
  listMethodologies: vi.fn(),
  getMethodology: vi.fn(),
  saveMethodology: vi.fn(),
}));

import {
  getForgeStatus,
  listForgeJobs,
  listForgeRuns,
  getForgeRun,
  getForgeRunPhases,
  startForgeRun,
  listMethodologies,
  getMethodology,
  saveMethodology,
} from "../api";

const mockGetStatus = vi.mocked(getForgeStatus);
const mockListJobs = vi.mocked(listForgeJobs);
const mockListRuns = vi.mocked(listForgeRuns);
const mockGetRun = vi.mocked(getForgeRun);
const mockGetPhases = vi.mocked(getForgeRunPhases);
const mockStartRun = vi.mocked(startForgeRun);
const mockListMethodologies = vi.mocked(listMethodologies);
const mockGetMethodology = vi.mocked(getMethodology);
const mockSaveMethodology = vi.mocked(saveMethodology);

// ── Factories ──

function makeStatus(overrides: Partial<ForgeStatus> = {}): ForgeStatus {
  return {
    enabled: true,
    executionAvailable: true,
    runsDirectory: "/tmp/forge",
    activeJobs: 0,
    totalJobs: 5,
    completedJobs: 4,
    failedJobs: 1,
    ...overrides,
  };
}

function makeJob(overrides: Partial<ForgeJobSummary> = {}): ForgeJobSummary {
  return {
    jobId: "job-1",
    runId: "R_abc",
    status: "completed",
    createdAt: "2026-04-10T12:00:00Z",
    taskId: "task-1",
    taskTitle: "Build auth module",
    ...overrides,
  };
}

function makeRunSummary(overrides: Partial<ForgeRunSummary> = {}): ForgeRunSummary {
  return {
    runId: "R_abc",
    taskId: "task-1",
    methodologyVersion: "standard/v1",
    outcome: "Succeeded",
    startedAt: "2026-04-10T12:00:00Z",
    endedAt: "2026-04-10T12:05:00Z",
    pipelineCost: 0.42,
    phaseCount: 5,
    fidelityOutcome: "pass",
    ...overrides,
  };
}

function makeRunTrace(overrides: Partial<ForgeRunTrace> = {}): ForgeRunTrace {
  return {
    runId: "R_abc",
    taskId: "task-1",
    methodologyVersion: "standard/v1",
    startedAt: "2026-04-10T12:00:00Z",
    endedAt: "2026-04-10T12:05:00Z",
    outcome: "Succeeded",
    pipelineTokens: { in: 5000, out: 3000 },
    controlTokens: { in: 0, out: 0 },
    pipelineCost: 0.42,
    finalArtifactHashes: { requirements: "sha256-req", contract: "sha256-con" },
    ...overrides,
  };
}

function makePhase(overrides: Partial<ForgePhaseRunTrace> = {}): ForgePhaseRunTrace {
  return {
    phaseId: "requirements",
    artifactType: "requirements/v1",
    stateTransitions: [{ to: "Running", at: "2026-04-10T12:00:00Z" }, { from: "Running", to: "Succeeded", at: "2026-04-10T12:01:00Z" }],
    attempts: [{
      attemptNumber: 1,
      status: "Accepted",
      artifactHash: "sha256-req",
      validatorResults: [],
      tokens: { in: 1000, out: 500 },
      latencyMs: 3200,
      model: "gpt-4o",
      startedAt: "2026-04-10T12:00:00Z",
      endedAt: "2026-04-10T12:00:03Z",
    }],
    inputArtifactHashes: [],
    outputArtifactHashes: ["sha256-req"],
    ...overrides,
  };
}

function renderPanel() {
  return render(
    <FluentProvider theme={webDarkTheme}>
      <ForgePanel />
    </FluentProvider>,
  );
}

describe("ForgePanel", () => {
  beforeEach(() => {
    vi.resetAllMocks();
    vi.useFakeTimers({ shouldAdvanceTime: true });
    // Default: empty methodology catalog (graceful fallback)
    mockListMethodologies.mockResolvedValue([]);
  });

  afterEach(() => {
    vi.useRealTimers();
    cleanup();
    document.body.innerHTML = "";
  });

  // ── List view ──

  it("shows loading spinner initially", () => {
    mockGetStatus.mockReturnValue(new Promise(() => {}));
    mockListJobs.mockReturnValue(new Promise(() => {}));
    mockListRuns.mockReturnValue(new Promise(() => {}));
    renderPanel();
    expect(screen.getByText("Loading forge data…")).toBeInTheDocument();
  });

  it("shows Forge header with Ready badge when enabled", async () => {
    mockGetStatus.mockResolvedValue(makeStatus());
    mockListJobs.mockResolvedValue([]);
    mockListRuns.mockResolvedValue([]);
    renderPanel();
    await waitFor(() => {
      expect(screen.getByText("Forge")).toBeInTheDocument();
    });
    expect(screen.getByText("Ready")).toBeInTheDocument();
  });

  it("shows disabled banner when forge is disabled", async () => {
    mockGetStatus.mockResolvedValue(makeStatus({ enabled: false }));
    mockListJobs.mockResolvedValue([]);
    mockListRuns.mockResolvedValue([]);
    renderPanel();
    await waitFor(() => {
      expect(screen.getByText("Forge engine is disabled")).toBeInTheDocument();
    });
    expect(screen.getByText("Disabled")).toBeInTheDocument();
  });

  it("shows Read-only badge when enabled but execution unavailable", async () => {
    mockGetStatus.mockResolvedValue(makeStatus({ executionAvailable: false }));
    mockListJobs.mockResolvedValue([]);
    mockListRuns.mockResolvedValue([]);
    renderPanel();
    await waitFor(() => {
      expect(screen.getByText("Read-only")).toBeInTheDocument();
    });
  });

  it("shows status cards with job counts", async () => {
    mockGetStatus.mockResolvedValue(makeStatus({ activeJobs: 2, completedJobs: 10, failedJobs: 3, totalJobs: 15 }));
    mockListJobs.mockResolvedValue([]);
    mockListRuns.mockResolvedValue([]);
    renderPanel();
    await waitFor(() => {
      expect(screen.getByText("2")).toBeInTheDocument(); // active
      expect(screen.getByText("10")).toBeInTheDocument(); // completed
      expect(screen.getByText("3")).toBeInTheDocument(); // failed
      expect(screen.getByText("15")).toBeInTheDocument(); // total
    });
  });

  it("shows error message on fetch failure", async () => {
    mockGetStatus.mockRejectedValue(new Error("Network error"));
    mockListJobs.mockRejectedValue(new Error("Network error"));
    mockListRuns.mockRejectedValue(new Error("Network error"));
    renderPanel();
    await waitFor(() => {
      expect(screen.getByText(/Network error/)).toBeInTheDocument();
    });
  });

  it("shows empty state when no runs exist", async () => {
    mockGetStatus.mockResolvedValue(makeStatus());
    mockListJobs.mockResolvedValue([]);
    mockListRuns.mockResolvedValue([]);
    renderPanel();
    await waitFor(() => {
      expect(screen.getByText("No forge runs yet")).toBeInTheDocument();
    });
  });

  it("renders run list with status badges", async () => {
    mockGetStatus.mockResolvedValue(makeStatus());
    mockListJobs.mockResolvedValue([]);
    mockListRuns.mockResolvedValue([
      makeRunSummary({ runId: "R_1", outcome: "Succeeded" }),
      makeRunSummary({ runId: "R_2", outcome: "Failed", fidelityOutcome: undefined }),
    ]);
    renderPanel();
    await waitFor(() => {
      expect(screen.getByText("Succeeded")).toBeInTheDocument();
      expect(screen.getAllByText("Failed").length).toBeGreaterThanOrEqual(1);
    });
  });

  it("renders active jobs section", async () => {
    mockGetStatus.mockResolvedValue(makeStatus({ activeJobs: 1 }));
    mockListJobs.mockResolvedValue([
      makeJob({ jobId: "j-active", status: "running", taskTitle: "Active task" }),
    ]);
    mockListRuns.mockResolvedValue([]);
    renderPanel();
    await waitFor(() => {
      expect(screen.getByText(/Active Jobs/)).toBeInTheDocument();
      expect(screen.getByText("Active task")).toBeInTheDocument();
      expect(screen.getByText("Running")).toBeInTheDocument();
    });
  });

  it("calls refresh on button click", async () => {
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    mockGetStatus.mockResolvedValue(makeStatus());
    mockListJobs.mockResolvedValue([]);
    mockListRuns.mockResolvedValue([]);
    renderPanel();
    await waitFor(() => {
      expect(screen.getByText("Forge")).toBeInTheDocument();
    });

    mockGetStatus.mockResolvedValue(makeStatus({ activeJobs: 1 }));
    await user.click(screen.getByLabelText("Refresh"));
    await waitFor(() => {
      expect(mockGetStatus).toHaveBeenCalledTimes(2);
    });
  });

  // ── Run detail view ──

  it("navigates to run detail on run click", async () => {
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    mockGetStatus.mockResolvedValue(makeStatus());
    mockListJobs.mockResolvedValue([]);
    mockListRuns.mockResolvedValue([makeRunSummary()]);
    renderPanel();

    await waitFor(() => {
      expect(screen.getByText("Succeeded")).toBeInTheDocument();
    });

    mockGetRun.mockResolvedValue(makeRunTrace());
    mockGetPhases.mockResolvedValue([makePhase()]);

    // Click the run row
    await user.click(screen.getByText("Succeeded").closest("[role='button']")!);

    await waitFor(() => {
      expect(screen.getByText("Back to list")).toBeInTheDocument();
      expect(screen.getByText(/Run R_abc/)).toBeInTheDocument();
    });
  });

  it("shows phases in run detail", async () => {
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    mockGetStatus.mockResolvedValue(makeStatus());
    mockListJobs.mockResolvedValue([]);
    mockListRuns.mockResolvedValue([makeRunSummary()]);
    renderPanel();

    await waitFor(() => {
      expect(screen.getByText("Succeeded")).toBeInTheDocument();
    });

    mockGetRun.mockResolvedValue(makeRunTrace());
    mockGetPhases.mockResolvedValue([makePhase()]);

    await user.click(screen.getByText("Succeeded").closest("[role='button']")!);

    await waitFor(() => {
      expect(screen.getByText("requirements")).toBeInTheDocument();
      expect(screen.getAllByText("Phases").length).toBeGreaterThanOrEqual(1);
    });
  });

  it("navigates back to list on back button click", async () => {
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    mockGetStatus.mockResolvedValue(makeStatus());
    mockListJobs.mockResolvedValue([]);
    mockListRuns.mockResolvedValue([makeRunSummary()]);
    renderPanel();

    await waitFor(() => {
      expect(screen.getByText("Succeeded")).toBeInTheDocument();
    });

    mockGetRun.mockResolvedValue(makeRunTrace());
    mockGetPhases.mockResolvedValue([makePhase()]);

    await user.click(screen.getByText("Succeeded").closest("[role='button']")!);

    await waitFor(() => {
      expect(screen.getByText("Back to list")).toBeInTheDocument();
    });

    // Reset mocks for list refresh
    mockGetStatus.mockResolvedValue(makeStatus());
    mockListJobs.mockResolvedValue([]);
    mockListRuns.mockResolvedValue([makeRunSummary()]);

    await user.click(screen.getByText("Back to list"));

    await waitFor(() => {
      expect(screen.getByText("Forge")).toBeInTheDocument();
      expect(screen.queryByText("Back to list")).not.toBeInTheDocument();
    });
  });

  it("shows final artifacts in run detail", async () => {
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    mockGetStatus.mockResolvedValue(makeStatus());
    mockListJobs.mockResolvedValue([]);
    mockListRuns.mockResolvedValue([makeRunSummary()]);
    renderPanel();

    await waitFor(() => {
      expect(screen.getByText("Succeeded")).toBeInTheDocument();
    });

    mockGetRun.mockResolvedValue(makeRunTrace({
      finalArtifactHashes: { requirements: "sha256-abcdef1234567890" },
    }));
    mockGetPhases.mockResolvedValue([]);

    await user.click(screen.getByText("Succeeded").closest("[role='button']")!);

    await waitFor(() => {
      expect(screen.getByText("Final artifacts")).toBeInTheDocument();
      expect(screen.getByText("requirements:")).toBeInTheDocument();
    });
  });

  it("shows run cost and token stats", async () => {
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    mockGetStatus.mockResolvedValue(makeStatus());
    mockListJobs.mockResolvedValue([]);
    mockListRuns.mockResolvedValue([makeRunSummary()]);
    renderPanel();

    await waitFor(() => {
      expect(screen.getByText("Succeeded")).toBeInTheDocument();
    });

    mockGetRun.mockResolvedValue(makeRunTrace({
      pipelineTokens: { in: 5000, out: 3000 },
      pipelineCost: 0.42,
    }));
    mockGetPhases.mockResolvedValue([]);

    await user.click(screen.getByText("Succeeded").closest("[role='button']")!);

    await waitFor(() => {
      expect(screen.getByText("$0.42")).toBeInTheDocument();
      expect(screen.getByText("8.0K")).toBeInTheDocument(); // 5000+3000
    });
  });

  // ── New Run form ──

  it("shows New Run button when execution is available", async () => {
    mockGetStatus.mockResolvedValue(makeStatus({ executionAvailable: true }));
    mockListJobs.mockResolvedValue([]);
    mockListRuns.mockResolvedValue([]);
    renderPanel();
    await waitFor(() => {
      expect(screen.getByLabelText("New run")).toBeInTheDocument();
    });
  });

  it("hides New Run button when execution is unavailable", async () => {
    mockGetStatus.mockResolvedValue(makeStatus({ executionAvailable: false }));
    mockListJobs.mockResolvedValue([]);
    mockListRuns.mockResolvedValue([]);
    renderPanel();
    await waitFor(() => {
      expect(screen.getByText("Forge")).toBeInTheDocument();
    });
    expect(screen.queryByLabelText("New run")).not.toBeInTheDocument();
  });

  it("navigates to new run form on button click", async () => {
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    mockGetStatus.mockResolvedValue(makeStatus());
    mockListJobs.mockResolvedValue([]);
    mockListRuns.mockResolvedValue([]);
    renderPanel();

    await waitFor(() => {
      expect(screen.getByLabelText("New run")).toBeInTheDocument();
    });

    await user.click(screen.getByLabelText("New run"));

    await waitFor(() => {
      expect(screen.getByText(/New Pipeline Run/)).toBeInTheDocument();
      expect(screen.getByLabelText("Title")).toBeInTheDocument();
      expect(screen.getByLabelText("Description")).toBeInTheDocument();
      expect(screen.getByLabelText("Methodology JSON")).toBeInTheDocument();
      expect(screen.getByText("Start Run")).toBeInTheDocument();
    });
  });

  it("validates title is required", async () => {
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    mockGetStatus.mockResolvedValue(makeStatus());
    mockListJobs.mockResolvedValue([]);
    mockListRuns.mockResolvedValue([]);
    renderPanel();

    await waitFor(() => {
      expect(screen.getByLabelText("New run")).toBeInTheDocument();
    });

    await user.click(screen.getByLabelText("New run"));

    await waitFor(() => {
      expect(screen.getByText("Start Run")).toBeInTheDocument();
    });

    await user.click(screen.getByText("Start Run"));

    await waitFor(() => {
      expect(screen.getByText(/Title is required/)).toBeInTheDocument();
    });
    expect(mockStartRun).not.toHaveBeenCalled();
  });

  it("validates description is required", async () => {
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    mockGetStatus.mockResolvedValue(makeStatus());
    mockListJobs.mockResolvedValue([]);
    mockListRuns.mockResolvedValue([]);
    renderPanel();

    await waitFor(() => {
      expect(screen.getByLabelText("New run")).toBeInTheDocument();
    });

    await user.click(screen.getByLabelText("New run"));

    await waitFor(() => {
      expect(screen.getByText("Start Run")).toBeInTheDocument();
    });

    await user.type(screen.getByLabelText("Title"), "My task");
    await user.click(screen.getByText("Start Run"));

    await waitFor(() => {
      expect(screen.getByText(/Description is required/)).toBeInTheDocument();
    });
    expect(mockStartRun).not.toHaveBeenCalled();
  });

  it("validates methodology JSON syntax", async () => {
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    mockGetStatus.mockResolvedValue(makeStatus());
    mockListJobs.mockResolvedValue([]);
    mockListRuns.mockResolvedValue([]);
    renderPanel();

    await waitFor(() => {
      expect(screen.getByLabelText("New run")).toBeInTheDocument();
    });

    await user.click(screen.getByLabelText("New run"));

    await waitFor(() => {
      expect(screen.getByText("Start Run")).toBeInTheDocument();
    });

    await user.type(screen.getByLabelText("Title"), "My task");
    await user.type(screen.getByLabelText("Description"), "Do the thing");

    // Clear methodology and type invalid JSON
    const methodologyInput = screen.getByLabelText("Methodology JSON");
    await user.clear(methodologyInput);
    await user.type(methodologyInput, "not valid json");

    await user.click(screen.getByText("Start Run"));

    await waitFor(() => {
      expect(screen.getByText(/Methodology JSON is invalid/)).toBeInTheDocument();
    });
    expect(mockStartRun).not.toHaveBeenCalled();
  });

  it("submits new run and returns to list", async () => {
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    mockGetStatus.mockResolvedValue(makeStatus());
    mockListJobs.mockResolvedValue([]);
    mockListRuns.mockResolvedValue([]);
    renderPanel();

    await waitFor(() => {
      expect(screen.getByLabelText("New run")).toBeInTheDocument();
    });

    await user.click(screen.getByLabelText("New run"));

    await waitFor(() => {
      expect(screen.getByText("Start Run")).toBeInTheDocument();
    });

    await user.type(screen.getByLabelText("Title"), "Build auth module");
    await user.type(screen.getByLabelText("Description"), "Implement JWT authentication");

    mockStartRun.mockResolvedValue({ jobId: "j1", status: "queued", createdAt: "2026-04-21T00:00:00Z", taskId: "t1" });
    mockGetStatus.mockResolvedValue(makeStatus({ activeJobs: 1 }));
    mockListJobs.mockResolvedValue([makeJob({ status: "queued", taskTitle: "Build auth module" })]);
    mockListRuns.mockResolvedValue([]);

    await user.click(screen.getByText("Start Run"));

    await waitFor(() => {
      expect(mockStartRun).toHaveBeenCalledTimes(1);
      expect(screen.getByText("Forge")).toBeInTheDocument();
      expect(screen.queryByText(/New Pipeline Run/)).not.toBeInTheDocument();
    });
  });

  it("shows API error on submission failure", async () => {
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    mockGetStatus.mockResolvedValue(makeStatus());
    mockListJobs.mockResolvedValue([]);
    mockListRuns.mockResolvedValue([]);
    renderPanel();

    await waitFor(() => {
      expect(screen.getByLabelText("New run")).toBeInTheDocument();
    });

    await user.click(screen.getByLabelText("New run"));

    await waitFor(() => {
      expect(screen.getByText("Start Run")).toBeInTheDocument();
    });

    await user.type(screen.getByLabelText("Title"), "Fail task");
    await user.type(screen.getByLabelText("Description"), "This will fail");

    mockStartRun.mockRejectedValue(new Error("Forge execution unavailable"));

    await user.click(screen.getByText("Start Run"));

    await waitFor(() => {
      expect(screen.getByText(/Forge execution unavailable/)).toBeInTheDocument();
    });
  });

  it("cancels new run form and returns to list", async () => {
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    mockGetStatus.mockResolvedValue(makeStatus());
    mockListJobs.mockResolvedValue([]);
    mockListRuns.mockResolvedValue([]);
    renderPanel();

    await waitFor(() => {
      expect(screen.getByLabelText("New run")).toBeInTheDocument();
    });

    await user.click(screen.getByLabelText("New run"));

    await waitFor(() => {
      expect(screen.getByText("Cancel")).toBeInTheDocument();
    });

    await user.click(screen.getByText("Cancel"));

    await waitFor(() => {
      expect(screen.getByText("Forge")).toBeInTheDocument();
      expect(screen.queryByText(/New Pipeline Run/)).not.toBeInTheDocument();
    });
  });

  // ── Methodology catalog ──

  it("shows methodology selector in new run form", async () => {
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    mockGetStatus.mockResolvedValue(makeStatus());
    mockListJobs.mockResolvedValue([]);
    mockListRuns.mockResolvedValue([]);
    mockListMethodologies.mockResolvedValue([
      { id: "spike-default-v1", description: "Five phases", phaseCount: 5, hasBudget: false, hasFidelity: false, hasControl: false },
      { id: "fast-v1", description: "Quick run", phaseCount: 2, hasBudget: false, hasFidelity: false, hasControl: false },
    ]);
    renderPanel();

    await waitFor(() => {
      expect(screen.getByLabelText("New run")).toBeInTheDocument();
    });

    await user.click(screen.getByLabelText("New run"));

    await waitFor(() => {
      expect(screen.getByLabelText("Methodology")).toBeInTheDocument();
    });

    // Catalog options should appear
    await waitFor(() => {
      const select = screen.getByLabelText("Methodology") as HTMLSelectElement;
      expect(select.options.length).toBe(3); // "Custom" + 2 methodologies
    });
  });

  it("loads methodology JSON when selecting from catalog", async () => {
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    mockGetStatus.mockResolvedValue(makeStatus());
    mockListJobs.mockResolvedValue([]);
    mockListRuns.mockResolvedValue([]);
    mockListMethodologies.mockResolvedValue([
      { id: "spike-default-v1", description: "Five phases", phaseCount: 5, hasBudget: false, hasFidelity: false, hasControl: false },
    ]);
    mockGetMethodology.mockResolvedValue({
      id: "spike-default-v1",
      description: "Five phases",
      phases: [{ id: "req", goal: "g", inputs: [], output_schema: "r/v1", instructions: "i" }],
    } as never);
    renderPanel();

    await waitFor(() => {
      expect(screen.getByLabelText("New run")).toBeInTheDocument();
    });

    await user.click(screen.getByLabelText("New run"));

    await waitFor(() => {
      expect(screen.getByLabelText("Methodology")).toBeInTheDocument();
    });

    // Wait for catalog to load
    await waitFor(() => {
      const select = screen.getByLabelText("Methodology") as HTMLSelectElement;
      expect(select.options.length).toBe(2);
    });

    await user.selectOptions(screen.getByLabelText("Methodology"), "spike-default-v1");

    await waitFor(() => {
      expect(mockGetMethodology).toHaveBeenCalledWith("spike-default-v1");
    });
  });

  it("shows Save as Template button in new run form", async () => {
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    mockGetStatus.mockResolvedValue(makeStatus());
    mockListJobs.mockResolvedValue([]);
    mockListRuns.mockResolvedValue([]);
    renderPanel();

    await waitFor(() => {
      expect(screen.getByLabelText("New run")).toBeInTheDocument();
    });

    await user.click(screen.getByLabelText("New run"));

    await waitFor(() => {
      expect(screen.getByText(/Save as Template/)).toBeInTheDocument();
    });
  });

  it("gracefully handles methodology catalog failure", async () => {
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    mockGetStatus.mockResolvedValue(makeStatus());
    mockListJobs.mockResolvedValue([]);
    mockListRuns.mockResolvedValue([]);
    mockListMethodologies.mockRejectedValue(new Error("Server error"));
    renderPanel();

    await waitFor(() => {
      expect(screen.getByLabelText("New run")).toBeInTheDocument();
    });

    await user.click(screen.getByLabelText("New run"));

    // Form should still render with Custom option only
    await waitFor(() => {
      expect(screen.getByText(/New Pipeline Run/)).toBeInTheDocument();
      const select = screen.getByLabelText("Methodology") as HTMLSelectElement;
      expect(select.options.length).toBe(1); // Just "Custom"
    });
  });
});
