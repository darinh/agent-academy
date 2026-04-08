import { test, expect } from "./fixtures";
import type { Page } from "@playwright/test";

/** Navigate to the Plan tab */
async function goToPlan(page: Page) {
  await page.goto("/");
  await expect(page).toHaveTitle(/Main Room/, { timeout: 10_000 });

  const tab = page.getByRole("tab", { name: /plan/i });
  await expect(tab).toBeVisible({ timeout: 5_000 });
  await tab.click();
}

test.describe("Plan panel — no plan", () => {
  test("shows empty state with create button when no plan exists", async ({ mockedPage: page }) => {
    // Mock plan endpoint returning 404
    await page.route("**/api/rooms/main/plan", (route) => {
      if (route.request().method() === "GET") {
        return route.fulfill({ status: 404, body: "" });
      }
      return route.fulfill({ status: 200 });
    });

    await goToPlan(page);

    await expect(page.getByText("No plan yet")).toBeVisible({ timeout: 5_000 });
    await expect(page.getByRole("button", { name: /create plan/i })).toBeVisible({ timeout: 3_000 });
  });

  test("shows plan title in toolbar", async ({ mockedPage: page }) => {
    await page.route("**/api/rooms/main/plan", (route) =>
      route.fulfill({ status: 404, body: "" }));

    await goToPlan(page);

    await expect(page.getByText("Collaboration Plan")).toBeVisible({ timeout: 5_000 });
  });
});

test.describe("Plan panel — with content", () => {
  test("renders plan content in view mode", async ({ mockedPage: page }) => {
    await page.route("**/api/rooms/main/plan", (route) => {
      if (route.request().method() === "GET") {
        return route.fulfill({ json: { content: "# Sprint Plan\n\nThis is the collaboration plan." } });
      }
      return route.fulfill({ status: 200 });
    });

    await goToPlan(page);

    // Markdown renders — heading and paragraph should appear
    await expect(page.getByText("Sprint Plan")).toBeVisible({ timeout: 5_000 });
    await expect(page.getByText("This is the collaboration plan.")).toBeVisible({ timeout: 3_000 });
  });

  test("shows edit button when plan exists", async ({ mockedPage: page }) => {
    await page.route("**/api/rooms/main/plan", (route) => {
      if (route.request().method() === "GET") {
        return route.fulfill({ json: { content: "Some plan content" } });
      }
      return route.fulfill({ status: 200 });
    });

    await goToPlan(page);

    await expect(page.getByRole("button", { name: /edit/i })).toBeVisible({ timeout: 5_000 });
  });

  test("shows delete button when plan exists", async ({ mockedPage: page }) => {
    await page.route("**/api/rooms/main/plan", (route) => {
      if (route.request().method() === "GET") {
        return route.fulfill({ json: { content: "Some plan content" } });
      }
      return route.fulfill({ status: 200 });
    });

    await goToPlan(page);

    await expect(page.getByRole("button", { name: /delete/i })).toBeVisible({ timeout: 5_000 });
  });

  test("clicking edit switches to textarea editor", async ({ mockedPage: page }) => {
    await page.route("**/api/rooms/main/plan", (route) => {
      if (route.request().method() === "GET") {
        return route.fulfill({ json: { content: "Editable plan text" } });
      }
      return route.fulfill({ status: 200 });
    });

    await goToPlan(page);

    const editBtn = page.getByRole("button", { name: /edit/i });
    await expect(editBtn).toBeVisible({ timeout: 5_000 });
    // Use evaluate to bypass section overlay intercepting pointer events
    await editBtn.evaluate((el) => (el as HTMLElement).click());

    // Textarea should appear with existing content
    const textarea = page.getByRole("textbox");
    await expect(textarea).toBeVisible({ timeout: 3_000 });
    await expect(textarea).toHaveValue("Editable plan text");

    // Save and cancel buttons appear
    await expect(page.getByRole("button", { name: /save/i })).toBeVisible({ timeout: 3_000 });
    await expect(page.getByRole("button", { name: /cancel/i })).toBeVisible({ timeout: 3_000 });
  });

  test("cancel exits edit mode without saving", async ({ mockedPage: page }) => {
    await page.route("**/api/rooms/main/plan", (route) => {
      if (route.request().method() === "GET") {
        return route.fulfill({ json: { content: "Original content" } });
      }
      return route.fulfill({ status: 200 });
    });

    await goToPlan(page);

    const editBtn = page.getByRole("button", { name: /edit/i });
    await expect(editBtn).toBeVisible({ timeout: 5_000 });
    await editBtn.evaluate((el) => (el as HTMLElement).click());

    // Modify the text
    const textarea = page.getByRole("textbox");
    await expect(textarea).toBeVisible({ timeout: 3_000 });
    await textarea.fill("Modified content");

    // Cancel via evaluate to bypass overlay
    const cancelBtn = page.getByRole("button", { name: /cancel/i });
    await cancelBtn.evaluate((el) => (el as HTMLElement).click());

    // Should be back in view mode with original content
    await expect(page.getByText("Original content")).toBeVisible({ timeout: 3_000 });
    await expect(page.getByRole("textbox")).not.toBeVisible({ timeout: 3_000 });
  });
});
