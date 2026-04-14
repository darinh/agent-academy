import { describe, expect, it, vi, beforeEach } from "vitest";
import { listRetrospectives, getRetrospective, getRetrospectiveStats } from "../api/retrospectives";

vi.mock("../api/core", () => ({
  apiUrl: (path: string) => `http://test${path}`,
  request: vi.fn(),
}));

import { request } from "../api/core";
const mockRequest = vi.mocked(request);

beforeEach(() => {
  vi.resetAllMocks();
});

describe("retrospectives API", () => {
  describe("listRetrospectives", () => {
    it("calls correct URL with no params", async () => {
      mockRequest.mockResolvedValue({ retrospectives: [], total: 0, limit: 20, offset: 0 });
      await listRetrospectives();
      expect(mockRequest).toHaveBeenCalledWith("http://test/api/retrospectives");
    });

    it("includes agentId in query string", async () => {
      mockRequest.mockResolvedValue({ retrospectives: [], total: 0, limit: 20, offset: 0 });
      await listRetrospectives({ agentId: "hephaestus" });
      const url = mockRequest.mock.calls[0][0] as string;
      expect(url).toContain("agentId=hephaestus");
    });

    it("includes taskId in query string", async () => {
      mockRequest.mockResolvedValue({ retrospectives: [], total: 0, limit: 20, offset: 0 });
      await listRetrospectives({ taskId: "task-42" });
      const url = mockRequest.mock.calls[0][0] as string;
      expect(url).toContain("taskId=task-42");
    });

    it("includes both agentId and taskId when combined", async () => {
      mockRequest.mockResolvedValue({ retrospectives: [], total: 0, limit: 20, offset: 0 });
      await listRetrospectives({ agentId: "athena", taskId: "task-99" });
      const url = mockRequest.mock.calls[0][0] as string;
      expect(url).toContain("agentId=athena");
      expect(url).toContain("taskId=task-99");
    });

    it("includes limit and offset in query string", async () => {
      mockRequest.mockResolvedValue({ retrospectives: [], total: 0, limit: 10, offset: 20 });
      await listRetrospectives({ limit: 10, offset: 20 });
      const url = mockRequest.mock.calls[0][0] as string;
      expect(url).toContain("limit=10");
      expect(url).toContain("offset=20");
    });

    it("omits undefined agentId from query string", async () => {
      mockRequest.mockResolvedValue({ retrospectives: [], total: 0, limit: 20, offset: 0 });
      await listRetrospectives({ agentId: undefined });
      const url = mockRequest.mock.calls[0][0] as string;
      expect(url).not.toContain("agentId");
    });

    it("omits empty string agentId from query string", async () => {
      mockRequest.mockResolvedValue({ retrospectives: [], total: 0, limit: 20, offset: 0 });
      await listRetrospectives({ agentId: "" });
      const url = mockRequest.mock.calls[0][0] as string;
      expect(url).not.toContain("agentId");
    });

    it("omits undefined taskId from query string", async () => {
      mockRequest.mockResolvedValue({ retrospectives: [], total: 0, limit: 20, offset: 0 });
      await listRetrospectives({ taskId: undefined });
      const url = mockRequest.mock.calls[0][0] as string;
      expect(url).not.toContain("taskId");
    });

    it("omits empty string taskId from query string", async () => {
      mockRequest.mockResolvedValue({ retrospectives: [], total: 0, limit: 20, offset: 0 });
      await listRetrospectives({ taskId: "" });
      const url = mockRequest.mock.calls[0][0] as string;
      expect(url).not.toContain("taskId");
    });

    it("includes limit=0 in query string", async () => {
      mockRequest.mockResolvedValue({ retrospectives: [], total: 0, limit: 0, offset: 0 });
      await listRetrospectives({ limit: 0 });
      const url = mockRequest.mock.calls[0][0] as string;
      expect(url).toContain("limit=0");
    });

    it("includes offset=0 in query string", async () => {
      mockRequest.mockResolvedValue({ retrospectives: [], total: 0, limit: 20, offset: 0 });
      await listRetrospectives({ offset: 0 });
      const url = mockRequest.mock.calls[0][0] as string;
      expect(url).toContain("offset=0");
    });

    it("propagates request rejection", async () => {
      mockRequest.mockRejectedValue(new Error("Network error"));
      await expect(listRetrospectives()).rejects.toThrow("Network error");
    });
  });

  describe("getRetrospective", () => {
    it("calls correct URL with commentId", async () => {
      mockRequest.mockResolvedValue({ id: "retro-1" });
      await getRetrospective("retro-1");
      expect(mockRequest).toHaveBeenCalledWith("http://test/api/retrospectives/retro-1");
    });

    it("encodes special characters in commentId", async () => {
      mockRequest.mockResolvedValue({ id: "a/b" });
      await getRetrospective("a/b");
      expect(mockRequest).toHaveBeenCalledWith("http://test/api/retrospectives/a%2Fb");
    });

    it("propagates request rejection", async () => {
      mockRequest.mockRejectedValue(new Error("Not found"));
      await expect(getRetrospective("bad-id")).rejects.toThrow("Not found");
    });
  });

  describe("getRetrospectiveStats", () => {
    it("calls correct URL", async () => {
      mockRequest.mockResolvedValue({ totalRetrospectives: 0, byAgent: [], averageContentLength: 0, latestRetrospectiveAt: null });
      await getRetrospectiveStats();
      expect(mockRequest).toHaveBeenCalledWith("http://test/api/retrospectives/stats");
    });

    it("propagates request rejection", async () => {
      mockRequest.mockRejectedValue(new Error("Server error"));
      await expect(getRetrospectiveStats()).rejects.toThrow("Server error");
    });
  });
});
