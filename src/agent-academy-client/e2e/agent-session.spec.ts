import { test as base, expect } from "@playwright/test";
import { mockAllApis, mockAgents, mockOverview } from "./fixtures";
import type { Page } from "@playwright/test";

// ── Mock data ───────────────────────────────────────────────────────────

const mockBreakoutSessions = [
  {
    id: "br-active-1",
    name: "BR: Implement user authentication",
    parentRoomId: "main",
    assignedAgentId: "engineer-1",
    tasks: [],
    status: "Active",
    recentMessages: [
      {
        id: "msg-1",
        sender: "engineer-1",
        senderName: "Hephaestus",
        content: "Starting implementation of JWT auth flow.",
        kind: "Agent",
        timestamp: new Date(Date.now() - 300_000).toISOString(),
      },
      {
        id: "msg-2",
        sender: "system",
        senderName: "System",
        content: "Task assigned to Hephaestus.",
        kind: "System",
        timestamp: new Date(Date.now() - 600_000).toISOString(),
      },
    ],
    createdAt: new Date(Date.now() - 3600_000).toISOString(),
    updatedAt: new Date(Date.now() - 60_000).toISOString(),
  },
  {
    id: "br-archived-1",
    name: "BR: Fix CSS grid layout",
    parentRoomId: "main",
    assignedAgentId: "engineer-1",
    tasks: [],
    status: "Archived",
    recentMessages: [
      {
        id: "msg-3",
        sender: "engineer-1",
        senderName: "Hephaestus",
        content: "CSS fix complete.",
        kind: "Agent",
        timestamp: new Date(Date.now() - 7200_000).toISOString(),
      },
    ],
    createdAt: new Date(Date.now() - 14400_000).toISOString(),
    updatedAt: new Date(Date.now() - 7200_000).toISOString(),
  },
  {
    id: "br-archived-2",
    name: "BR: Add unit tests for parser",
    parentRoomId: "main",
    assignedAgentId: "engineer-1",
    tasks: [],
    status: "Archived",
    recentMessages: [],
    createdAt: new Date(Date.now() - 28800_000).toISOString(),
    updatedAt: new Date(Date.now() - 14400_000).toISOString(),
  },
];

// ── Helpers ─────────────────────────────────────────────────────────────

async function mockAgentSessionApis(page: Page) {
  await mockAllApis(page);

  // Mock agent sessions endpoint
  await page.route("**/api/agents/engineer-1/sessions", (route) =>
    route.fulfill({ json: mockBreakoutSessions }));

  // Also mock any room fetch for the breakout chat panel
  await page.route("**/api/rooms/br-*", (route) =>
    route.fulfill({ json: mockBreakoutSessions[0] }));
}

async function navigateToAgentSession(page: Page) {
  await page.goto("/");
  await expect(page).toHaveTitle(/Main Room/);

  // Click the agent button in the sidebar to open their session panel
  const agentButton = page.getByLabel(/View Hephaestus/i);
  await agentButton.click();
}

const test = base.extend<{ agentPage: Page }>({
  agentPage: async ({ page }, use) => {
    await mockAgentSessionApis(page);
    await navigateToAgentSession(page);
    await use(page);
  },
});

// ── Tests ───────────────────────────────────────────────────────────────

test.describe("AgentSessionPanel", () => {
  test("renders agent header with name and role", async ({ agentPage: page }) => {
    // The agent session panel has the agent's name in its header
    await expect(page.getByText("Hephaestus's Sessions")).toBeVisible({ timeout: 5_000 });
    await expect(page.getByText("SoftwareEngineer").first()).toBeVisible();
  });

  test("shows agent state badge", async ({ agentPage: page }) => {
    await expect(page.getByText("Hephaestus's Sessions")).toBeVisible({ timeout: 5_000 });
    // Default state from agentLocations is "Idle"
    await expect(page.getByText("Idle").first()).toBeVisible();
  });

  test("shows active session with messages", async ({ agentPage: page }) => {
    await expect(page.getByText("Implement user authentication")).toBeVisible({ timeout: 5_000 });
    await expect(page.getByText("Starting implementation of JWT auth flow")).toBeVisible();
  });

  test("shows active session badge", async ({ agentPage: page }) => {
    await expect(page.getByText("Implement user authentication")).toBeVisible({ timeout: 5_000 });
    await expect(page.getByText("Active", { exact: true }).first()).toBeVisible();
  });

  test("shows past sessions section", async ({ agentPage: page }) => {
    await expect(page.getByText(/Past Sessions/)).toBeVisible({ timeout: 5_000 });
    await expect(page.getByText("Fix CSS grid layout")).toBeVisible();
    await expect(page.getByText("Add unit tests for parser")).toBeVisible();
  });

  test("shows refresh button", async ({ agentPage: page }) => {
    await expect(page.getByText("Hephaestus's Sessions")).toBeVisible({ timeout: 5_000 });
    await expect(page.getByRole("button", { name: "↻" })).toBeVisible();
  });

  test("shows empty state when no sessions", async ({ page }) => {
    await mockAllApis(page);
    await page.route("**/api/agents/engineer-1/sessions", (route) =>
      route.fulfill({ json: [] }));

    await page.goto("/");
    await expect(page).toHaveTitle(/Main Room/);
    const agentButton = page.getByLabel(/View Hephaestus/i);
    await agentButton.click();

    await expect(page.getByText("No sessions yet")).toBeVisible({ timeout: 5_000 });
  });

  test("shows error state with retry", async ({ page }) => {
    await mockAllApis(page);
    await page.route("**/api/agents/engineer-1/sessions", (route) =>
      route.fulfill({ status: 500, body: "Internal Server Error" }));

    await page.goto("/");
    await expect(page).toHaveTitle(/Main Room/);
    const agentButton = page.getByLabel(/View Hephaestus/i);
    await agentButton.click();

    await expect(page.getByText("Failed to load sessions")).toBeVisible({ timeout: 5_000 });
    await expect(page.getByRole("button", { name: /retry/i })).toBeVisible();
  });

  test("clicking archived session switches display", async ({ agentPage: page }) => {
    // Wait for past sessions to be visible
    await expect(page.getByText(/Past Sessions/)).toBeVisible({ timeout: 5_000 });

    // Click an archived session
    const archivedSession = page.getByText("Fix CSS grid layout");
    await archivedSession.click();

    // The archived session name should now appear in the session header area
    // and the collapse indicator should change to ▾
    await expect(page.getByText("▾")).toBeVisible();
  });
});
