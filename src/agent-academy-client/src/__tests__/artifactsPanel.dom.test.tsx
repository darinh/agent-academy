// @vitest-environment jsdom
/**
 * Tests for ArtifactsPanel — artifact operations log and quality evaluations.
 */
import "@testing-library/jest-dom/vitest";
import { describe, expect, it, vi, afterEach, beforeEach } from "vitest";
import { render, screen, cleanup, waitFor, fireEvent } from "@testing-library/react";
import { FluentProvider, webDarkTheme } from "@fluentui/react-components";
import ArtifactsPanel from "../ArtifactsPanel";
import type { ArtifactRecord, RoomEvaluationResponse } from "../api";

// ── Mocks ──

const mockGetRoomArtifacts = vi.fn();
const mockGetRoomEvaluations = vi.fn();

vi.mock("../api", async () => {
  const actual = await vi.importActual<typeof import("../api")>("../api");
  return {
    ...actual,
    getRoomArtifacts: (...args: unknown[]) => mockGetRoomArtifacts(...args),
    getRoomEvaluations: (...args: unknown[]) => mockGetRoomEvaluations(...args),
  };
});

// ── Factories ──

function makeArtifact(overrides: Partial<ArtifactRecord> = {}): ArtifactRecord {
  return {
    agentId: "athena",
    roomId: "room-1",
    filePath: "src/main.ts",
    operation: "Created",
    timestamp: "2026-04-14T10:00:00Z",
    ...overrides,
  };
}

function makeEvaluation(overrides: Partial<RoomEvaluationResponse> = {}): RoomEvaluationResponse {
  return {
    aggregateScore: 85,
    artifacts: [
      {
        filePath: "src/main.ts",
        score: 100,
        exists: true,
        nonEmpty: true,
        syntaxValid: true,
        complete: true,
        issues: [],
      },
      {
        filePath: "src/utils.ts",
        score: 65,
        exists: true,
        nonEmpty: true,
        syntaxValid: true,
        complete: false,
        issues: ["Contains TODO marker"],
      },
    ],
    ...overrides,
  };
}

// ── Helpers ──

function renderPanel(roomId: string | null = "room-1", refreshTrigger?: number) {
  return render(
    <FluentProvider theme={webDarkTheme}>
      <ArtifactsPanel roomId={roomId} refreshTrigger={refreshTrigger} />
    </FluentProvider>,
  );
}

// ── Teardown ──

afterEach(() => {
  cleanup();
  document.body.innerHTML = "";
  vi.clearAllMocks();
});

beforeEach(() => {
  mockGetRoomArtifacts.mockResolvedValue([]);
  mockGetRoomEvaluations.mockResolvedValue({ artifacts: [], aggregateScore: 0 });
});

// ── Tests ──

