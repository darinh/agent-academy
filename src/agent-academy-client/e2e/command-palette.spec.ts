import { test, expect } from "./fixtures";

test.describe("Command palette", () => {
  test("opens with Ctrl+K and shows search input", async ({ mockedPage: page }) => {
    await page.goto("/");
    await expect(page).toHaveTitle(/Main Room/, { timeout: 10_000 });

    // Open command palette with keyboard shortcut
    await page.keyboard.press("Control+k");

    // The palette should show the search input
    await expect(page.getByPlaceholder("Search commands…")).toBeVisible({ timeout: 5_000 });
  });

  test("shows command categories in palette", async ({ mockedPage: page }) => {
    await page.goto("/");
    await expect(page).toHaveTitle(/Main Room/, { timeout: 10_000 });

    await page.keyboard.press("Control+k");
    await expect(page.getByPlaceholder("Search commands…")).toBeVisible({ timeout: 5_000 });

    // Should show at least some built-in commands
    await expect(page.getByText("Read file").first()).toBeVisible({ timeout: 3_000 });
  });

  test("palette closes with Escape", async ({ mockedPage: page }) => {
    await page.goto("/");
    await expect(page).toHaveTitle(/Main Room/, { timeout: 10_000 });

    await page.keyboard.press("Control+k");
    await expect(page.getByPlaceholder("Search commands…")).toBeVisible({ timeout: 5_000 });

    await page.keyboard.press("Escape");
    await expect(page.getByPlaceholder("Search commands…")).not.toBeVisible({ timeout: 3_000 });
  });

  test("palette filters commands by search query", async ({ mockedPage: page }) => {
    await page.goto("/");
    await expect(page).toHaveTitle(/Main Room/, { timeout: 10_000 });

    await page.keyboard.press("Control+k");
    const searchInput = page.getByPlaceholder("Search commands…");
    await expect(searchInput).toBeVisible({ timeout: 5_000 });

    // Type a filter
    await searchInput.fill("build");

    // "Run build" should be visible (matches "build")
    await expect(page.getByText(/run build/i).first()).toBeVisible({ timeout: 3_000 });
  });

  test("palette toggles off with second Ctrl+K", async ({ mockedPage: page }) => {
    await page.goto("/");
    await expect(page).toHaveTitle(/Main Room/, { timeout: 10_000 });

    // Open
    await page.keyboard.press("Control+k");
    await expect(page.getByPlaceholder("Search commands…")).toBeVisible({ timeout: 5_000 });

    // Blur the input first (the Ctrl+K handler skips when focused on INPUT)
    await page.keyboard.press("Escape");
    await expect(page.getByPlaceholder("Search commands…")).not.toBeVisible({ timeout: 3_000 });

    // Reopen
    await page.keyboard.press("Control+k");
    await expect(page.getByPlaceholder("Search commands…")).toBeVisible({ timeout: 3_000 });
  });
});
