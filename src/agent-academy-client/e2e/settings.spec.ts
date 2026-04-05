import { test as baseTest, expect, mockAgents, mockAuthStatusAuthenticated, mockAllApis } from "./fixtures";
import type { Page } from "@playwright/test";

/**
 * Extended fixture that uses authenticated mock (UserBadge + Settings available).
 * Auth override is registered AFTER mockAllApis so it takes priority.
 */
const test = baseTest.extend<{ authedPage: Page }>({
  authedPage: async ({ page }, use) => {
    await mockAllApis(page);
    // Override auth AFTER mockAllApis — Playwright matches last-registered route first
    await page.route("**/api/auth/status", (route) =>
      route.fulfill({ json: mockAuthStatusAuthenticated }));
    await use(page);
  },
});

/**
 * Helper to open the settings panel via the User menu.
 */
async function openSettings(page: Page) {
  const trigger = page.getByLabel("User menu");
  await expect(trigger).toBeVisible({ timeout: 5_000 });
  await trigger.click();
  const settingsItem = page.getByRole("menuitem", { name: /settings/i });
  await expect(settingsItem).toBeVisible({ timeout: 3_000 });
  await settingsItem.click();
}

test.describe("Settings panel", () => {
  test("opens settings from user menu", async ({ authedPage: page }) => {
    await page.goto("/");
    await expect(page).toHaveTitle(/Main Room/, { timeout: 10_000 });

    await openSettings(page);

    // Settings panel header should be visible
    await expect(page.getByText("Agents").first()).toBeVisible({ timeout: 5_000 });
  });

  test("settings panel shows configured agents", async ({ authedPage: page }) => {
    await page.goto("/");
    await expect(page).toHaveTitle(/Main Room/, { timeout: 10_000 });

    await openSettings(page);

    // Agent names should be visible
    for (const agent of mockAgents) {
      await expect(page.getByText(agent.name).first()).toBeVisible({ timeout: 3_000 });
    }
  });

  test("settings panel closes and returns to workspace", async ({ authedPage: page }) => {
    await page.goto("/");
    await expect(page).toHaveTitle(/Main Room/, { timeout: 10_000 });

    await openSettings(page);
    await expect(page.getByText("Agents").first()).toBeVisible({ timeout: 5_000 });

    // Press Escape to close settings overlay
    await page.keyboard.press("Escape");

    // Workspace tabs should be visible again
    await expect(page.getByRole("tab", { name: /conversation/i })).toBeVisible({ timeout: 3_000 });
  });
});
