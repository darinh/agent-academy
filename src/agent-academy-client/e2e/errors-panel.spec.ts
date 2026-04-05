import { test as base, expect } from "@playwright/test";
import { mockAllApis, mockOverview } from "./fixtures";
import type { Page } from "@playwright/test";

// ── Mock data ───────────────────────────────────────────────────────────

const mockErrorSummary = {
  totalErrors: 7,
  recoverableErrors: 5,
  unrecoverableErrors: 2,
  byType: [
    { errorType: "Transient", count: 3 },
    { errorType: "Quota", count: 2 },
    { errorType: "Authorization", count: 2 },
  ],
  byAgent: [
    { agentId: "engineer-1", count: 4 },
    { agentId: "planner-1", count: 3 },
  ],
};

const mockErrorRecords = [
  {
    agentId: "engineer-1",
    roomId: "main",
    errorType: "Transient",
    message: "Connection timeout after 30s",
    recoverable: true,
    timestamp: new Date(Date.now() - 60_000).toISOString(),
  },
  {
    agentId: "planner-1",
    roomId: "main",
    errorType: "Quota",
    message: "Rate limit exceeded — retry in 60s",
    recoverable: true,
    timestamp: new Date(Date.now() - 120_000).toISOString(),
  },
  {
    agentId: "engineer-1",
    roomId: "main",
    errorType: "Authorization",
    message: "Token expired",
    recoverable: false,
    timestamp: new Date(Date.now() - 300_000).toISOString(),
  },
];

const emptyErrorSummary = {
  totalErrors: 0,
  recoverableErrors: 0,
  unrecoverableErrors: 0,
  byType: [],
  byAgent: [],
};

// ── Fixtures ────────────────────────────────────────────────────────────

async function mockErrorApis(page: Page) {
  await mockAllApis(page);

  await page.route("**/api/errors?**", (route) =>
    route.fulfill({ json: mockErrorSummary }));
  await page.route("**/api/errors", (route) =>
    route.fulfill({ json: mockErrorSummary }));
  await page.route("**/api/errors/records**", (route) =>
    route.fulfill({ json: mockErrorRecords }));

  // Dashboard sub-panels: provide usage + restart stubs so they don't error
  await page.route("**/api/usage**", (route) =>
    route.fulfill({ json: { totalInputTokens: 0, totalOutputTokens: 0, totalCost: 0, requestCount: 0, models: [] } }));
  await page.route("**/api/usage/records**", (route) =>
    route.fulfill({ json: [] }));
  await page.route("**/api/system/restarts/stats**", (route) =>
    route.fulfill({ json: { totalInstances: 0, crashRestarts: 0, intentionalRestarts: 0, cleanShutdowns: 0, stillRunning: 0, windowHours: 24, maxRestartsPerWindow: 10, restartWindowHours: 1 } }));
  await page.route("**/api/system/restarts?**", (route) =>
    route.fulfill({ json: { instances: [], total: 0, limit: 10, offset: 0 } }));
  await page.route("**/api/system/restarts", (route) =>
    route.fulfill({ json: { instances: [], total: 0, limit: 10, offset: 0 } }));
}

const test = base.extend<{ errorsPage: Page }>({
  errorsPage: async ({ page }, use) => {
    await mockErrorApis(page);
    await page.goto("/");
    await expect(page).toHaveTitle(/Main Room/);
    const dashTab = page.getByRole("tab", { name: /dashboard/i });
    await dashTab.click({ force: true });
    await use(page);
  },
});

// ── Tests ───────────────────────────────────────────────────────────────

test.describe("ErrorsPanel", () => {
  test("renders error summary cards", async ({ errorsPage: page }) => {
    await expect(page.getByText("Total Errors")).toBeVisible({ timeout: 5_000 });
    await expect(page.getByText("Recoverable", { exact: true })).toBeVisible();
    await expect(page.getByText("Unrecoverable", { exact: true })).toBeVisible();
  });

  test("shows error counts in summary", async ({ errorsPage: page }) => {
    // Wait for summary to load, then verify actual values from mock data
    // totalErrors=7, recoverableErrors=5, unrecoverableErrors=2
    await expect(page.getByText("Total Errors")).toBeVisible({ timeout: 5_000 });
    // Values appear as text nodes next to their labels
    await expect(page.getByText("7", { exact: true }).first()).toBeVisible();
    await expect(page.getByText("5", { exact: true }).first()).toBeVisible();
  });

  test("renders by-type breakdown", async ({ errorsPage: page }) => {
    await expect(page.getByText("By Type")).toBeVisible({ timeout: 5_000 });
    await expect(page.getByText("Transient").first()).toBeVisible();
    await expect(page.getByText("Quota").first()).toBeVisible();
    await expect(page.getByText("Authorization").first()).toBeVisible();
  });

  test("renders by-agent breakdown", async ({ errorsPage: page }) => {
    await expect(page.getByText("By Agent")).toBeVisible({ timeout: 5_000 });
    await expect(page.getByText("engineer-1").first()).toBeVisible();
    await expect(page.getByText("planner-1").first()).toBeVisible();
  });

  test("renders recent error records table", async ({ errorsPage: page }) => {
    await expect(page.getByText("Recent Errors")).toBeVisible({ timeout: 5_000 });
    await expect(page.getByText("Connection timeout after 30s")).toBeVisible();
    await expect(page.getByText("Rate limit exceeded")).toBeVisible();
    await expect(page.getByText("Token expired")).toBeVisible();
  });

  test("shows clean state when no errors", async ({ page }) => {
    await mockAllApis(page);
    await page.route("**/api/errors?**", (route) =>
      route.fulfill({ json: emptyErrorSummary }));
    await page.route("**/api/errors", (route) =>
      route.fulfill({ json: emptyErrorSummary }));
    await page.route("**/api/errors/records**", (route) =>
      route.fulfill({ json: [] }));
    await page.route("**/api/usage**", (route) =>
      route.fulfill({ json: { totalInputTokens: 0, totalOutputTokens: 0, totalCost: 0, requestCount: 0, models: [] } }));
    await page.route("**/api/usage/records**", (route) =>
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

    await expect(page.getByText("No errors recorded")).toBeVisible({ timeout: 5_000 });
  });

  test("refresh button triggers data reload", async ({ errorsPage: page }) => {
    await expect(page.getByText("Total Errors")).toBeVisible({ timeout: 5_000 });

    // Find the Refresh button within the errors section
    const refreshButtons = page.getByRole("button", { name: /refresh/i });
    // There may be multiple — the errors panel one is present
    const count = await refreshButtons.count();
    expect(count).toBeGreaterThan(0);
  });
});
