import { test as base, expect } from "@playwright/test";
import { mockAllApis, mockOverview } from "./fixtures";
import type { Page } from "@playwright/test";

// ── Mock data ───────────────────────────────────────────────────────────

const mockAuditStats = {
  totalCommands: 25,
  byStatus: { Success: 20, Error: 3, Denied: 2 },
  byAgent: { "architect": 10, "engineer-1": 8, "human": 7 },
  byCommand: { READ_FILE: 8, SEARCH_CODE: 6, RUN_BUILD: 5, LIST_ROOMS: 4, RUN_TESTS: 2 },
  windowHours: null,
};

const mockAuditRecords = {
  records: [
    {
      id: "a1",
      correlationId: "cmd-001",
      agentId: "architect",
      source: null,
      command: "READ_FILE",
      status: "Success",
      errorMessage: null,
      errorCode: null,
      roomId: "main",
      timestamp: new Date(Date.now() - 60_000).toISOString(),
    },
    {
      id: "a2",
      correlationId: "cmd-002",
      agentId: "engineer-1",
      source: null,
      command: "RUN_BUILD",
      status: "Error",
      errorMessage: "Build failed: exit code 1",
      errorCode: "EXECUTION",
      roomId: "main",
      timestamp: new Date(Date.now() - 120_000).toISOString(),
    },
    {
      id: "a3",
      correlationId: "cmd-003",
      agentId: "human",
      source: "human-ui",
      command: "LIST_ROOMS",
      status: "Success",
      errorMessage: null,
      errorCode: null,
      roomId: null,
      timestamp: new Date(Date.now() - 300_000).toISOString(),
    },
    {
      id: "a4",
      correlationId: "cmd-004",
      agentId: "engineer-1",
      source: null,
      command: "SEARCH_CODE",
      status: "Denied",
      errorMessage: "Agent not authorized",
      errorCode: "PERMISSION",
      roomId: "main",
      timestamp: new Date(Date.now() - 600_000).toISOString(),
    },
  ],
  total: 25,
  limit: 15,
  offset: 0,
};

const emptyAuditStats = {
  totalCommands: 0,
  byStatus: {},
  byAgent: {},
  byCommand: {},
  windowHours: null,
};

const emptyAuditRecords = {
  records: [],
  total: 0,
  limit: 15,
  offset: 0,
};

// ── Fixtures ────────────────────────────────────────────────────────────

async function mockAuditApis(page: Page) {
  await mockAllApis(page);

  // Override the default empty audit stubs with populated data
  await page.route("**/api/commands/audit/stats**", (route) =>
    route.fulfill({ json: mockAuditStats }));
  await page.route("**/api/commands/audit?**", (route) =>
    route.fulfill({ json: mockAuditRecords }));
  await page.route("**/api/commands/audit", (route) =>
    route.fulfill({ json: mockAuditRecords }));

  // Dashboard sibling panel stubs
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
  await page.route("**/api/system/restarts/stats**", (route) =>
    route.fulfill({ json: { totalInstances: 0, crashRestarts: 0, intentionalRestarts: 0, cleanShutdowns: 0, stillRunning: 0, windowHours: 24, maxRestartsPerWindow: 10, restartWindowHours: 1 } }));
  await page.route("**/api/system/restarts?**", (route) =>
    route.fulfill({ json: { instances: [], total: 0, limit: 10, offset: 0 } }));
  await page.route("**/api/system/restarts", (route) =>
    route.fulfill({ json: { instances: [], total: 0, limit: 10, offset: 0 } }));
}

async function mockEmptyAuditApis(page: Page) {
  await mockAllApis(page);

  await page.route("**/api/commands/audit/stats**", (route) =>
    route.fulfill({ json: emptyAuditStats }));
  await page.route("**/api/commands/audit?**", (route) =>
    route.fulfill({ json: emptyAuditRecords }));
  await page.route("**/api/commands/audit", (route) =>
    route.fulfill({ json: emptyAuditRecords }));

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
  await page.route("**/api/system/restarts/stats**", (route) =>
    route.fulfill({ json: { totalInstances: 0, crashRestarts: 0, intentionalRestarts: 0, cleanShutdowns: 0, stillRunning: 0, windowHours: 24, maxRestartsPerWindow: 10, restartWindowHours: 1 } }));
  await page.route("**/api/system/restarts?**", (route) =>
    route.fulfill({ json: { instances: [], total: 0, limit: 10, offset: 0 } }));
  await page.route("**/api/system/restarts", (route) =>
    route.fulfill({ json: { instances: [], total: 0, limit: 10, offset: 0 } }));
}

