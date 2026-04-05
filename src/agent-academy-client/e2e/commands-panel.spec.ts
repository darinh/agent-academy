import { test as base, expect } from "@playwright/test";
import { mockAllApis } from "./fixtures";
import type { Page } from "@playwright/test";

// ── Mock data ───────────────────────────────────────────────────────────

const mockCommandMetadata = [
  {
    command: "READ_FILE",
    title: "Read file",
    category: "code",
    description: "Inspect a repository file",
    detail: "Source spelunking and spec checks.",
    isAsync: false,
    fields: [
      { name: "path", label: "Path", kind: "text", description: "Repository-relative file path.", placeholder: "src/Program.cs", required: true },
    ],
  },
  {
    command: "LIST_ROOMS",
    title: "List Rooms",
    category: "workspace",
    description: "Show all collaboration rooms",
    detail: "Quick overview of active rooms.",
    isAsync: false,
    fields: [],
  },
  {
    command: "RUN_BUILD",
    title: "Run Build",
    category: "operations",
    description: "Build the project",
    detail: "Triggers dotnet build and reports results.",
    isAsync: true,
    fields: [],
  },
  {
    command: "GIT_LOG",
    title: "Git Log",
    category: "git",
    description: "Show recent commit history",
    detail: "Recent commits with optional filters.",
    isAsync: false,
    fields: [
      { name: "count", label: "Count", kind: "number", description: "Number of commits.", placeholder: "10" },
    ],
  },
];

// ── Fixtures ────────────────────────────────────────────────────────────

async function mockCommandApis(page: Page) {
  await mockAllApis(page);

  // Override the commands metadata endpoint (last-registered wins over mockAllApis)
  await page.route("**/api/commands/metadata", (route) =>
    route.fulfill({ json: mockCommandMetadata }));
}

const test = base.extend<{ commandsPage: Page }>({
  commandsPage: async ({ page }, use) => {
    await mockCommandApis(page);
    await page.goto("/");
    await expect(page).toHaveTitle(/Main Room/);
    const cmdTab = page.getByRole("tab", { name: /commands/i });
    await cmdTab.click({ force: true });
    await use(page);
  },
});

// ── Tests ───────────────────────────────────────────────────────────────

test.describe("CommandsPanel", () => {
  test("renders hero section", async ({ commandsPage: page }) => {
    await expect(page.getByText("Human command surface", { exact: true })).toBeVisible({ timeout: 5_000 });
    await expect(page.getByText(/Operate the workspace/)).toBeVisible();
  });

  test("shows summary metric cards", async ({ commandsPage: page }) => {
    await expect(page.getByText("Available", { exact: true }).first()).toBeVisible({ timeout: 5_000 });
    await expect(page.getByText("Immediate", { exact: true })).toBeVisible();
    await expect(page.getByText("Polling", { exact: true })).toBeVisible();
  });

  test("renders command deck with command cards", async ({ commandsPage: page }) => {
    await expect(page.getByRole("heading", { name: "Command deck" })).toBeVisible({ timeout: 5_000 });
    // Use the command card buttons which have category + title
    await expect(page.getByRole("button", { name: /Read file/ }).first()).toBeVisible();
    await expect(page.getByRole("button", { name: /List Rooms/ }).first()).toBeVisible();
    await expect(page.getByRole("button", { name: /Run Build/ }).first()).toBeVisible();
    await expect(page.getByRole("button", { name: /Git Log/ }).first()).toBeVisible();
  });

  test("shows category badges on command cards", async ({ commandsPage: page }) => {
    await expect(page.getByRole("heading", { name: "Command deck" })).toBeVisible({ timeout: 5_000 });
    await expect(page.getByText("code", { exact: true }).first()).toBeVisible();
    await expect(page.getByText("workspace", { exact: true }).first()).toBeVisible();
    await expect(page.getByText("operations", { exact: true }).first()).toBeVisible();
    await expect(page.getByText("git", { exact: true }).first()).toBeVisible();
  });

  test("shows async/sync badges", async ({ commandsPage: page }) => {
    await expect(page.getByRole("heading", { name: "Command deck" })).toBeVisible({ timeout: 5_000 });
    await expect(page.getByText("instant", { exact: true }).first()).toBeVisible();
    await expect(page.getByText("polling", { exact: true }).first()).toBeVisible();
  });

  test("clicking command card shows its fields", async ({ commandsPage: page }) => {
    await expect(page.getByRole("heading", { name: "Command deck" })).toBeVisible({ timeout: 5_000 });

    // Click READ_FILE command card button (not the Run button)
    const readFileCard = page.getByRole("button", { name: /code.*instant.*Read file/i });
    await readFileCard.click();

    await expect(page.getByText("Path").first()).toBeVisible();
    await expect(page.getByPlaceholder("src/Program.cs")).toBeVisible();
  });

  test("clicking command with no fields shows helper text", async ({ commandsPage: page }) => {
    await expect(page.getByRole("heading", { name: "Command deck" })).toBeVisible({ timeout: 5_000 });

    // Click LIST_ROOMS command card — no fields
    const listRoomsCard = page.getByRole("button", { name: /workspace.*instant.*List Rooms/i });
    await listRoomsCard.click();

    await expect(page.getByText("No extra arguments required")).toBeVisible();
  });

  test("shows Run button with command title", async ({ commandsPage: page }) => {
    await expect(page.getByRole("heading", { name: "Command deck" })).toBeVisible({ timeout: 5_000 });

    // Default selected command should have a Run button
    const runButton = page.getByRole("button", { name: /run/i });
    await expect(runButton.first()).toBeVisible();
  });

  test("renders execution rail section", async ({ commandsPage: page }) => {
    await expect(page.getByText("Execution rail")).toBeVisible({ timeout: 5_000 });
    await expect(page.getByText("No command runs yet")).toBeVisible();
  });

  test("renders history section", async ({ commandsPage: page }) => {
    await expect(page.getByText("History", { exact: true }).first()).toBeVisible({ timeout: 5_000 });
  });

  test("shows badge row in execution rail", async ({ commandsPage: page }) => {
    await expect(page.getByText("Execution rail")).toBeVisible({ timeout: 5_000 });
    await expect(page.getByText("latest first", { exact: true })).toBeVisible();
    await expect(page.getByText("audited", { exact: true })).toBeVisible();
  });
});
