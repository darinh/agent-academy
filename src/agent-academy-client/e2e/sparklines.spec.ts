import { test as base, expect } from "@playwright/test";
import { mockAllApis } from "./fixtures";
import type { Page } from "@playwright/test";

// ── Mock data with enough records for sparklines (need >= 2) ────────────

function ts(minutesAgo: number): string {
  return new Date(Date.now() - minutesAgo * 60_000).toISOString();
}

const mockUsageRecords = [
  { id: "u1", agentId: "engineer-1", roomId: "main", model: "gpt-4o", inputTokens: 5000, outputTokens: 1200, cacheReadTokens: 0, cacheWriteTokens: 0, cost: 0.04, durationMs: 2000, reasoningEffort: null, recordedAt: ts(10) },
  { id: "u2", agentId: "planner-1", roomId: "main", model: "gpt-4o", inputTokens: 3000, outputTokens: 900, cacheReadTokens: 0, cacheWriteTokens: 0, cost: 0.03, durationMs: 1500, reasoningEffort: null, recordedAt: ts(30) },
  { id: "u3", agentId: "engineer-1", roomId: "main", model: "gpt-4o", inputTokens: 4000, outputTokens: 1100, cacheReadTokens: 0, cacheWriteTokens: 0, cost: 0.035, durationMs: 1800, reasoningEffort: null, recordedAt: ts(60) },
];

const mockUsageSummary = {
  totalInputTokens: 12000,
  totalOutputTokens: 3200,
  totalCost: 0.105,
  requestCount: 3,
  models: ["gpt-4o"],
};

const mockErrorRecords = [
  { agentId: "engineer-1", roomId: "main", errorType: "Transient", message: "Timeout", recoverable: true, timestamp: ts(5) },
  { agentId: "planner-1", roomId: "main", errorType: "Quota", message: "Rate limited", recoverable: true, timestamp: ts(20) },
  { agentId: "engineer-1", roomId: "main", errorType: "Authorization", message: "Token expired", recoverable: false, timestamp: ts(45) },
];

const mockErrorSummary = {
  totalErrors: 3,
  recoverableErrors: 2,
  unrecoverableErrors: 1,
  byType: [
    { errorType: "Transient", count: 1 },
    { errorType: "Quota", count: 1 },
    { errorType: "Authorization", count: 1 },
  ],
  byAgent: [
    { agentId: "engineer-1", count: 2 },
    { agentId: "planner-1", count: 1 },
  ],
};

const mockAuditRecords = {
  records: [
    { id: "a1", correlationId: "c1", agentId: "engineer-1", source: null, command: "READ_FILE", status: "Success", errorMessage: null, errorCode: null, roomId: "main", timestamp: ts(10) },
    { id: "a2", correlationId: "c2", agentId: "planner-1", source: null, command: "RUN_BUILD", status: "Success", errorMessage: null, errorCode: null, roomId: "main", timestamp: ts(30) },
    { id: "a3", correlationId: "c3", agentId: "engineer-1", source: null, command: "SEARCH_CODE", status: "Error", errorMessage: "Not found", errorCode: "EXECUTION", roomId: "main", timestamp: ts(60) },
  ],
  total: 3,
  limit: 200,
  offset: 0,
};

const mockAuditStats = {
  totalCommands: 3,
  byStatus: { Success: 2, Error: 1 },
  byAgent: { "engineer-1": 2, "planner-1": 1 },
  byCommand: { READ_FILE: 1, RUN_BUILD: 1, SEARCH_CODE: 1 },
  windowHours: null,
};

// ── Helpers ─────────────────────────────────────────────────────────────

const stubRestarts = async (page: Page) => {
  await page.route("**/api/system/restarts/stats**", (route) =>
    route.fulfill({ json: { totalInstances: 0, crashRestarts: 0, intentionalRestarts: 0, cleanShutdowns: 0, stillRunning: 0, windowHours: 24, maxRestartsPerWindow: 10, restartWindowHours: 1 } }));
  await page.route("**/api/system/restarts?**", (route) =>
    route.fulfill({ json: { instances: [], total: 0, limit: 10, offset: 0 } }));
  await page.route("**/api/system/restarts", (route) =>
    route.fulfill({ json: { instances: [], total: 0, limit: 10, offset: 0 } }));
};

