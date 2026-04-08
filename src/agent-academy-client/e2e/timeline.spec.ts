import { test, expect, mockActivity } from "./fixtures";

test.describe("Timeline panel", () => {
  test("renders timeline tab with activity events", async ({ mockedPage: page }) => {
    await page.goto("/");
    await expect(page).toHaveTitle(/Main Room/, { timeout: 10_000 });

    // Navigate to Timeline tab
    const timelineTab = page.getByRole("tab", { name: /timeline/i });
    await expect(timelineTab).toBeVisible({ timeout: 5_000 });
    await timelineTab.click();

    // Timeline header with count badge
    await expect(page.getByText("Activity Timeline")).toBeVisible({ timeout: 5_000 });
  });

  test("shows activity event messages", async ({ mockedPage: page }) => {
    await page.goto("/");
    await expect(page).toHaveTitle(/Main Room/, { timeout: 10_000 });

    const timelineTab = page.getByRole("tab", { name: /timeline/i });
    await timelineTab.click();

    // Event messages should be visible
    await expect(page.getByText(/Implement user authentication/).first()).toBeVisible({ timeout: 5_000 });
    await expect(page.getByText(/Fix dashboard layout/).first()).toBeVisible({ timeout: 3_000 });
  });

  test("shows event type badges", async ({ mockedPage: page }) => {
    await page.goto("/");
    await expect(page).toHaveTitle(/Main Room/, { timeout: 10_000 });

    const timelineTab = page.getByRole("tab", { name: /timeline/i });
    await timelineTab.click();

    // Event type badges
    await expect(page.getByText("TaskCreated").first()).toBeVisible({ timeout: 5_000 });
    await expect(page.getByText("AgentFinished").first()).toBeVisible({ timeout: 3_000 });
  });

  test("shows error events with error styling", async ({ mockedPage: page }) => {
    await page.goto("/");
    await expect(page).toHaveTitle(/Main Room/, { timeout: 10_000 });

    const timelineTab = page.getByRole("tab", { name: /timeline/i });
    await timelineTab.click();

    // Error event message
    await expect(page.getByText(/rate limit exceeded/).first()).toBeVisible({ timeout: 5_000 });
    await expect(page.getByText("AgentErrorOccurred").first()).toBeVisible({ timeout: 3_000 });
  });
});
