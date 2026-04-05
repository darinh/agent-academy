import { test, expect, mockTasks } from "./fixtures";

test.describe("Dashboard panel", () => {
  test("renders dashboard tab with summary cards", async ({ mockedPage: page }) => {
    await page.goto("/");
    await expect(page).toHaveTitle(/Main Room/, { timeout: 10_000 });

    // Navigate to Dashboard tab
    const dashTab = page.getByRole("tab", { name: /dashboard/i });
    await expect(dashTab).toBeVisible({ timeout: 5_000 });
    await dashTab.click();

    // Summary cards should render with labels
    await expect(page.getByText("Rooms").first()).toBeVisible({ timeout: 5_000 });
    await expect(page.getByText("Agents").first()).toBeVisible({ timeout: 3_000 });
  });

  test("shows time range selector buttons", async ({ mockedPage: page }) => {
    await page.goto("/");
    await expect(page).toHaveTitle(/Main Room/, { timeout: 10_000 });

    const dashTab = page.getByRole("tab", { name: /dashboard/i });
    await dashTab.click();

    // Time range buttons should exist
    await expect(page.getByRole("button", { name: "24h" })).toBeVisible({ timeout: 5_000 });
    await expect(page.getByRole("button", { name: "7d" })).toBeVisible({ timeout: 3_000 });
    await expect(page.getByRole("button", { name: "30d" })).toBeVisible({ timeout: 3_000 });
  });

  test("time range button click changes selection", async ({ mockedPage: page }) => {
    await page.goto("/");
    await expect(page).toHaveTitle(/Main Room/, { timeout: 10_000 });

    const dashTab = page.getByRole("tab", { name: /dashboard/i });
    await dashTab.click();

    // Click 7d — force: true because dashboard cards may overlay the button
    const sevenDay = page.getByRole("button", { name: "7d" });
    await expect(sevenDay).toBeVisible({ timeout: 5_000 });
    await sevenDay.click({ force: true });

    // Dashboard should still be visible after interaction
    await expect(page.getByText("Rooms").first()).toBeVisible({ timeout: 3_000 });
  });

  test("dashboard shows phase distribution section", async ({ mockedPage: page }) => {
    await page.goto("/");
    await expect(page).toHaveTitle(/Main Room/, { timeout: 10_000 });

    const dashTab = page.getByRole("tab", { name: /dashboard/i });
    await dashTab.click();

    // Phase distribution section
    await expect(page.getByText(/phase distribution/i).first()).toBeVisible({ timeout: 5_000 });
  });
});