// ── UsagePanel sparkline fixture ────────────────────────────────────────

async function mockUsageWithRecords(page: Page) {
  await mockAllApis(page);
  await page.route("**/api/usage/records**", (route) =>
    route.fulfill({ json: mockUsageRecords }));
  await page.route("**/api/usage?**", (route) =>
    route.fulfill({ json: mockUsageSummary }));
  await page.route("**/api/usage", (route) =>
    route.fulfill({ json: mockUsageSummary }));
  // Stub siblings
  await page.route("**/api/errors?**", (route) =>
    route.fulfill({ json: { totalErrors: 0, recoverableErrors: 0, unrecoverableErrors: 0, byType: [], byAgent: [] } }));
  await page.route("**/api/errors", (route) =>
    route.fulfill({ json: { totalErrors: 0, recoverableErrors: 0, unrecoverableErrors: 0, byType: [], byAgent: [] } }));
  await page.route("**/api/errors/records**", (route) =>
    route.fulfill({ json: [] }));
  await stubRestarts(page);
}

// ── ErrorsPanel sparkline fixture ───────────────────────────────────────

async function mockErrorsWithRecords(page: Page) {
  await mockAllApis(page);
  await page.route("**/api/errors?**", (route) =>
    route.fulfill({ json: mockErrorSummary }));
  await page.route("**/api/errors", (route) =>
    route.fulfill({ json: mockErrorSummary }));
  await page.route("**/api/errors/records**", (route) =>
    route.fulfill({ json: mockErrorRecords }));
  // Stub siblings
  await page.route("**/api/usage**", (route) =>
    route.fulfill({ json: { totalInputTokens: 0, totalOutputTokens: 0, totalCost: 0, requestCount: 0, models: [] } }));
  await page.route("**/api/usage/records**", (route) =>
    route.fulfill({ json: [] }));
  await stubRestarts(page);
}

// ── AuditLogPanel sparkline fixture ─────────────────────────────────────

async function mockAuditWithRecords(page: Page) {
  await mockAllApis(page);
  await page.route("**/api/commands/audit/stats**", (route) =>
    route.fulfill({ json: mockAuditStats }));
  await page.route("**/api/commands/audit?**", (route) =>
    route.fulfill({ json: mockAuditRecords }));
  await page.route("**/api/commands/audit", (route) =>
    route.fulfill({ json: mockAuditRecords }));
  // Stub siblings
  await page.route("**/api/errors?**", (route) =>
    route.fulfill({ json: { totalErrors: 0, recoverableErrors: 0, unrecoverableErrors: 0, byType: [], byAgent: [] } }));
  await page.route("**/api/errors", (route) =>
    route.fulfill({ json: { totalErrors: 0, recoverableErrors: 0, unrecoverableErrors: 0, byType: [], byAgent: [] } }));
  await page.route("**/api/errors/records**", (route) =>
    route.fulfill({ json: [] }));
  await page.route("**/api/usage**", (route) =>
    route.fulfill({ json: { totalInputTokens: 0, totalOutputTokens: 0, totalCost: 0, requestCount: 0, models: [] } }));
  await page.route("**/api/usage/records**", (route) =>
    route.fulfill({ json: [] }));
  await stubRestarts(page);
}

// ── Test fixtures ───────────────────────────────────────────────────────

const usageTest = base.extend<{ usagePage: Page }>({
  usagePage: async ({ page }, use) => {
    await mockUsageWithRecords(page);
    await page.goto("/");
    await expect(page).toHaveTitle(/Main Room/);
    await page.getByRole("tab", { name: /dashboard/i }).click({ force: true });
    await use(page);
  },
});

