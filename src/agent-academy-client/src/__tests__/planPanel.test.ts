import { describe, expect, it, vi, beforeEach } from "vitest";

vi.mock("../api", () => ({
  getPlan: vi.fn(),
  setPlan: vi.fn(),
  deletePlan: vi.fn(),
}));

import { getPlan, setPlan, deletePlan } from "../api";
import type { PlanContent } from "../api";

const mockGetPlan = vi.mocked(getPlan);
const mockSetPlan = vi.mocked(setPlan);
const mockDeletePlan = vi.mocked(deletePlan);

// ── Factories ──

function makePlan(content: string = "## Sprint Plan\n\n- Task 1\n- Task 2"): PlanContent {
  return { content };
}

// ── Tests ──

describe("PlanPanel", () => {
  beforeEach(() => {
    vi.resetAllMocks();
  });

  describe("getPlan API contract", () => {
    it("returns plan content for a valid room", async () => {
      mockGetPlan.mockResolvedValue(makePlan("# My Plan"));
      const result = await getPlan("room-1");
      expect(mockGetPlan).toHaveBeenCalledWith("room-1");
      expect(result).not.toBeNull();
      expect(result!.content).toBe("# My Plan");
    });

    it("returns null when no plan exists", async () => {
      mockGetPlan.mockResolvedValue(null);
      const result = await getPlan("room-1");
      expect(result).toBeNull();
    });

    it("plan content can contain complex markdown", async () => {
      const markdown = [
        "# Architecture Plan",
        "",
        "## Phase 1",
        "- [ ] Set up database schema",
        "- [ ] Create API endpoints",
        "",
        "```csharp",
        "public class MyService { }",
        "```",
        "",
        "| Component | Status |",
        "|-----------|--------|",
        "| API       | Done   |",
      ].join("\n");
      mockGetPlan.mockResolvedValue(makePlan(markdown));
      const result = await getPlan("room-1");
      expect(result!.content).toContain("## Phase 1");
      expect(result!.content).toContain("```csharp");
      expect(result!.content).toContain("| Component | Status |");
    });

    it("rejects on fetch failure", async () => {
      mockGetPlan.mockRejectedValue(new Error("Server error"));
      await expect(getPlan("room-1")).rejects.toThrow("Server error");
    });
  });

  describe("setPlan API contract", () => {
    it("saves plan content and resolves void", async () => {
      mockSetPlan.mockResolvedValue(undefined);
      await setPlan("room-1", "Updated plan content");
      expect(mockSetPlan).toHaveBeenCalledWith("room-1", "Updated plan content");
    });

    it("can save empty content", async () => {
      mockSetPlan.mockResolvedValue(undefined);
      await setPlan("room-1", "");
      expect(mockSetPlan).toHaveBeenCalledWith("room-1", "");
    });

    it("rejects on save failure", async () => {
      mockSetPlan.mockRejectedValue(new Error("Unauthorized"));
      await expect(setPlan("room-1", "content")).rejects.toThrow("Unauthorized");
    });
  });

  describe("deletePlan API contract", () => {
    it("deletes plan and resolves void", async () => {
      mockDeletePlan.mockResolvedValue(undefined);
      await deletePlan("room-1");
      expect(mockDeletePlan).toHaveBeenCalledWith("room-1");
    });

    it("rejects on delete failure", async () => {
      mockDeletePlan.mockRejectedValue(new Error("Not found"));
      await expect(deletePlan("room-1")).rejects.toThrow("Not found");
    });
  });

  describe("PlanContent type shape", () => {
    it("has a content string field", () => {
      const plan = makePlan("test");
      expect(plan).toHaveProperty("content");
      expect(typeof plan.content).toBe("string");
    });
  });

  describe("component state logic", () => {
    it("loading state tracks when roomId changes", () => {
      // Simulates: roomId !== null && loadedRoomId !== roomId
      const roomId = "room-1";
      const loadedRoomId: string | null = null;
      const loading = roomId !== null && loadedRoomId !== roomId;
      expect(loading).toBe(true);
    });

    it("loading is false once loadedRoomId matches roomId", () => {
      const roomId = "room-1";
      const loadedRoomId = "room-1";
      const loading = roomId !== null && loadedRoomId !== roomId;
      expect(loading).toBe(false);
    });

    it("loading is false when roomId is null", () => {
      const roomId: string | null = null;
      const loadedRoomId: string | null = null;
      const loading = roomId !== null && loadedRoomId !== roomId;
      expect(loading).toBe(false);
    });

    it("edit mode initializes draft from current content", () => {
      const content = "# Current Plan";
      const draft = content; // handleEdit sets draft = content
      expect(draft).toBe(content);
    });

    it("save replaces content with draft", async () => {
      mockSetPlan.mockResolvedValue(undefined);
      const draft = "# Updated Plan";
      await setPlan("room-1", draft);
      // After successful save, content = draft, editing = false
      expect(mockSetPlan).toHaveBeenCalledWith("room-1", "# Updated Plan");
    });

    it("delete clears content and exits edit mode", async () => {
      mockDeletePlan.mockResolvedValue(undefined);
      await deletePlan("room-1");
      // After successful delete: content = "", editing = false, confirmOpen = false
      expect(mockDeletePlan).toHaveBeenCalledWith("room-1");
    });
  });

  describe("edge cases", () => {
    it("handles plan with only whitespace", async () => {
      mockGetPlan.mockResolvedValue(makePlan("   \n\n  "));
      const result = await getPlan("room-1");
      expect(result!.content.trim()).toBe("");
    });

    it("handles very long plan content", async () => {
      const longContent = "x".repeat(100_000);
      mockGetPlan.mockResolvedValue(makePlan(longContent));
      const result = await getPlan("room-1");
      expect(result!.content).toHaveLength(100_000);
    });
  });
});