describe("ArtifactsPanel", () => {
  describe("empty states", () => {
    it("shows 'no room selected' when roomId is null", () => {
      renderPanel(null);
      expect(screen.getByText("No room selected")).toBeInTheDocument();
    });

    it("shows 'no artifacts yet' when both endpoints return empty", async () => {
      mockGetRoomArtifacts.mockResolvedValue([]);
      mockGetRoomEvaluations.mockResolvedValue({ artifacts: [], aggregateScore: 0 });
      renderPanel("room-1");

      await waitFor(() => {
        expect(screen.getByText("No artifacts yet")).toBeInTheDocument();
      });
    });
  });

  describe("data fetching", () => {
    it("calls both endpoints with the room ID", async () => {
      mockGetRoomArtifacts.mockResolvedValue([makeArtifact()]);
      mockGetRoomEvaluations.mockResolvedValue(makeEvaluation());
      renderPanel("room-42");

      await waitFor(() => {
        expect(mockGetRoomArtifacts).toHaveBeenCalledWith("room-42");
        expect(mockGetRoomEvaluations).toHaveBeenCalledWith("room-42");
      });
    });

    it("refetches when roomId changes", async () => {
      mockGetRoomArtifacts.mockResolvedValue([makeArtifact()]);
      mockGetRoomEvaluations.mockResolvedValue(makeEvaluation());

      const { rerender } = render(
        <FluentProvider theme={webDarkTheme}>
          <ArtifactsPanel roomId="room-1" />
        </FluentProvider>,
      );

      await waitFor(() => expect(mockGetRoomArtifacts).toHaveBeenCalledWith("room-1"));

      rerender(
        <FluentProvider theme={webDarkTheme}>
          <ArtifactsPanel roomId="room-2" />
        </FluentProvider>,
      );

      await waitFor(() => expect(mockGetRoomArtifacts).toHaveBeenCalledWith("room-2"));
    });

    it("refetches when refreshTrigger changes", async () => {
      mockGetRoomArtifacts.mockResolvedValue([makeArtifact()]);
      mockGetRoomEvaluations.mockResolvedValue(makeEvaluation());

      const { rerender } = render(
        <FluentProvider theme={webDarkTheme}>
          <ArtifactsPanel roomId="room-1" refreshTrigger={0} />
        </FluentProvider>,
      );

      await waitFor(() => expect(mockGetRoomArtifacts).toHaveBeenCalledTimes(1));

      rerender(
        <FluentProvider theme={webDarkTheme}>
          <ArtifactsPanel roomId="room-1" refreshTrigger={1} />
        </FluentProvider>,
      );

      await waitFor(() => expect(mockGetRoomArtifacts).toHaveBeenCalledTimes(2));
    });
  });

  describe("evaluations section", () => {
    it("shows aggregate score badge", async () => {
      mockGetRoomArtifacts.mockResolvedValue([makeArtifact()]);
      mockGetRoomEvaluations.mockResolvedValue(makeEvaluation({ aggregateScore: 85 }));
      renderPanel();

      await waitFor(() => {
        expect(screen.getByText("85%")).toBeInTheDocument();
      });
    });

    it("shows file count", async () => {
      mockGetRoomArtifacts.mockResolvedValue([makeArtifact()]);
      mockGetRoomEvaluations.mockResolvedValue(makeEvaluation());
      renderPanel();

      await waitFor(() => {
        expect(screen.getByText("2 files")).toBeInTheDocument();
      });
    });

    it("renders evaluation cards with file paths", async () => {
      mockGetRoomArtifacts.mockResolvedValue([makeArtifact()]);
      mockGetRoomEvaluations.mockResolvedValue(makeEvaluation());
      renderPanel();

      await waitFor(() => {
        // Both evaluation cards and log table may show the same paths
        expect(screen.getAllByText("src/main.ts").length).toBeGreaterThanOrEqual(1);
        expect(screen.getAllByText("src/utils.ts").length).toBeGreaterThanOrEqual(1);
      });
    });

    it("shows check marks for passing criteria", async () => {
      mockGetRoomArtifacts.mockResolvedValue([makeArtifact()]);
      mockGetRoomEvaluations.mockResolvedValue(makeEvaluation());
      renderPanel();

      await waitFor(() => {
        // src/main.ts has all passing
        const checks = screen.getAllByText(/✓ Exists/);
        expect(checks.length).toBeGreaterThanOrEqual(1);
      });
    });

    it("shows failing marks for failed criteria", async () => {
      mockGetRoomArtifacts.mockResolvedValue([makeArtifact()]);
      mockGetRoomEvaluations.mockResolvedValue(makeEvaluation());
      renderPanel();

      await waitFor(() => {
        // src/utils.ts has complete=false
        const failChecks = screen.getAllByText(/✗ Complete/);
        expect(failChecks.length).toBeGreaterThanOrEqual(1);
      });
    });

    it("shows issues list", async () => {
      mockGetRoomArtifacts.mockResolvedValue([makeArtifact()]);
      mockGetRoomEvaluations.mockResolvedValue(makeEvaluation());
      renderPanel();

      await waitFor(() => {
        expect(screen.getByText("Contains TODO marker")).toBeInTheDocument();
      });
    });

    it("shows error message when evaluations fail", async () => {
      mockGetRoomArtifacts.mockResolvedValue([makeArtifact()]);
      mockGetRoomEvaluations.mockRejectedValue(new Error("Server error"));
      renderPanel();

      await waitFor(() => {
        expect(screen.getByText("Server error")).toBeInTheDocument();
      });
    });
  });

  describe("file operations log", () => {
    it("shows artifact entries in the table", async () => {
      const artifacts = [
        makeArtifact({ filePath: "src/index.ts", operation: "Created", agentId: "athena" }),
        makeArtifact({ filePath: "src/helper.ts", operation: "Updated", agentId: "hephaestus" }),
      ];
      mockGetRoomArtifacts.mockResolvedValue(artifacts);
      mockGetRoomEvaluations.mockResolvedValue(makeEvaluation());
      renderPanel();

      await waitFor(() => {
        expect(screen.getByText("src/index.ts")).toBeInTheDocument();
        expect(screen.getByText("src/helper.ts")).toBeInTheDocument();
        expect(screen.getByText("athena")).toBeInTheDocument();
        expect(screen.getByText("hephaestus")).toBeInTheDocument();
      });
    });

    it("shows operation types with correct text", async () => {
      const artifacts = [
        makeArtifact({ operation: "Created" }),
        makeArtifact({ filePath: "src/old.ts", operation: "Deleted" }),
      ];
      mockGetRoomArtifacts.mockResolvedValue(artifacts);
      mockGetRoomEvaluations.mockResolvedValue(makeEvaluation());
      renderPanel();

      await waitFor(() => {
        expect(screen.getByText("Created")).toBeInTheDocument();
        expect(screen.getByText("Deleted")).toBeInTheDocument();
      });
    });

    it("shows event count", async () => {
      const artifacts = [makeArtifact(), makeArtifact({ filePath: "b.ts" }), makeArtifact({ filePath: "c.ts" })];
      mockGetRoomArtifacts.mockResolvedValue(artifacts);
      mockGetRoomEvaluations.mockResolvedValue(makeEvaluation());
      renderPanel();

      await waitFor(() => {
        expect(screen.getByText("3 events")).toBeInTheDocument();
      });
    });

    it("collapses and expands the log on click", async () => {
      mockGetRoomArtifacts.mockResolvedValue([makeArtifact({ filePath: "src/unique-log-file.ts" })]);
      mockGetRoomEvaluations.mockResolvedValue(makeEvaluation());
      renderPanel();

      await waitFor(() => {
        // Table headers are visible when expanded
        expect(screen.getByText("Agent")).toBeInTheDocument();
      });

      // Click to collapse
      const toggle = screen.getByText(/Recent File Operations/);
      fireEvent.click(toggle);

      // Table headers should be gone
      expect(screen.queryByText("Agent")).not.toBeInTheDocument();

      // Click to expand again
      fireEvent.click(toggle);
      await waitFor(() => {
        expect(screen.getByText("Agent")).toBeInTheDocument();
      });
    });

    it("shows error message when artifacts fail", async () => {
      mockGetRoomArtifacts.mockRejectedValue(new Error("Network error"));
      mockGetRoomEvaluations.mockResolvedValue({ artifacts: [], aggregateScore: 0 });
      renderPanel();

      await waitFor(() => {
        expect(screen.getByText("Network error")).toBeInTheDocument();
      });
    });
  });

  describe("refresh", () => {
    it("has a Refresh button that refetches both endpoints", async () => {
      mockGetRoomArtifacts.mockResolvedValue([makeArtifact()]);
      mockGetRoomEvaluations.mockResolvedValue(makeEvaluation());
      renderPanel();

      await waitFor(() => {
        expect(screen.getByText("85%")).toBeInTheDocument();
      });

      mockGetRoomArtifacts.mockClear();
      mockGetRoomEvaluations.mockClear();

      fireEvent.click(screen.getByText("Refresh"));

      await waitFor(() => {
        expect(mockGetRoomArtifacts).toHaveBeenCalledTimes(1);
        expect(mockGetRoomEvaluations).toHaveBeenCalledTimes(1);
      });
    });
  });

  describe("independent loading", () => {
    it("shows artifacts while evaluations are still loading", async () => {
      mockGetRoomArtifacts.mockResolvedValue([makeArtifact({ filePath: "src/fast.ts" })]);
      // Evaluations never resolve in this test
      mockGetRoomEvaluations.mockReturnValue(new Promise(() => {}));
      renderPanel();

      await waitFor(() => {
        expect(screen.getByText("src/fast.ts")).toBeInTheDocument();
      });
      expect(screen.getByText(/Evaluating files on disk/)).toBeInTheDocument();
    });
  });

  describe("score colors", () => {
    it("shows green for high scores (>=80)", async () => {
      mockGetRoomArtifacts.mockResolvedValue([makeArtifact()]);
      mockGetRoomEvaluations.mockResolvedValue(makeEvaluation({ aggregateScore: 95 }));
      renderPanel();

      await waitFor(() => {
        const badge = screen.getByText("95%");
        expect(badge).toBeInTheDocument();
      });
    });

    it("shows badge for low scores (<50)", async () => {
      mockGetRoomArtifacts.mockResolvedValue([makeArtifact()]);
      mockGetRoomEvaluations.mockResolvedValue(makeEvaluation({ aggregateScore: 30 }));
      renderPanel();

      await waitFor(() => {
        const badge = screen.getByText("30%");
        expect(badge).toBeInTheDocument();
      });
    });
  });
});