const errorsTest = base.extend<{ errorsPage: Page }>({
  errorsPage: async ({ page }, use) => {
    await mockErrorsWithRecords(page);
    await page.goto("/");
    await expect(page).toHaveTitle(/Main Room/);
    await page.getByRole("tab", { name: /dashboard/i }).click({ force: true });
    await use(page);
  },
});

const auditTest = base.extend<{ auditPage: Page }>({
  auditPage: async ({ page }, use) => {
    await mockAuditWithRecords(page);
    await page.goto("/");
    await expect(page).toHaveTitle(/Main Room/);
    await page.getByRole("tab", { name: /dashboard/i }).click({ force: true });
    await use(page);
  },
});

// ── Tests: UsagePanel sparklines ────────────────────────────────────────

usageTest.describe("UsagePanel sparklines", () => {
  usageTest("renders request count sparkline", async ({ usagePage: page }) => {
    const sparkline = page.getByTestId("usage-sparkline-requests");
    await expect(sparkline).toBeVisible({ timeout: 5_000 });
    await expect(sparkline.getByText("Requests")).toBeVisible();
    // SVG sparkline should be present inside the container
    await expect(sparkline.locator("svg[role='img']")).toBeVisible();
  });

  usageTest("renders token volume sparkline", async ({ usagePage: page }) => {
    const sparkline = page.getByTestId("usage-sparkline-tokens");
    await expect(sparkline).toBeVisible({ timeout: 5_000 });
    await expect(sparkline.getByText("Tokens")).toBeVisible();
    await expect(sparkline.locator("svg[role='img']")).toBeVisible();
  });

  usageTest("sparklines have proper aria labels", async ({ usagePage: page }) => {
    const sparklines = page.locator("svg[aria-label='Sparkline trend']");
    // UsagePanel shows 2 sparklines (requests + tokens)
    await expect(sparklines.first()).toBeVisible({ timeout: 5_000 });
    const count = await sparklines.count();
    expect(count).toBeGreaterThanOrEqual(2);
  });

  usageTest("sparklines contain SVG polyline elements", async ({ usagePage: page }) => {
    const sparkline = page.getByTestId("usage-sparkline-requests");
    await expect(sparkline).toBeVisible({ timeout: 5_000 });
    // Sparkline renders a polyline (trend line) and polygon (gradient fill)
    await expect(sparkline.locator("polyline")).toBeVisible();
    await expect(sparkline.locator("polygon")).toBeVisible();
  });
});

// ── Tests: ErrorsPanel sparkline ────────────────────────────────────────

errorsTest.describe("ErrorsPanel sparkline", () => {
  errorsTest("renders error rate sparkline", async ({ errorsPage: page }) => {
    const sparkline = page.getByTestId("errors-sparkline");
    await expect(sparkline).toBeVisible({ timeout: 5_000 });
    await expect(sparkline.getByText("Error Rate")).toBeVisible();
    await expect(sparkline.locator("svg[role='img']")).toBeVisible();
  });

  errorsTest("error sparkline contains SVG polyline", async ({ errorsPage: page }) => {
    const sparkline = page.getByTestId("errors-sparkline");
    await expect(sparkline).toBeVisible({ timeout: 5_000 });
    await expect(sparkline.locator("polyline")).toBeVisible();
  });
});

// ── Tests: AuditLogPanel sparkline ──────────────────────────────────────

auditTest.describe("AuditLogPanel sparkline", () => {
  auditTest("renders command trend sparkline", async ({ auditPage: page }) => {
    const sparkline = page.getByTestId("audit-sparkline");
    await expect(sparkline).toBeVisible({ timeout: 5_000 });
    await expect(sparkline.getByText("Commands")).toBeVisible();
    await expect(sparkline.locator("svg[role='img']")).toBeVisible();
  });

  auditTest("audit sparkline contains SVG polyline", async ({ auditPage: page }) => {
    const sparkline = page.getByTestId("audit-sparkline");
    await expect(sparkline).toBeVisible({ timeout: 5_000 });
    await expect(sparkline.locator("polyline")).toBeVisible();
  });
});

// ── Tests: No sparklines with insufficient data ─────────────────────────

