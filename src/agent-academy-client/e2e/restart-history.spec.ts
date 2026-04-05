import { test as base, expect } from "@playwright/test";
import { mockAllApis } from "./fixtures";
import type { Page } from "@playwright/test";

// ── Mock data ───────────────────────────────────────────────────────────

const mockRestartStats = {
  totalInstances: 5,
  crashRestarts: 1,
  intentionalRestarts: 2,
  cleanShutdowns: 1,
  stillRunning: 1,
  windowHours: 24,
  maxRestartsPerWindow: 10,
  restartWindowHours: 1,
};

const mockRestartHistory = {
  instances: [
    {
      id: "inst-1",
      startedAt: new Date(Date.now() - 3600_000).toISOString(),
      shutdownAt: null,
      exitCode: null,
      crashDetected: false,
      version: "0.3.0",
      shutdownReason: "",
    },
    {
      id: "inst-2",
      startedAt: new Date(Date.now() - 7200_000).toISOString(),
      shutdownAt: new Date(Date.now() - 3600_000).toISOString(),
      exitCode: 0,
      crashDetected: false,
      version: "0.2.9",
      shutdownReason: "Intentional restart",
    },
    {
      id: "inst-3",
      startedAt: new Date(Date.now() - 14400_000).toISOString(),
      shutdownAt: new Date(Date.now() - 7200_000).toISOString(),
      exitCode: 137,
      crashDetected: true,
      version: "0.2.8",
      shutdownReason: "Crash detected",
    },
  ],
  total: 3,
  limit: 10,
  offset: 0,
};

const emptyRestartHistory = {
  instances: [],
  total: 0,
  limit: 10,
  offset: 0,
};

const emptyRestartStats = {
  totalInstances: 0,
  crashRestarts: 0,
  intentionalRestarts: 0,
  cleanShutdowns: 0,
  stillRunning: 0,
  windowHours: 24,
  maxRestartsPerWindow: 10,
  restartWindowHours: 1,
};

// ── Fixtures ────────────────────────────────────────────────────────────

async function mockRestartApis(page: Page) {
  await mockAllApis(page);

  await page.route("**/api/system/restarts/stats**", (route) =>
    route.fulfill({ json: mockRestartStats }));
  await page.route("**/api/system/restarts?**", (route) =>
    route.fulfill({ json: mockRestartHistory }));
  await page.route("**/api/system/restarts", (route) =>
    route.fulfill({ json: mockRestartHistory }));

  // Stub other dashboard sub-panels
  await page.route("**/api/usage/records**", (route) =>
    route.fulfill({ json: [] }));
  await page.route("**/api/usage?**", (route) =>
    route.fulfill({ json: { totalInputTokens: 0, totalOutputTokens: 0, totalCost: 0, requestCount: 0, models: [] } }));
  await page.route("**/api/usage", (route) =>
    route.fulfill({ json: { totalInputTokens: 0, totalOutputTokens: 0, totalCost: 0, requestCount: 0, models: [] } }));
  await page.route("**/api/errors?**", (route) =>
    route.fulfill({ json: { totalErrors: 0, recoverableErrors: 0, unrecoverableErrors: 0, byType: [], byAgent: [] } }));
  await page.route("**/api/errors", (route) =>
    route.fulfill({ json: { totalErrors: 0, recoverableErrors: 0, unrecoverableErrors: 0, byType: [], byAgent: [] } }));
  await page.route("**/api/errors/records**", (route) =>
    route.fulfill({ json: [] }));
}

const test = base.extend<{ restartPage: Page }>({
  restartPage: async ({ page }, use) => {
    await mockRestartApis(page);
    await page.goto("/");
    await expect(page).toHaveTitle(/Main Room/);
    const dashTab = page.getByRole("tab", { name: /dashboard/i });
    await dashTab.click({ force: true });
    await use(page);
  },
});

// ── Tests ───────────────────────────────────────────────────────────────

test.describe("RestartHistoryPanel", () => {
  test("renders restart stats cards", async ({ restartPage: page }) => {
    await expect(page.getByText(/Instances \(\d+h\)/)).toBeVisible({ timeout: 5_000 });
    await expect(page.getByText("Crashes", { exact: true })).toBeVisible();
    await expect(page.getByText("Restarts", { exact: true })).toBeVisible();
  });

  test("shows instance count values", async ({ restartPage: page }) => {
    await expect(page.getByText(/Instances \(\d+h\)/)).toBeVisible({ timeout: 5_000 });
    await expect(page.getByText("Clean Stops")).toBeVisible();
    await expect(page.getByText("Running", { exact: true })).toBeVisible();
  });

  test("renders instance history table with columns", async ({ restartPage: page }) => {
    // Table column headers
    await expect(page.getByText("Status").first()).toBeVisible({ timeout: 5_000 });
    await expect(page.getByText("Started").first()).toBeVisible();
    await expect(page.getByText("Version").first()).toBeVisible();
  });

  test("shows running instance", async ({ restartPage: page }) => {
    await expect(page.getByText("0.3.0").first()).toBeVisible({ timeout: 5_000 });
    // Running instance has no shutdown
    await expect(page.getByText(/running/i).first()).toBeVisible();
  });

  test("shows crashed instance", async ({ restartPage: page }) => {
    await expect(page.getByText("0.2.8")).toBeVisible({ timeout: 5_000 });
    // Crashed instance should have indicator
    await expect(page.getByText(/crash/i).first()).toBeVisible();
  });

  test("shows empty state when no instances", async ({ page }) => {
    await mockAllApis(page);
    await page.route("**/api/system/restarts/stats**", (route) =>
      route.fulfill({ json: emptyRestartStats }));
    await page.route("**/api/system/restarts?**", (route) =>
      route.fulfill({ json: emptyRestartHistory }));
    await page.route("**/api/system/restarts", (route) =>
      route.fulfill({ json: emptyRestartHistory }));
    await page.route("**/api/usage/records**", (route) =>
      route.fulfill({ json: [] }));
    await page.route("**/api/usage?**", (route) =>
      route.fulfill({ json: { totalInputTokens: 0, totalOutputTokens: 0, totalCost: 0, requestCount: 0, models: [] } }));
    await page.route("**/api/usage", (route) =>
      route.fulfill({ json: { totalInputTokens: 0, totalOutputTokens: 0, totalCost: 0, requestCount: 0, models: [] } }));
    await page.route("**/api/errors?**", (route) =>
      route.fulfill({ json: { totalErrors: 0, recoverableErrors: 0, unrecoverableErrors: 0, byType: [], byAgent: [] } }));
    await page.route("**/api/errors", (route) =>
      route.fulfill({ json: { totalErrors: 0, recoverableErrors: 0, unrecoverableErrors: 0, byType: [], byAgent: [] } }));
    await page.route("**/api/errors/records**", (route) =>
      route.fulfill({ json: [] }));

    await page.goto("/");
    await expect(page).toHaveTitle(/Main Room/);
    const dashTab = page.getByRole("tab", { name: /dashboard/i });
    await dashTab.click({ force: true });

    await expect(page.getByText("No server instances recorded yet")).toBeVisible({ timeout: 5_000 });
  });

  test("refresh button is present", async ({ restartPage: page }) => {
    await expect(page.getByText(/Instances \(\d+h\)/)).toBeVisible({ timeout: 5_000 });
    const refreshButtons = page.getByRole("button", { name: /refresh/i });
    const count = await refreshButtons.count();
    expect(count).toBeGreaterThan(0);
  });
});
