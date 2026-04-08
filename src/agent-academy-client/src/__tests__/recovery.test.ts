import { describe, expect, it } from "vitest";
import {
  clearChatDraft,
  getChatDraftStorageKey,
  hasInstanceChanged,
  loadChatDraft,
  saveChatDraft,
  type StorageLike,
} from "../recovery";

class MemoryStorage implements StorageLike {
  private readonly store = new Map<string, string>();

  getItem(key: string): string | null {
    return this.store.get(key) ?? null;
  }

  setItem(key: string, value: string): void {
    this.store.set(key, value);
  }

  removeItem(key: string): void {
    this.store.delete(key);
  }
}

describe("recovery helpers", () => {
  it("uses room-scoped keys for chat drafts", () => {
    expect(getChatDraftStorageKey("room-123")).toBe("aa-chat-draft:room-123");
  });

  it("persists and clears chat drafts", () => {
    const storage = new MemoryStorage();

    saveChatDraft("room-123", "unsent message", storage);
    expect(loadChatDraft("room-123", storage)).toBe("unsent message");

    clearChatDraft("room-123", storage);
    expect(loadChatDraft("room-123", storage)).toBe("");
  });

  it("removes blank drafts instead of storing empty values", () => {
    const storage = new MemoryStorage();

    saveChatDraft("room-123", "draft", storage);
    saveChatDraft("room-123", "   ", storage);

    expect(loadChatDraft("room-123", storage)).toBe("");
  });

  it("detects server instance changes only when a baseline exists", () => {
    expect(hasInstanceChanged(null, "next")).toBe(false);
    expect(hasInstanceChanged("same", "same")).toBe(false);
    expect(hasInstanceChanged("old", "new")).toBe(true);
  });
});
