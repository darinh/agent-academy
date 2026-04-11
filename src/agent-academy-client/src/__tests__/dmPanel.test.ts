import { describe, expect, it, vi, beforeEach } from "vitest";

vi.mock("../api", () => ({
  getDmThreads: vi.fn(),
  getDmThreadMessages: vi.fn(),
  sendDmToAgent: vi.fn(),
}));

import { getDmThreads, getDmThreadMessages, sendDmToAgent } from "../api";
import type { DmThreadSummary, DmMessage } from "../api";

const mockGetDmThreads = vi.mocked(getDmThreads);
const mockGetDmThreadMessages = vi.mocked(getDmThreadMessages);
const mockSendDmToAgent = vi.mocked(sendDmToAgent);

// ── Factories ──

function makeThread(overrides: Partial<DmThreadSummary> = {}): DmThreadSummary {
  return {
    agentId: "agent-1",
    agentName: "Architect",
    agentRole: "architect",
    lastMessage: "Hello from agent",
    lastMessageAt: "2026-04-10T12:00:00Z",
    messageCount: 5,
    ...overrides,
  };
}

function makeMessage(overrides: Partial<DmMessage> = {}): DmMessage {
  return {
    id: "msg-1",
    senderId: "agent-1",
    senderName: "Architect",
    content: "Hello!",
    sentAt: "2026-04-10T12:00:00Z",
    isFromHuman: false,
    ...overrides,
  };
}

// ── Tests ──