const noDataTest = base.extend<{ emptyPage: Page }>({
  emptyPage: async ({ page }, use) => {
    await mockAllApis(page);
    // All panels return empty / single-record data — no sparklines
    await page.route("**/api/usage/records**", (route) =>
      route.fulfill({ json: [{ id: "u1", agentId: "a", roomId: "r", model: "m", inputTokens: 1, outputTokens: 1, cacheReadTokens: 0, cacheWriteTokens: 0, cost: 0, durationMs: 1, reasoningEffort: null, recordedAt: ts(10) }] }));
    await page.route("**/api/usage?**", (route) =>
      route.fulfill({ json: { totalInputTokens: 1, totalOutputTokens: 1, totalCost: 0, requestCount: 1, models: ["m"] } }));
    await page.route("**/api/usage", (route) =>
      route.fulfill({ json: { totalInputTokens: 1, totalOutputTokens: 1, totalCost: 0, requestCount: 1, models: ["m"] } }));
    await page.route("**/api/errors?**", (route) =>
      route.fulfill({ json: { totalErrors: 1, recoverableErrors: 1, unrecoverableErrors: 0, byType: [{ errorType: "Transient", count: 1 }], byAgent: [{ agentId: "a", count: 1 }] } }));
    await page.route("**/api/errors", (route) =>
      route.fulfill({ json: { totalErrors: 1, recoverableErrors: 1, unrecoverableErrors: 0, byType: [{ errorType: "Transient", count: 1 }], byAgent: [{ agentId: "a", count: 1 }] } }));
    await page.route("**/api/errors/records**", (route) =>
      route.fulfill({ json: [{ agentId: "a", roomId: "r", errorType: "Transient", message: "err", recoverable: true, timestamp: ts(10) }] }));
    await page.route("**/api/commands/audit/stats**", (route) =>
      route.fulfill({ json: { totalCommands: 1, byStatus: { Success: 1 }, byAgent: { a: 1 }, byCommand: { X: 1 }, windowHours: null } }));
    await page.route("**/api/commands/audit?**", (route) =>
      route.fulfill({ json: { records: [{ id: "a1", correlationId: "c1", agentId: "a", source: null, command: "X", status: "Success", errorMessage: null, errorCode: null, roomId: "r", timestamp: ts(10) }], total: 1, limit: 200, offset: 0 } }));
    await page.route("**/api/commands/audit", (route) =>
      route.fulfill({ json: { records: [{ id: "a1", correlationId: "c1", agentId: "a", source: null, command: "X", status: "Success", errorMessage: null, errorCode: null, roomId: "r", timestamp: ts(10) }], total: 1, limit: 200, offset: 0 } }));
    await stubRestarts(page);

    await page.goto("/");
    await expect(page).toHaveTitle(/Main Room/);
    await page.getByRole("tab", { name: /dashboard/i }).click({ force: true });
    await use(page);
  },
});

noDataTest.describe("Sparklines with insufficient data", () => {
  noDataTest("no usage sparklines when only 1 record", async ({ emptyPage: page }) => {
    // Wait for usage panel to render
    await expect(page.getByText("LLM Calls", { exact: true })).toBeVisible({ timeout: 5_000 });
    // Sparkline containers should not exist
    await expect(page.getByTestId("usage-sparkline-requests")).not.toBeVisible();
    await expect(page.getByTestId("usage-sparkline-tokens")).not.toBeVisible();
  });

  noDataTest("no error sparkline when only 1 record", async ({ emptyPage: page }) => {
    await expect(page.getByText("Total Errors")).toBeVisible({ timeout: 5_000 });
    await expect(page.getByTestId("errors-sparkline")).not.toBeVisible();
  });

  noDataTest("no audit sparkline when only 1 record", async ({ emptyPage: page }) => {
    // The audit stat card label is just "Total", not "Total Commands"
    await expect(page.getByText("Total", { exact: true }).first()).toBeVisible({ timeout: 5_000 });
    await expect(page.getByTestId("audit-sparkline")).not.toBeVisible();
  });
});
