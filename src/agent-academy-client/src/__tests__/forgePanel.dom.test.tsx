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
}));

import {
  getForgeStatus,
  listForgeJobs,
  listForgeRuns,
  getForgeRun,
  getForgeRunPhases,
} from "../api";

const mockGetStatus = vi.mocked(getForgeStatus);
const mockListJobs = vi.mocked(listForgeJobs);
const mockListRuns = vi.mocked(listForgeRuns);
const mockGetRun = vi.mocked(getForgeRun);
const mockGetPhases = vi.mocked(getForgeRunPhases);

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
});
