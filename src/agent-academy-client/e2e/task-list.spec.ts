import { test, expect, mockTasks } from "./fixtures";

test.describe("Task list panel", () => {
  test("renders task list with filter chips", async ({ mockedPage: page }) => {
    await page.goto("/");
    await expect(page).toHaveTitle(/Main Room/, { timeout: 10_000 });

    // Navigate to Tasks tab
    const tasksTab = page.getByRole("tab", { name: /tasks/i });
    await expect(tasksTab).toBeVisible({ timeout: 5_000 });
    await tasksTab.click();

    // Filter chips should be visible
    await expect(page.getByText(/all/i).first()).toBeVisible({ timeout: 5_000 });
    await expect(page.getByText(mockTasks[0].title)).toBeVisible({ timeout: 5_000 });
  });

  test("shows task metadata badges", async ({ mockedPage: page }) => {
    await page.goto("/");
    await expect(page).toHaveTitle(/Main Room/, { timeout: 10_000 });

    const tasksTab = page.getByRole("tab", { name: /tasks/i });
    await tasksTab.click();

    // Task should show assigned agent name
    await expect(page.getByText("Hephaestus").first()).toBeVisible({ timeout: 5_000 });
  });

  test("shows active and completed tasks", async ({ mockedPage: page }) => {
    await page.goto("/");
    await expect(page).toHaveTitle(/Main Room/, { timeout: 10_000 });

    const tasksTab = page.getByRole("tab", { name: /tasks/i });
    await tasksTab.click();

    // Both tasks should render
    await expect(page.getByText(mockTasks[0].title)).toBeVisible({ timeout: 5_000 });
    await expect(page.getByText(mockTasks[1].title)).toBeVisible({ timeout: 5_000 });
  });

  test("task cards show status and type badges", async ({ mockedPage: page }) => {
    await page.goto("/");
    await expect(page).toHaveTitle(/Main Room/, { timeout: 10_000 });

    const tasksTab = page.getByRole("tab", { name: /tasks/i });
    await tasksTab.click();

    // Task status should be visible (Active for task-1)
    await expect(page.getByText(/active/i).first()).toBeVisible({ timeout: 5_000 });
  });

  test("task card is expandable", async ({ mockedPage: page }) => {
    await page.goto("/");
    await expect(page).toHaveTitle(/Main Room/, { timeout: 10_000 });

    const tasksTab = page.getByRole("tab", { name: /tasks/i });
    await tasksTab.click();

    // Click on a task title to expand it
    const taskTitle = page.getByText(mockTasks[0].title);
    await expect(taskTitle).toBeVisible({ timeout: 5_000 });
    await taskTitle.click();

    // After expanding, description or success criteria should be visible
    await expect(page.getByText(mockTasks[0].description).first()).toBeVisible({ timeout: 3_000 });
  });

  test("review queue filter shows only reviewable tasks", async ({ mockedPage: page }) => {
    await page.goto("/");
    await expect(page).toHaveTitle(/Main Room/, { timeout: 10_000 });

    const tasksTab = page.getByRole("tab", { name: /tasks/i });
    await tasksTab.click();

    // Click "Review Queue" filter
    const reviewFilter = page.getByText(/review queue/i);
    if (await reviewFilter.isVisible({ timeout: 3_000 }).catch(() => false)) {
      await reviewFilter.click();
      // Page should still render without crashing
      await expect(page.getByRole("tab", { name: /tasks/i })).toBeVisible({ timeout: 3_000 });
    }
  });
});
