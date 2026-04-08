import { test as base, expect } from "./fixtures";
import { mockAllApis, mockAgents } from "./fixtures";
import type { Page } from "@playwright/test";

/* ── Mock data for DM tests ──────────────────────────────────────────── */

const mockThreads = [
  {
    agentId: "planner-1",
    agentName: "Aristotle",
    agentRole: "Planner",
    lastMessage: "I've reviewed the plan and have some suggestions.",
    lastMessageAt: new Date(Date.now() - 30_000).toISOString(),
    messageCount: 5,
  },
];

const mockThreadMessages = [
  {
    id: "dm-1",
    senderId: "consultant",
    senderName: "Human",
    content: "Can you review the architecture spec?",
    sentAt: new Date(Date.now() - 120_000).toISOString(),
    isFromHuman: true,
  },
  {
    id: "dm-2",
    senderId: "planner-1",
    senderName: "Aristotle",
    content: "I've reviewed the plan and have some suggestions.",
    sentAt: new Date(Date.now() - 30_000).toISOString(),
    isFromHuman: false,
  },
];

/** Navigate to the Messages (DM) tab */
async function goToMessages(page: Page) {
  await page.goto("/");
  await expect(page).toHaveTitle(/Main Room/, { timeout: 10_000 });

  const tab = page.getByRole("tab", { name: /messages/i });
  await expect(tab).toBeVisible({ timeout: 5_000 });
  await tab.click();
}

/* ── Tests — empty state ──────────────────────────────────────────────── */

const test = base.extend<{ dmPage: Page }>({
  dmPage: async ({ page }, use) => {
    await mockAllApis(page);
    // Mock DM API returning empty threads
    await page.route("**/api/dm/threads", (route) => {
      if (route.request().url().match(/\/api\/dm\/threads$/)) {
        return route.fulfill({ json: [] });
      }
      return route.continue();
    });
    await use(page);
  },
});

test.describe("DM panel — empty state", () => {
  test("shows Messages header", async ({ dmPage: page }) => {
    await goToMessages(page);

    await expect(page.getByText("Messages").first()).toBeVisible({ timeout: 5_000 });
  });

  test("shows no conversations message when empty", async ({ dmPage: page }) => {
    await goToMessages(page);

    await expect(page.getByText("No conversations yet")).toBeVisible({ timeout: 5_000 });
  });

  test("shows new conversation button", async ({ dmPage: page }) => {
    await goToMessages(page);

    await expect(page.getByRole("button", { name: "+" }).or(page.getByTitle(/new/i))).toBeVisible({ timeout: 5_000 });
  });
});

/* ── Tests — with threads ─────────────────────────────────────────────── */

const testWithThreads = base.extend<{ dmPageWithThreads: Page }>({
  dmPageWithThreads: async ({ page }, use) => {
    await mockAllApis(page);
    // Mock all DM endpoints with a single glob — last-registered wins in Playwright
    await page.route("**/api/dm/threads/**", (route) => {
      const url = route.request().url();
      if (url.includes("/planner-1")) {
        return route.fulfill({ json: mockThreadMessages });
      }
      if (url.includes("/engineer-1")) {
        if (route.request().method() === "POST") {
          return route.fulfill({
            json: {
              id: "dm-new",
              senderId: "consultant",
              senderName: "Human",
              content: "Hello engineer!",
              sentAt: new Date().toISOString(),
              isFromHuman: true,
            },
          });
        }
        return route.fulfill({ json: [] });
      }
      return route.fulfill({ json: [] });
    });
    // Thread list — register after sub-paths so it takes priority for exact match
    await page.route("**/api/dm/threads", (route) =>
      route.fulfill({ json: mockThreads }));
    await use(page);
  },
});

testWithThreads.describe("DM panel — with threads", () => {
  testWithThreads("shows thread in sidebar with agent name", async ({ dmPageWithThreads: page }) => {
    await goToMessages(page);

    await expect(page.getByText("Aristotle").first()).toBeVisible({ timeout: 5_000 });
  });

  testWithThreads("shows thread preview text", async ({ dmPageWithThreads: page }) => {
    await goToMessages(page);

    await expect(page.getByText(/reviewed the plan/i).first()).toBeVisible({ timeout: 5_000 });
  });

  testWithThreads("clicking thread shows messages", async ({ dmPageWithThreads: page }) => {
    await goToMessages(page);

    // Click the thread using evaluate to bypass section overlay
    const threadPreview = page.getByText(/reviewed the plan/i).first();
    await expect(threadPreview).toBeVisible({ timeout: 5_000 });
    await threadPreview.evaluate((el) => {
      // Click the parent button element
      const btn = el.closest("button") ?? el;
      (btn as HTMLElement).click();
    });

    // Messages should appear in the chat area
    await expect(page.getByText("Can you review the architecture spec?")).toBeVisible({ timeout: 5_000 });
    // Scope to message log to avoid matching the sidebar preview too
    await expect(page.getByLabel("Direct messages").getByText(/reviewed the plan/i)).toBeVisible({ timeout: 3_000 });
  });

  testWithThreads("shows message composer when thread is selected", async ({ dmPageWithThreads: page }) => {
    await goToMessages(page);

    // Click the thread using evaluate
    const threadPreview = page.getByText(/reviewed the plan/i).first();
    await expect(threadPreview).toBeVisible({ timeout: 5_000 });
    await threadPreview.evaluate((el) => {
      const btn = el.closest("button") ?? el;
      (btn as HTMLElement).click();
    });

    // Composer textarea should appear
    const textarea = page.getByRole("textbox");
    await expect(textarea).toBeVisible({ timeout: 5_000 });
  });

  testWithThreads("has direct messages log role", async ({ dmPageWithThreads: page }) => {
    await goToMessages(page);

    // Click thread using evaluate
    const threadPreview = page.getByText(/reviewed the plan/i).first();
    await expect(threadPreview).toBeVisible({ timeout: 5_000 });
    await threadPreview.evaluate((el) => {
      const btn = el.closest("button") ?? el;
      (btn as HTMLElement).click();
    });

    const messageLog = page.getByRole("log", { name: /direct messages/i });
    await expect(messageLog).toBeVisible({ timeout: 5_000 });
  });
});