const test = base.extend<{ auditPage: Page; emptyAuditPage: Page }>({
  auditPage: async ({ page }, use) => {
    await mockAuditApis(page);
    await page.goto("/");
    await expect(page).toHaveTitle(/Main Room/);
    const dashTab = page.getByRole("tab", { name: /dashboard/i });
    await dashTab.click({ force: true });
    await use(page);
  },
  emptyAuditPage: async ({ page }, use) => {
    await mockEmptyAuditApis(page);
    await page.goto("/");
    await expect(page).toHaveTitle(/Main Room/);
    const dashTab = page.getByRole("tab", { name: /dashboard/i });
    await dashTab.click({ force: true });
    await use(page);
  },
});

// ── Tests ───────────────────────────────────────────────────────────────

test.describe("AuditLogPanel", () => {
  test("renders section heading", async ({ auditPage: page }) => {
    await expect(page.getByText("Command Audit Log")).toBeVisible({ timeout: 5_000 });
  });

  test("renders stat cards with counts", async ({ auditPage: page }) => {
    await expect(page.getByText("Command Audit Log")).toBeVisible({ timeout: 5_000 });
    // Total=25, Success=20, Errors=3, Denied=2
    await expect(page.getByText("Total", { exact: true }).first()).toBeVisible();
    await expect(page.getByText("25", { exact: true }).first()).toBeVisible();
    await expect(page.getByText("20", { exact: true }).first()).toBeVisible();
  });

  test("renders by-agent breakdown", async ({ auditPage: page }) => {
    await expect(page.getByText("By Agent")).toBeVisible({ timeout: 5_000 });
    await expect(page.getByText("architect").first()).toBeVisible();
    await expect(page.getByText("engineer-1").first()).toBeVisible();
  });

  test("renders top commands breakdown", async ({ auditPage: page }) => {
    await expect(page.getByText("Top Commands")).toBeVisible({ timeout: 5_000 });
    await expect(page.getByText("READ_FILE").first()).toBeVisible();
    await expect(page.getByText("SEARCH_CODE").first()).toBeVisible();
    await expect(page.getByText("RUN_BUILD").first()).toBeVisible();
  });

  test("renders recent commands heading with count", async ({ auditPage: page }) => {
    await expect(page.getByText("Recent Commands")).toBeVisible({ timeout: 5_000 });
    // total=25 shown as badge
    await expect(page.getByText("25", { exact: true }).first()).toBeVisible();
  });

  test("renders command records in table", async ({ auditPage: page }) => {
    await expect(page.getByText("Recent Commands")).toBeVisible({ timeout: 5_000 });
    // Records should show agent names, commands, and statuses
    await expect(page.getByText("READ_FILE").first()).toBeVisible();
    await expect(page.getByText("RUN_BUILD").first()).toBeVisible();
    await expect(page.getByText("LIST_ROOMS").first()).toBeVisible();
  });

  test("shows status badges for different statuses", async ({ auditPage: page }) => {
    await expect(page.getByText("Recent Commands")).toBeVisible({ timeout: 5_000 });
    await expect(page.getByText("Success", { exact: true }).first()).toBeVisible();
    await expect(page.getByText("Error", { exact: true }).first()).toBeVisible();
    await expect(page.getByText("Denied", { exact: true }).first()).toBeVisible();
  });

  test("shows error details for failed commands", async ({ auditPage: page }) => {
    await expect(page.getByText("Recent Commands")).toBeVisible({ timeout: 5_000 });
    await expect(page.getByText(/EXECUTION/).first()).toBeVisible();
    await expect(page.getByText(/Build failed/).first()).toBeVisible();
  });

  test("distinguishes human-ui commands visually", async ({ auditPage: page }) => {
    await expect(page.getByText("Recent Commands")).toBeVisible({ timeout: 5_000 });
    // "human" agent badge should appear
    await expect(page.getByText("human", { exact: true }).first()).toBeVisible();
  });

  test("shows refresh button", async ({ auditPage: page }) => {
    await expect(page.getByText("Command Audit Log")).toBeVisible({ timeout: 5_000 });
    // Find refresh button within the audit section
    const auditSection = page.getByText("Recent Commands").locator("..");
    await expect(auditSection.getByText("Refresh")).toBeVisible();
  });

  test("shows empty state when no commands recorded", async ({ emptyAuditPage: page }) => {
    await expect(page.getByText("Command Audit Log")).toBeVisible({ timeout: 5_000 });
    await expect(page.getByText("No commands recorded yet.")).toBeVisible();
  });
});
