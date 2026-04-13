import { describe, expect, it, vi, beforeEach } from "vitest";
import { listDigests, getDigest, getDigestStats } from "../api/digests";

vi.mock("../api/core", () => ({
  apiUrl: (path: string) => `http://test${path}`,
  request: vi.fn(),
}));

import { request } from "../api/core";
const mockRequest = vi.mocked(request);

beforeEach(() => {
  vi.resetAllMocks();
});

describe("digests API", () => {
  describe("listDigests", () => {
    it("calls correct URL with no params", async () => {
      mockRequest.mockResolvedValue({ digests: [], total: 0, limit: 20, offset: 0 });
      await listDigests();
      expect(mockRequest).toHaveBeenCalledWith("http://test/api/digests");
    });

    it("includes status in query string", async () => {
      mockRequest.mockResolvedValue({ digests: [], total: 0, limit: 20, offset: 0 });
      await listDigests({ status: "Completed" });
      expect(mockRequest).toHaveBeenCalledWith(
        expect.stringContaining("status=Completed"),
      );
    });

    it("includes limit and offset in query string", async () => {
      mockRequest.mockResolvedValue({ digests: [], total: 0, limit: 10, offset: 20 });
      await listDigests({ limit: 10, offset: 20 });
      const url = mockRequest.mock.calls[0][0] as string;
      expect(url).toContain("limit=10");
      expect(url).toContain("offset=20");
    });

    it("omits undefined status from query string", async () => {
      mockRequest.mockResolvedValue({ digests: [], total: 0, limit: 20, offset: 0 });
      await listDigests({ status: undefined });
      const url = mockRequest.mock.calls[0][0] as string;
      expect(url).not.toContain("status");
    });

    it("omits empty string status from query string", async () => {
      mockRequest.mockResolvedValue({ digests: [], total: 0, limit: 20, offset: 0 });
      await listDigests({ status: "" });
      const url = mockRequest.mock.calls[0][0] as string;
      expect(url).not.toContain("status");
    });

    it("includes limit=0 in query string", async () => {
      mockRequest.mockResolvedValue({ digests: [], total: 0, limit: 0, offset: 0 });
      await listDigests({ limit: 0 });
      const url = mockRequest.mock.calls[0][0] as string;
      expect(url).toContain("limit=0");
    });

    it("includes offset=0 in query string", async () => {
      mockRequest.mockResolvedValue({ digests: [], total: 0, limit: 20, offset: 0 });
      await listDigests({ offset: 0 });
      const url = mockRequest.mock.calls[0][0] as string;
      expect(url).toContain("offset=0");
    });

    it("returns the response from request()", async () => {
      const expected = { digests: [{ id: 1 }], total: 1, limit: 20, offset: 0 };
      mockRequest.mockResolvedValue(expected);
      const result = await listDigests();
      expect(result).toBe(expected);
    });
  });

  describe("getDigest", () => {
    it("calls correct URL with digest ID", async () => {
      mockRequest.mockResolvedValue({ id: 42 });
      await getDigest(42);
      expect(mockRequest).toHaveBeenCalledWith("http://test/api/digests/42");
    });

    it("returns the detail response", async () => {
      const expected = { id: 42, summary: "test", sources: [] };
      mockRequest.mockResolvedValue(expected);
      const result = await getDigest(42);
      expect(result).toBe(expected);
    });
  });

  describe("getDigestStats", () => {
    it("calls correct URL", async () => {
      mockRequest.mockResolvedValue({ totalDigests: 0 });
      await getDigestStats();
      expect(mockRequest).toHaveBeenCalledWith("http://test/api/digests/stats");
    });

    it("returns the stats response", async () => {
      const expected = { totalDigests: 5, byStatus: {} };
      mockRequest.mockResolvedValue(expected);
      const result = await getDigestStats();
      expect(result).toBe(expected);
    });
  });
});
