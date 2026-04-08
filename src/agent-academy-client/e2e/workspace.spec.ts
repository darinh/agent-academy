import { test, expect, mockTasks, mockAgents } from "./fixtures";

test.describe("Workspace smoke tests", () => {
  test("renders workspace with room name in title", async ({ mockedPage: page }) => {
    await page.goto("/");
    // Room name appears in page title
    await expect(page).toHaveTitle(/Main Room/, { timeout: 10_000 });
  });

  test("shows configured agents in sidebar", async ({ mockedPage: page }) => {
    await page.goto("/");
    for (const agent of mockAgents) {
      await expect(page.getByText(agent.name).first()).toBeVisible({ timeout: 10_000 });
    }
  });

  test("navigates to tasks tab and shows task list", async ({ mockedPage: page }) => {
    await page.goto("/");
    await expect(page).toHaveTitle(/Main Room/, { timeout: 10_000 });

    // Find and click the Tasks tab
    const tasksTab = page.getByRole("tab", { name: /tasks/i });
    await expect(tasksTab).toBeVisible({ timeout: 5_000 });
    await tasksTab.click();

    // Task titles should render
    await expect(page.getByText(mockTasks[0].title)).toBeVisible({ timeout: 10_000 });
    await expect(page.getByText(mockTasks[1].title)).toBeVisible({ timeout: 5_000 });
  });

  test("shows workspace deck tabs", async ({ mockedPage: page }) => {
    await page.goto("/");
    await expect(page).toHaveTitle(/Main Room/, { timeout: 10_000 });

    // Core tabs should be rendered
    const tabNames = ["Conversation", "Tasks", "Plan", "Commands", "Timeline", "Dashboard"];
    for (const name of tabNames) {
      await expect(page.getByRole("tab", { name: new RegExp(name, "i") })).toBeVisible({ timeout: 5_000 });
    }
  });

  test("shows project info in header", async ({ mockedPage: page }) => {
    await page.goto("/");
    // The mock workspace has projectName "test-project"
    await expect(page.getByText("test-project").first()).toBeVisible({ timeout: 10_000 });
  });
});
