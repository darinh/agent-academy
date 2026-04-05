import { test, expect, mockRoom, mockAgents } from "./fixtures";
import type { Page } from "@playwright/test";

/** Navigate to the Overview tab */
async function goToOverview(page: Page) {
  await page.goto("/");
  await expect(page).toHaveTitle(/Main Room/, { timeout: 10_000 });

  const tab = page.getByRole("tab", { name: /overview/i });
  await expect(tab).toBeVisible({ timeout: 5_000 });
  await tab.click();
}

test.describe("Overview panel", () => {
  test("shows current phase section with room name", async ({ mockedPage: page }) => {
    // Mock room stats endpoints
    await page.route("**/api/rooms/main/usage", (route) =>
      route.fulfill({ json: { totalRequests: 10, totalInputTokens: 5000, totalOutputTokens: 3000, totalCost: 0.15 } }));
    await page.route("**/api/rooms/main/usage/agents", (route) =>
      route.fulfill({ json: [] }));
    await page.route("**/api/rooms/main/errors**", (route) =>
      route.fulfill({ json: [] }));

    await goToOverview(page);

    // Current phase section with room name
    await expect(page.getByText(/current phase/i).first()).toBeVisible({ timeout: 5_000 });
    await expect(page.getByText("Main Room").first()).toBeVisible({ timeout: 3_000 });
  });

  test("shows phase transition buttons", async ({ mockedPage: page }) => {
    await page.route("**/api/rooms/main/usage", (route) =>
      route.fulfill({ json: { totalRequests: 0, totalInputTokens: 0, totalOutputTokens: 0, totalCost: 0 } }));
    await page.route("**/api/rooms/main/usage/agents", (route) =>
      route.fulfill({ json: [] }));
    await page.route("**/api/rooms/main/errors**", (route) =>
      route.fulfill({ json: [] }));

    await goToOverview(page);

    // Phase buttons should be visible
    const phaseNames = ["Intake", "Planning", "Discussion", "Validation", "Implementation", "FinalSynthesis"];
    for (const name of phaseNames) {
      await expect(page.getByRole("button", { name }).first()).toBeVisible({ timeout: 5_000 });
    }
  });

  test("current phase button is disabled", async ({ mockedPage: page }) => {
    await page.route("**/api/rooms/main/usage", (route) =>
      route.fulfill({ json: { totalRequests: 0, totalInputTokens: 0, totalOutputTokens: 0, totalCost: 0 } }));
    await page.route("**/api/rooms/main/usage/agents", (route) =>
      route.fulfill({ json: [] }));
    await page.route("**/api/rooms/main/errors**", (route) =>
      route.fulfill({ json: [] }));

    await goToOverview(page);

    // mockRoom has currentPhase: "Planning" — that button should be disabled
    const planningBtn = page.getByRole("button", { name: "Planning" }).first();
    await expect(planningBtn).toBeVisible({ timeout: 5_000 });
    await expect(planningBtn).toBeDisabled();
  });

  test("shows room status summary section", async ({ mockedPage: page }) => {
    await page.route("**/api/rooms/main/usage", (route) =>
      route.fulfill({ json: { totalRequests: 0, totalInputTokens: 0, totalOutputTokens: 0, totalCost: 0 } }));
    await page.route("**/api/rooms/main/usage/agents", (route) =>
      route.fulfill({ json: [] }));
    await page.route("**/api/rooms/main/errors**", (route) =>
      route.fulfill({ json: [] }));

    await goToOverview(page);

    await expect(page.getByText("Room Status Summary")).toBeVisible({ timeout: 5_000 });
  });

  test("shows room stats section", async ({ mockedPage: page }) => {
    await page.route("**/api/rooms/main/usage", (route) =>
      route.fulfill({ json: { totalRequests: 10, totalInputTokens: 5000, totalOutputTokens: 3000, totalCost: 0.15 } }));
    await page.route("**/api/rooms/main/usage/agents", (route) =>
      route.fulfill({ json: [] }));
    await page.route("**/api/rooms/main/errors**", (route) =>
      route.fulfill({ json: [] }));

    await goToOverview(page);

    // Room stats section header
    await expect(page.getByText(/room stats/i).first()).toBeVisible({ timeout: 5_000 });
  });

  test("shows room card with status badge", async ({ mockedPage: page }) => {
    await page.route("**/api/rooms/main/usage", (route) =>
      route.fulfill({ json: { totalRequests: 0, totalInputTokens: 0, totalOutputTokens: 0, totalCost: 0 } }));
    await page.route("**/api/rooms/main/usage/agents", (route) =>
      route.fulfill({ json: [] }));
    await page.route("**/api/rooms/main/errors**", (route) =>
      route.fulfill({ json: [] }));

    await goToOverview(page);

    // Room card with Active status (from mockRoom.status)
    await expect(page.getByText("Active").first()).toBeVisible({ timeout: 5_000 });
  });
});
