import { test as base, expect } from "./fixtures";
import { mockAllApis, mockRoom, mockAgents } from "./fixtures";
import type { Page } from "@playwright/test";

/* ── Mock data for chat tests ─────────────────────────────────────────── */

const mockMessages = [
  {
    id: "msg-1",
    content: "Let's start planning the authentication module.",
    senderKind: "Agent",
    senderId: "planner-1",
    senderName: "Aristotle",
    sentAt: new Date(Date.now() - 120_000).toISOString(),
    kind: "Chat",
    recipientId: null,
  },
  {
    id: "msg-2",
    content: "I'll handle the JWT token generation and validation.",
    senderKind: "Agent",
    senderId: "engineer-1",
    senderName: "Hephaestus",
    sentAt: new Date(Date.now() - 60_000).toISOString(),
    kind: "Chat",
    recipientId: null,
  },
  {
    id: "msg-3",
    content: "System: Room created",
    senderKind: "System",
    senderId: "system",
    senderName: "System",
    sentAt: new Date(Date.now() - 180_000).toISOString(),
    kind: "System",
    recipientId: null,
  },
];

const roomWithMessages = {
  ...mockRoom,
  recentMessages: mockMessages,
};

/** Set up mocks with messages in the room */
async function mockWithMessages(page: Page) {
  await mockAllApis(page);
  // Override room endpoints to include messages (last-registered wins)
  await page.route("**/api/rooms/main", (route) =>
    route.fulfill({ json: roomWithMessages }));
  await page.route("**/api/rooms", (route) =>
    route.fulfill({ json: [roomWithMessages] }));
  // Overview also uses room data
  await page.route("**/api/overview", (route) =>
    route.fulfill({
      json: {
        configuredAgents: mockAgents,
        rooms: [roomWithMessages],
        recentActivity: [],
        agentLocations: mockAgents.map((a) => ({
          agentId: a.id, roomId: "main", state: "Idle", updatedAt: new Date().toISOString(),
        })),
        breakoutRooms: [],
        generatedAt: new Date().toISOString(),
      },
    }));
}

/** Navigate to the Conversation tab */
async function goToChat(page: Page) {
  await page.goto("/");
  await expect(page).toHaveTitle(/Main Room/, { timeout: 10_000 });
  // Conversation is the default tab, but click it explicitly to be sure
  const tab = page.getByRole("tab", { name: /conversation/i });
  await expect(tab).toBeVisible({ timeout: 5_000 });
  await tab.click();
}

/* ── Tests ────────────────────────────────────────────────────────────── */

const test = base.extend<{ chatPage: Page }>({
  chatPage: async ({ page }, use) => {
    await mockWithMessages(page);
    await use(page);
  },
});

test.describe("Chat panel", () => {
  test("renders messages from agents", async ({ chatPage: page }) => {
    await goToChat(page);

    // Agent messages should be visible
    await expect(page.getByText("Let's start planning the authentication module.")).toBeVisible({ timeout: 5_000 });
    await expect(page.getByText("I'll handle the JWT token generation and validation.")).toBeVisible({ timeout: 3_000 });
  });

  test("shows agent names on messages", async ({ chatPage: page }) => {
    await goToChat(page);

    await expect(page.getByText("Aristotle").first()).toBeVisible({ timeout: 5_000 });
    await expect(page.getByText("Hephaestus").first()).toBeVisible({ timeout: 3_000 });
  });

  test("shows message composer with textarea", async ({ chatPage: page }) => {
    await goToChat(page);

    const textarea = page.getByRole("textbox", { name: /message to agents/i });
    await expect(textarea).toBeVisible({ timeout: 5_000 });
    await expect(page.getByRole("button", { name: /send message/i })).toBeVisible({ timeout: 3_000 });
    await expect(page.getByRole("button", { name: /clear/i })).toBeVisible({ timeout: 3_000 });
  });

  test("send button is disabled when textarea is empty", async ({ chatPage: page }) => {
    await goToChat(page);

    const sendBtn = page.getByRole("button", { name: /send message/i });
    await expect(sendBtn).toBeVisible({ timeout: 5_000 });
    await expect(sendBtn).toBeDisabled();
  });

  test("send button enables when text is entered", async ({ chatPage: page }) => {
    await goToChat(page);

    const textarea = page.getByRole("textbox", { name: /message to agents/i });
    await expect(textarea).toBeVisible({ timeout: 5_000 });
    await textarea.fill("Hello agents!");

    const sendBtn = page.getByRole("button", { name: /send message/i });
    await expect(sendBtn).toBeEnabled();
  });

  test("shows connection status indicator", async ({ chatPage: page }) => {
    await goToChat(page);

    // Status text should be visible — scoped to the message log area to avoid false matches
    const chatArea = page.getByRole("log", { name: /conversation messages/i }).locator("..");
    await expect(chatArea.getByText(/live|connecting|reconnecting|disconnected/i).first()).toBeVisible({ timeout: 5_000 });
  });

  test("has message list with log role", async ({ chatPage: page }) => {
    await goToChat(page);

    const messageLog = page.getByRole("log", { name: /conversation messages/i });
    await expect(messageLog).toBeVisible({ timeout: 5_000 });
  });

  test("shows filter button", async ({ chatPage: page }) => {
    await goToChat(page);

    await expect(page.getByRole("button", { name: /filter/i })).toBeVisible({ timeout: 5_000 });
  });
});

test.describe("Chat panel — empty room", () => {
  test("shows empty state when no messages", async ({ mockedPage: page }) => {
    await goToChat(page);

    await expect(page.getByText("No messages yet for this room.")).toBeVisible({ timeout: 5_000 });
  });
});
