import { describe, expect, it, vi, beforeEach } from "vitest";
import {
  getForgeStatus,
  listForgeJobs,
  getForgeJob,
  listForgeRuns,
  getForgeRun,
  getForgeRunPhases,
  getForgeArtifact,
  listForgeSchemas,
  startForgeRun,
} from "../api/forge";

vi.mock("../api/core", () => ({
  apiUrl: (path: string) => `http://test${path}`,
  request: vi.fn(),
}));

import { request } from "../api/core";
const mockRequest = vi.mocked(request);

beforeEach(() => {
  vi.resetAllMocks();
});

describe("forge API", () => {
  describe("getForgeStatus", () => {
    it("calls correct URL", async () => {
      mockRequest.mockResolvedValue({ enabled: true });
      await getForgeStatus();
      expect(mockRequest).toHaveBeenCalledWith("http://test/api/forge/status");
    });

    it("returns the status response", async () => {
      const expected = { enabled: true, executionAvailable: true, activeJobs: 0, totalJobs: 5, completedJobs: 4, failedJobs: 1, runsDirectory: "/tmp" };
      mockRequest.mockResolvedValue(expected);
      const result = await getForgeStatus();
      expect(result).toBe(expected);
    });
  });

  describe("listForgeJobs", () => {
    it("calls correct URL", async () => {
      mockRequest.mockResolvedValue([]);
      await listForgeJobs();
      expect(mockRequest).toHaveBeenCalledWith("http://test/api/forge/jobs");
    });

    it("returns the jobs array", async () => {
      const expected = [{ jobId: "j1", status: "Completed" }];
      mockRequest.mockResolvedValue(expected);
      const result = await listForgeJobs();
      expect(result).toBe(expected);
    });
  });

  describe("getForgeJob", () => {
    it("calls correct URL with jobId", async () => {
      mockRequest.mockResolvedValue({ jobId: "j1" });
      await getForgeJob("j1");
      expect(mockRequest).toHaveBeenCalledWith("http://test/api/forge/jobs/j1");
    });

    it("encodes special characters in jobId", async () => {
      mockRequest.mockResolvedValue({ jobId: "a/b" });
      await getForgeJob("a/b");
      expect(mockRequest).toHaveBeenCalledWith("http://test/api/forge/jobs/a%2Fb");
    });
  });

  describe("listForgeRuns", () => {
    it("calls correct URL", async () => {
      mockRequest.mockResolvedValue([]);
      await listForgeRuns();
      expect(mockRequest).toHaveBeenCalledWith("http://test/api/forge/runs");
    });
  });

  describe("getForgeRun", () => {
    it("calls correct URL with runId", async () => {
      mockRequest.mockResolvedValue({ runId: "R_abc" });
      await getForgeRun("R_abc");
      expect(mockRequest).toHaveBeenCalledWith("http://test/api/forge/runs/R_abc");
    });
  });

  describe("getForgeRunPhases", () => {
    it("calls correct URL with runId", async () => {
      mockRequest.mockResolvedValue([]);
      await getForgeRunPhases("R_abc");
      expect(mockRequest).toHaveBeenCalledWith("http://test/api/forge/runs/R_abc/phases");
    });
  });

  describe("getForgeArtifact", () => {
    it("calls correct URL with hash", async () => {
      mockRequest.mockResolvedValue({ artifact: {}, meta: {} });
      await getForgeArtifact("sha256-abc123");
      expect(mockRequest).toHaveBeenCalledWith("http://test/api/forge/artifacts/sha256-abc123");
    });
  });

  describe("listForgeSchemas", () => {
    it("calls correct URL", async () => {
      mockRequest.mockResolvedValue([]);
      await listForgeSchemas();
      expect(mockRequest).toHaveBeenCalledWith("http://test/api/forge/schemas");
    });
  });

  describe("startForgeRun", () => {
    it("calls correct URL with POST method and body", async () => {
      const response = { jobId: "j1", status: "queued", createdAt: "2026-04-21T00:00:00Z", taskId: "t1" };
      mockRequest.mockResolvedValue(response);

      const req = {
        title: "Build auth",
        description: "Implement JWT auth module",
        methodology: {
          id: "test-v1",
          phases: [{ id: "req", goal: "Requirements", inputs: [], output_schema: "requirements/v1", instructions: "Do it" }],
        },
      };

      const result = await startForgeRun(req);
      expect(result).toBe(response);
      expect(mockRequest).toHaveBeenCalledWith("http://test/api/forge/jobs", {
        method: "POST",
        body: JSON.stringify(req),
      });
    });

    it("passes optional taskId when provided", async () => {
      mockRequest.mockResolvedValue({ jobId: "j1", status: "queued", createdAt: "2026-04-21T00:00:00Z", taskId: "custom-id" });

      const req = {
        taskId: "custom-id",
        title: "Test task",
        description: "Test description",
        methodology: { id: "m1", phases: [] },
      };

      await startForgeRun(req);
      const body = JSON.parse((mockRequest.mock.calls[0][1] as RequestInit).body as string);
      expect(body.taskId).toBe("custom-id");
    });
  });
});