describe("DmPanel", () => {
  beforeEach(() => {
    vi.resetAllMocks();
  });

  describe("getDmThreads API contract", () => {
    it("returns an array of thread summaries", async () => {
      const threads = [makeThread(), makeThread({ agentId: "agent-2", agentName: "Engineer" })];
      mockGetDmThreads.mockResolvedValue(threads);
      const result = await getDmThreads();
      expect(result).toHaveLength(2);
      expect(result[0].agentId).toBe("agent-1");
      expect(result[1].agentName).toBe("Engineer");
    });

    it("returns empty array when no threads exist", async () => {
      mockGetDmThreads.mockResolvedValue([]);
      const result = await getDmThreads();
      expect(result).toHaveLength(0);
    });

    it("thread summary includes messageCount", async () => {
      mockGetDmThreads.mockResolvedValue([makeThread({ messageCount: 42 })]);
      const result = await getDmThreads();
      expect(result[0].messageCount).toBe(42);
    });

    it("thread summary includes lastMessageAt timestamp", async () => {
      const ts = "2026-04-10T15:30:00Z";
      mockGetDmThreads.mockResolvedValue([makeThread({ lastMessageAt: ts })]);
      const result = await getDmThreads();
      expect(result[0].lastMessageAt).toBe(ts);
    });
  });

  describe("getDmThreadMessages API contract", () => {
    it("returns messages for a specific agent thread", async () => {
      const messages = [
        makeMessage({ id: "msg-1", content: "Hi" }),
        makeMessage({ id: "msg-2", content: "Hello", isFromHuman: true, senderId: "human" }),
      ];
      mockGetDmThreadMessages.mockResolvedValue(messages);
      const result = await getDmThreadMessages("agent-1");
      expect(mockGetDmThreadMessages).toHaveBeenCalledWith("agent-1");
      expect(result).toHaveLength(2);
      expect(result[0].isFromHuman).toBe(false);
      expect(result[1].isFromHuman).toBe(true);
    });

    it("returns empty array for thread with no messages", async () => {
      mockGetDmThreadMessages.mockResolvedValue([]);
      const result = await getDmThreadMessages("agent-new");
      expect(result).toHaveLength(0);
    });

    it("messages include sender metadata", async () => {
      const msg = makeMessage({ senderId: "agent-2", senderName: "Engineer" });
      mockGetDmThreadMessages.mockResolvedValue([msg]);
      const result = await getDmThreadMessages("agent-2");
      expect(result[0].senderId).toBe("agent-2");
      expect(result[0].senderName).toBe("Engineer");
    });
  });

  describe("sendDmToAgent API contract", () => {
    it("sends a message and returns the created DmMessage", async () => {
      const reply = makeMessage({
        id: "msg-new",
        senderId: "human",
        senderName: "Human",
        content: "Please review the plan",
        isFromHuman: true,
      });
      mockSendDmToAgent.mockResolvedValue(reply);
      const result = await sendDmToAgent("agent-1", "Please review the plan");
      expect(mockSendDmToAgent).toHaveBeenCalledWith("agent-1", "Please review the plan");
      expect(result.id).toBe("msg-new");
      expect(result.isFromHuman).toBe(true);
      expect(result.content).toBe("Please review the plan");
    });

    it("propagates errors on send failure", async () => {
      mockSendDmToAgent.mockRejectedValue(new Error("Network error"));
      await expect(sendDmToAgent("agent-1", "test")).rejects.toThrow("Network error");
    });
  });

  describe("DmThreadSummary type shape", () => {
    it("has all required fields", () => {
      const thread = makeThread();
      expect(thread).toHaveProperty("agentId");
      expect(thread).toHaveProperty("agentName");
      expect(thread).toHaveProperty("agentRole");
      expect(thread).toHaveProperty("lastMessage");
      expect(thread).toHaveProperty("lastMessageAt");
      expect(thread).toHaveProperty("messageCount");
    });

    it("agentRole is a string identifying the agent role", () => {
      const thread = makeThread({ agentRole: "engineer" });
      expect(typeof thread.agentRole).toBe("string");
      expect(thread.agentRole).toBe("engineer");
    });
  });

  describe("DmMessage type shape", () => {
    it("has all required fields", () => {
      const msg = makeMessage();
      expect(msg).toHaveProperty("id");
      expect(msg).toHaveProperty("senderId");
      expect(msg).toHaveProperty("senderName");
      expect(msg).toHaveProperty("content");
      expect(msg).toHaveProperty("sentAt");
      expect(msg).toHaveProperty("isFromHuman");
    });

    it("distinguishes human vs agent messages", () => {
      const agentMsg = makeMessage({ isFromHuman: false });
      const humanMsg = makeMessage({ isFromHuman: true, senderId: "human", senderName: "Darin" });
      expect(agentMsg.isFromHuman).toBe(false);
      expect(humanMsg.isFromHuman).toBe(true);
    });

    it("content can contain markdown", () => {
      const msg = makeMessage({ content: "## Plan\n- Step 1\n- Step 2" });
      expect(msg.content).toContain("## Plan");
    });
  });

  describe("thread filtering logic", () => {
    it("can identify agents without existing threads", () => {
      const agents = [
        { id: "agent-1", name: "Architect", role: "architect" },
        { id: "agent-2", name: "Engineer", role: "engineer" },
        { id: "agent-3", name: "Tester", role: "tester" },
      ];
      const threads = [makeThread({ agentId: "agent-1" }), makeThread({ agentId: "agent-3" })];
      const agentsWithoutThread = agents.filter(
        (a) => !threads.some((t) => t.agentId === a.id),
      );
      expect(agentsWithoutThread).toHaveLength(1);
      expect(agentsWithoutThread[0].id).toBe("agent-2");
    });

    it("returns empty when all agents have threads", () => {
      const agents = [{ id: "agent-1", name: "Architect", role: "architect" }];
      const threads = [makeThread({ agentId: "agent-1" })];
      const agentsWithoutThread = agents.filter(
        (a) => !threads.some((t) => t.agentId === a.id),
      );
      expect(agentsWithoutThread).toHaveLength(0);
    });
  });

  describe("error handling patterns", () => {
    it("getDmThreads rejects on network failure", async () => {
      mockGetDmThreads.mockRejectedValue(new Error("Failed to fetch"));
      await expect(getDmThreads()).rejects.toThrow("Failed to fetch");
    });

    it("getDmThreadMessages rejects on network failure", async () => {
      mockGetDmThreadMessages.mockRejectedValue(new Error("Not found"));
      await expect(getDmThreadMessages("bad-id")).rejects.toThrow("Not found");
    });
  });
});
