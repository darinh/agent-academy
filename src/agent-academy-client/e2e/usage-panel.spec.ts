import { test as base, expect } from "@playwright/test";
import { mockAllApis } from "./fixtures";
import type { Page } from "@playwright/test";

// ── Mock data ───────────────────────────────────────────────────────────

const mockUsageSummary = {
  totalInputTokens: 125000,
  totalOutputTokens: 43000,
  totalCost: 1.85,
  requestCount: 42,
  models: ["gpt-4o", "claude-sonnet-4-20250514"],
};

const mockUsageRecords = [
  {
    id: "rec-1",
    agentId: "engineer-1",
    roomId: "main",
    model: "gpt-4o",
    inputTokens: 5000,
    outputTokens: 1200,
    cacheReadTokens: 800,
    cacheWriteTokens: 200,
    cost: 0.045,
    durationMs: 2300,
    reasoningEffort: null,
    recordedAt: new Date(Date.now() - 60_000).toISOString(),
  },
  {
    id: "rec-2",
    agentId: "planner-1",
    roomId: "main",
    model: "claude-sonnet-4-20250514",
    inputTokens: 3200,
    outputTokens: 900,
    cacheReadTokens: 0,
    cacheWriteTokens: 0,
    cost: 0.032,
    durationMs: 1800,
    reasoningEffort: "medium",
    recordedAt: new Date(Date.now() - 120_000).toISOString(),
  },
];

const emptyUsageSummary = {
  totalInputTokens: 0,
  totalOutputTokens: 0,
  totalCost: 0,
  requestCount: 0,
  models: [],
};

// ── Fixtures ────────────────────────────────────────────────────────────

async function mockUsageApis(page: Page) {
  await mockAllApis(page);

  await page.route("**/api/usage/records**", (route) =>
    route.fulfill({ json: mockUsageRecords }));
  await page.route("**/api/usage?**", (route) =>
    route.fulfill({ json: mockUsageSummary }));
  await page.route("**/api/usage", (route) =>
    route.fulfill({ json: mockUsageSummary }));

  // Stub other dashboard sub-panels
  await page.route("**/api/errors?**", (route) =>
    route.fulfill({ json: { totalErrors: 0, recoverableErrors: 0, unrecoverableErrors: 0, byType: [], byAgent: [] } }));
  await page.route("**/api/errors", (route) =>
    route.fulfill({ json: { totalErrors: 0, recoverableErrors: 0, unrecoverableErrors: 0, byType: [], byAgent: [] } }));
  await page.route("**/api/errors/records**", (route) =>
    route.fulfill({ json: [] }));
  await page.route("**/api/system/restarts/stats**", (route) =>
    route.fulfill({ json: { totalInstances: 0, crashRestarts: 0, intentionalRestarts: 0, cleanShutdowns: 0, stillRunning: 0, windowHours: 24, maxRestartsPerWindow: 10, restartWindowHours: 1 } }));
  await page.route("**/api/system/restarts?**", (route) =>
    route.fulfill({ json: { instances: [], total: 0, limit: 10, offset: 0 } }));
  await page.route("**/api/system/restarts", (route) =>
    route.fulfill({ json: { instances: [], total: 0, limit: 10, offset: 0 } }));
}

const test = base.extend<{ usagePage: Page }>({
  usagePage: async ({ page }, use) => {
    await mockUsageApis(page);
    await page.goto("/");
    await expect(page).toHaveTitle(/Main Room/);
    const dashTab = page.getByRole("tab", { name: /dashboard/i });
    await dashTab.click({ force: true });
    await use(page);
  },
});

// ── Tests ───────────────────────────────────────────────────────────────

test.describe("UsagePanel", () => {
  test("renders usage summary cards", async ({ usagePage: page }) => {
    await expect(page.getByText("Input Tokens")).toBeVisible({ timeout: 5_000 });
    await expect(page.getByText("Output Tokens")).toBeVisible();
    await expect(page.getByText("Total Cost")).toBeVisible();
    await expect(page.getByText("LLM Calls", { exact: true })).toBeVisible();
  });

  test("shows token counts", async ({ usagePage: page }) => {
    await expect(page.getByText("Input Tokens")).toBeVisible({ timeout: 5_000 });
    // 125000 → "125.0K", 43000 → "43.0K" via formatTokenCount
    await expect(page.getByText("125.0K")).toBeVisible();
    await expect(page.getByText("43.0K")).toBeVisible();
  });

  test("shows models list", async ({ usagePage: page }) => {
    await expect(page.getByText("Models")).toBeVisible({ timeout: 5_000 });
    await expect(page.getByText("gpt-4o").first()).toBeVisible();
    await expect(page.getByText(/claude/i).first()).toBeVisible();
  });

  test("renders recent LLM calls table", async ({ usagePage: page }) => {
    await expect(page.getByText("Recent LLM Calls")).toBeVisible({ timeout: 5_000 });
    // Records should show agent IDs
    await expect(page.getByText("engineer-1").first()).toBeVisible();
    await expect(page.getByText("planner-1").first()).toBeVisible();
  });

  test("shows empty state when no usage", async ({ page }) => {
    await mockAllApis(page);
    await page.route("**/api/usage/records**", (route) =>
      route.fulfill({ json: [] }));
    await page.route("**/api/usage?**", (route) =>
      route.fulfill({ json: emptyUsageSummary }));
    await page.route("**/api/usage", (route) =>
      route.fulfill({ json: emptyUsageSummary }));
    await page.route("**/api/errors?**", (route) =>
      route.fulfill({ json: { totalErrors: 0, recoverableErrors: 0, unrecoverableErrors: 0, byType: [], byAgent: [] } }));
    await page.route("**/api/errors", (route) =>
      route.fulfill({ json: { totalErrors: 0, recoverableErrors: 0, unrecoverableErrors: 0, byType: [], byAgent: [] } }));
    await page.route("**/api/errors/records**", (route) =>
      route.fulfill({ json: [] }));
    await page.route("**/api/system/restarts/stats**", (route) =>
      route.fulfill({ json: { totalInstances: 0, crashRestarts: 0, intentionalRestarts: 0, cleanShutdowns: 0, stillRunning: 0, windowHours: 24, maxRestartsPerWindow: 10, restartWindowHours: 1 } }));
    await page.route("**/api/system/restarts?**", (route) =>
      route.fulfill({ json: { instances: [], total: 0, limit: 10, offset: 0 } }));
    await page.route("**/api/system/restarts", (route) =>
      route.fulfill({ json: { instances: [], total: 0, limit: 10, offset: 0 } }));

    await page.goto("/");
    await expect(page).toHaveTitle(/Main Room/);
    const dashTab = page.getByRole("tab", { name: /dashboard/i });
    await dashTab.click({ force: true });

    await expect(page.getByText("No LLM usage recorded yet")).toBeVisible({ timeout: 5_000 });
  });

  test("refresh button is present", async ({ usagePage: page }) => {
    await expect(page.getByText("Input Tokens")).toBeVisible({ timeout: 5_000 });
    const refreshButtons = page.getByRole("button", { name: /refresh/i });
    const count = await refreshButtons.count();
    expect(count).toBeGreaterThan(0);
  });
});
