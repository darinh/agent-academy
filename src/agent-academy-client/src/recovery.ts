export interface StorageLike {
  getItem(key: string): string | null;
  setItem(key: string, value: string): void;
  removeItem(key: string): void;
}

const CHAT_DRAFT_STORAGE_PREFIX = "aa-chat-draft";

function getStorage(storage?: StorageLike | null): StorageLike | null {
  if (storage !== undefined) {
    return storage;
  }

  try {
    return window.localStorage;
  } catch {
    return null;
  }
}

export function getChatDraftStorageKey(roomId: string): string {
  return `${CHAT_DRAFT_STORAGE_PREFIX}:${roomId}`;
}

export function loadChatDraft(roomId: string, storage?: StorageLike | null): string {
  const resolvedStorage = getStorage(storage);
  if (!resolvedStorage) {
    return "";
  }

  try {
    return resolvedStorage.getItem(getChatDraftStorageKey(roomId)) ?? "";
  } catch {
    return "";
  }
}

export function saveChatDraft(roomId: string, value: string, storage?: StorageLike | null): void {
  const resolvedStorage = getStorage(storage);
  if (!resolvedStorage) {
    return;
  }

  try {
    if (value.trim().length === 0) {
      resolvedStorage.removeItem(getChatDraftStorageKey(roomId));
      return;
    }

    resolvedStorage.setItem(getChatDraftStorageKey(roomId), value);
  } catch {
    // Ignore storage failures; draft state still lives in memory.
  }
}

export function clearChatDraft(roomId: string, storage?: StorageLike | null): void {
  const resolvedStorage = getStorage(storage);
  if (!resolvedStorage) {
    return;
  }

  try {
    resolvedStorage.removeItem(getChatDraftStorageKey(roomId));
  } catch {
    // Ignore storage failures; the next send still clears in-memory state.
  }
}

export function hasInstanceChanged(previousInstanceId: string | null, nextInstanceId: string): boolean {
  return previousInstanceId !== null && previousInstanceId !== nextInstanceId;
}
