import { test as base, expect } from "@playwright/test";
import { mockAllApis, mockInstanceHealth } from "./fixtures";
import type { Page } from "@playwright/test";

// ── Helpers ─────────────────────────────────────────────────────────────

/** Stub all dashboard sub-panel APIs to avoid fetch errors. */
async function stubDashboardPanels(page: Page) {
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

async function setupWithCircuitBreaker(page: Page, cbState: string | undefined) {
  await mockAllApis(page);
  // Override the default instance health mock with circuit breaker state
  await page.route("**/api/health/instance", (route) =>
    route.fulfill({
      json: {
        ...mockInstanceHealth,
        circuitBreakerState: cbState,
      },
    }),
  );
  await stubDashboardPanels(page);
}

// ── Fixtures ────────────────────────────────────────────────────────────

const testOpen = base.extend<{ cbOpenPage: Page }>({
  cbOpenPage: async ({ page }, use) => {
    await setupWithCircuitBreaker(page, "Open");
    await page.goto("/");
    await expect(page).toHaveTitle(/Main Room/);
    await use(page);
  },
});

const testHalfOpen = base.extend<{ cbHalfOpenPage: Page }>({
  cbHalfOpenPage: async ({ page }, use) => {
    await setupWithCircuitBreaker(page, "HalfOpen");
    await page.goto("/");
    await expect(page).toHaveTitle(/Main Room/);
    await use(page);
  },
});

const testClosed = base.extend<{ cbClosedPage: Page }>({
  cbClosedPage: async ({ page }, use) => {
    await setupWithCircuitBreaker(page, "Closed");
    await page.goto("/");
    await expect(page).toHaveTitle(/Main Room/);
    await use(page);
  },
});

// ── Tests: Banner ───────────────────────────────────────────────────────

testOpen.describe("Circuit breaker banner — Open", () => {
  testOpen("shows banner when circuit breaker is Open", async ({ cbOpenPage: page }) => {
    const banner = page.getByTestId("circuit-breaker-banner");
    await expect(banner).toBeVisible({ timeout: 5_000 });
  });

  testOpen("banner has alert role for accessibility", async ({ cbOpenPage: page }) => {
    const banner = page.getByTestId("circuit-breaker-banner");
    await expect(banner).toBeVisible({ timeout: 5_000 });
    await expect(banner).toHaveAttribute("role", "alert");
  });

  testOpen("shows Open state label", async ({ cbOpenPage: page }) => {
    const banner = page.getByTestId("circuit-breaker-banner");
    await expect(banner).toBeVisible({ timeout: 5_000 });
    await expect(banner.getByText("Circuit breaker open")).toBeVisible();
  });

  testOpen("shows Open state messaging", async ({ cbOpenPage: page }) => {
    const banner = page.getByTestId("circuit-breaker-banner");
    await expect(banner).toBeVisible({ timeout: 5_000 });
    await expect(banner.getByText("Agent requests are temporarily blocked")).toBeVisible();
    await expect(banner.getByText(/cooldown period/)).toBeVisible();
  });
});

testHalfOpen.describe("Circuit breaker banner — HalfOpen", () => {
  testHalfOpen("shows banner when circuit breaker is HalfOpen", async ({ cbHalfOpenPage: page }) => {
    const banner = page.getByTestId("circuit-breaker-banner");
    await expect(banner).toBeVisible({ timeout: 5_000 });
  });

  testHalfOpen("shows HalfOpen state label", async ({ cbHalfOpenPage: page }) => {
    const banner = page.getByTestId("circuit-breaker-banner");
    await expect(banner).toBeVisible({ timeout: 5_000 });
    await expect(banner.getByText("Circuit breaker probing")).toBeVisible();
  });

  testHalfOpen("shows HalfOpen state messaging", async ({ cbHalfOpenPage: page }) => {
    const banner = page.getByTestId("circuit-breaker-banner");
    await expect(banner).toBeVisible({ timeout: 5_000 });
    await expect(banner.getByText("Testing backend recovery with a probe request")).toBeVisible();
    await expect(banner.getByText(/probe request is being sent/)).toBeVisible();
  });
});

testClosed.describe("Circuit breaker banner — Closed", () => {
  testClosed("does not show banner when circuit breaker is Closed", async ({ cbClosedPage: page }) => {
    // Wait for the page to settle, then confirm no banner
    await page.waitForTimeout(1_000);
    await expect(page.getByTestId("circuit-breaker-banner")).not.toBeVisible();
  });
});

// ── Tests: Header chip ──────────────────────────────────────────────────

testOpen.describe("Circuit breaker header chip — Open", () => {
  testOpen("shows 'Circuit open' chip in workspace header", async ({ cbOpenPage: page }) => {
    await expect(page.getByText("Circuit open", { exact: true })).toBeVisible({ timeout: 5_000 });
  });
});

testHalfOpen.describe("Circuit breaker header chip — HalfOpen", () => {
  testHalfOpen("shows 'Circuit probing' chip in workspace header", async ({ cbHalfOpenPage: page }) => {
    await expect(page.getByText("Circuit probing", { exact: true })).toBeVisible({ timeout: 5_000 });
  });
});

testClosed.describe("Circuit breaker header chip — Closed", () => {
  testClosed("does not show circuit chip when Closed", async ({ cbClosedPage: page }) => {
    await page.waitForTimeout(1_000);
    await expect(page.getByText("Circuit open", { exact: true })).not.toBeVisible();
    await expect(page.getByText("Circuit probing", { exact: true })).not.toBeVisible();
  });
});

// ── Tests: ErrorsPanel circuit breaker status ───────────────────────────

testOpen.describe("Circuit breaker in ErrorsPanel — Open", () => {
  testOpen("shows circuit breaker status row in ErrorsPanel", async ({ cbOpenPage: page }) => {
    const dashTab = page.getByRole("tab", { name: /dashboard/i });
    await dashTab.click({ force: true });

    const cbStatus = page.getByTestId("circuit-breaker-status");
    await expect(cbStatus).toBeVisible({ timeout: 5_000 });
    await expect(cbStatus.getByText("Circuit Open")).toBeVisible();
    await expect(cbStatus.getByText(/requests are blocked/)).toBeVisible();
  });
});

testHalfOpen.describe("Circuit breaker in ErrorsPanel — HalfOpen", () => {
  testHalfOpen("shows half-open status row in ErrorsPanel", async ({ cbHalfOpenPage: page }) => {
    const dashTab = page.getByRole("tab", { name: /dashboard/i });
    await dashTab.click({ force: true });

    const cbStatus = page.getByTestId("circuit-breaker-status");
    await expect(cbStatus).toBeVisible({ timeout: 5_000 });
    await expect(cbStatus.getByText("Circuit Half-Open")).toBeVisible();
    await expect(cbStatus.getByText(/Probing/)).toBeVisible();
  });
});
