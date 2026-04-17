// @vitest-environment jsdom
/**
 * Interactive RTL tests for ProjectSelectorPage.
 *
 * Uses @testing-library/react + jsdom.
 * Covers: tab switching, API-driven workspace list, scan/onboard flow,
 * create flow, error states, and dialog interactions.
 */
import "@testing-library/jest-dom/vitest";
import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createElement } from "react";
import { FluentProvider, webDarkTheme } from "@fluentui/react-components";

// ── Mocks ──────────────────────────────────────────────────────────────

vi.mock("../api", () => ({
  listWorkspaces: vi.fn(),
  scanProject: vi.fn(),
  onboardProject: vi.fn(),
  browseDirectory: vi.fn(),
}));

import ProjectSelectorPage from "../ProjectSelectorPage";
import type { AuthUser, BrowseResult, OnboardResult, ProjectScanResult, WorkspaceMeta } from "../api";
import { browseDirectory, listWorkspaces, onboardProject, scanProject } from "../api";

const mockListWorkspaces = vi.mocked(listWorkspaces);
const mockScanProject = vi.mocked(scanProject);
const mockOnboardProject = vi.mocked(onboardProject);
const mockBrowseDirectory = vi.mocked(browseDirectory);

// ── Factories ──────────────────────────────────────────────────────────

function makeUser(overrides: Partial<AuthUser> = {}): AuthUser {
  return { login: "testuser", name: "Test User", avatarUrl: null, ...overrides };
}

function makeWorkspace(overrides: Partial<WorkspaceMeta> = {}): WorkspaceMeta {
  return { path: "/home/user/project", projectName: "my-project", lastAccessedAt: new Date().toISOString(), ...overrides };
}

function makeScanResult(overrides: Partial<ProjectScanResult> = {}): ProjectScanResult {
  return {
    path: "/home/user/project",
    projectName: "my-project",
    techStack: ["TypeScript", "React"],
    hasSpecs: false,
    hasReadme: true,
    isGitRepo: true,
    gitBranch: "main",
    detectedFiles: ["package.json", "tsconfig.json"],
    ...overrides,
  };
}

function makeBrowseResult(overrides: Partial<BrowseResult> = {}): BrowseResult {
  return {
    current: "/home/user",
    parent: "/home",
    entries: [
      { name: "project-a", path: "/home/user/project-a", isDirectory: true },
      { name: "project-b", path: "/home/user/project-b", isDirectory: true },
      { name: "notes.txt", path: "/home/user/notes.txt", isDirectory: false },
    ],
    ...overrides,
  };
}

function makeOnboardResult(overrides: Partial<OnboardResult> = {}): OnboardResult {
  return {
    scan: makeScanResult(),
    workspace: makeWorkspace(),
    specTaskCreated: true,
    roomId: "room-1",
    ...overrides,
  };
}

// ── Render helper ──────────────────────────────────────────────────────

interface RenderProps {
  user?: AuthUser | null;
  onLogout?: () => void;
  onProjectSelected?: (path: string) => void;
  onProjectOnboarded?: (result: OnboardResult) => void;
}

function renderPage(props: RenderProps = {}) {
  const user = userEvent.setup();
  const result = render(
    createElement(
      FluentProvider,
      { theme: webDarkTheme },
      createElement(ProjectSelectorPage, {
        onProjectSelected: props.onProjectSelected ?? vi.fn(),
        onProjectOnboarded: props.onProjectOnboarded,
        user: props.user ?? null,
        onLogout: props.onLogout,
      }),
    ),
  );
  return { ...result, user };
}

// ── Setup ──────────────────────────────────────────────────────────────

beforeEach(() => {
  vi.resetAllMocks();
  mockListWorkspaces.mockResolvedValue([]);
});

afterEach(cleanup);

// ── Tests ──────────────────────────────────────────────────────────────

describe("ProjectSelectorPage (interactive)", () => {
  // ── Tab switching ────────────────────────────────────────────────────

  describe("tab switching", () => {
    it("defaults to the onboard tab", () => {
      renderPage();
      const onboardTab = screen.getByRole("tab", { name: /onboard/i });
      expect(onboardTab).toHaveAttribute("aria-selected", "true");
    });

    it("switches to existing tab and shows workspace list", async () => {
      mockListWorkspaces.mockResolvedValue([makeWorkspace()]);
      const { user } = renderPage();

      await user.click(screen.getByRole("tab", { name: /existing/i }));

      const existingTab = screen.getByRole("tab", { name: /existing/i });
      expect(existingTab).toHaveAttribute("aria-selected", "true");

      // Header copy updates to the existing tab
      expect(screen.getByText("Resume work")).toBeInTheDocument();
      expect(screen.getByText("Return to an active workspace")).toBeInTheDocument();
    });

    it("switches to create tab and shows create form", async () => {
      const { user } = renderPage();

      await user.click(screen.getByRole("tab", { name: /create/i }));

      expect(screen.getByRole("tab", { name: /create/i })).toHaveAttribute("aria-selected", "true");
      expect(screen.getByText("Start from a fresh directory")).toBeInTheDocument();
    });

    it("switches back to onboard from create", async () => {
      const { user } = renderPage();

      await user.click(screen.getByRole("tab", { name: /create/i }));
      await user.click(screen.getByRole("tab", { name: /onboard/i }));

      expect(screen.getByRole("tab", { name: /onboard/i })).toHaveAttribute("aria-selected", "true");
      expect(screen.getByText("Inspect before entering")).toBeInTheDocument();
    });
  });

  // ── Existing workspace tab ───────────────────────────────────────────

  describe("existing workspaces", () => {
    it("shows a loading spinner while fetching workspaces", async () => {
      let resolveWorkspaces!: (ws: WorkspaceMeta[]) => void;
      mockListWorkspaces.mockImplementation(() => new Promise((res) => { resolveWorkspaces = res; }));

      const { user } = renderPage();
      await user.click(screen.getByRole("tab", { name: /existing/i }));

      expect(screen.getByText("Loading workspaces…")).toBeInTheDocument();

      resolveWorkspaces([]);
      await waitFor(() => {
        expect(screen.queryByText("Loading workspaces…")).not.toBeInTheDocument();
      });
    });

    it("displays workspaces after loading and selects on click", async () => {
      const onSelect = vi.fn();
      mockListWorkspaces.mockResolvedValue([
        makeWorkspace({ path: "/home/user/alpha", projectName: "Alpha" }),
        makeWorkspace({ path: "/home/user/beta", projectName: "Beta" }),
      ]);

      const { user } = renderPage({ onProjectSelected: onSelect });
      await user.click(screen.getByRole("tab", { name: /existing/i }));

      await waitFor(() => {
        expect(screen.getByText("Alpha")).toBeInTheDocument();
      });
      expect(screen.getByText("Beta")).toBeInTheDocument();

      await user.click(screen.getByTitle("Open Alpha"));
      expect(onSelect).toHaveBeenCalledWith("/home/user/alpha");
    });

    it("shows error state when fetching fails and retries on button click", async () => {
      mockListWorkspaces
        .mockRejectedValueOnce(new Error("Network error"))
        .mockResolvedValueOnce([makeWorkspace({ projectName: "Recovered" })]);

      const { user } = renderPage();
      await user.click(screen.getByRole("tab", { name: /existing/i }));

      await waitFor(() => {
        expect(screen.getByText(/failed to load workspaces/i)).toBeInTheDocument();
      });

      await user.click(screen.getByText("Retry"));
      await waitFor(() => {
        expect(screen.getByText("Recovered")).toBeInTheDocument();
      });
    });

    it("shows empty state when no workspaces exist", async () => {
      mockListWorkspaces.mockResolvedValue([]);
      const { user } = renderPage();
      await user.click(screen.getByRole("tab", { name: /existing/i }));

      await waitFor(() => {
        expect(screen.getByText(/no existing projects found/i)).toBeInTheDocument();
      });
    });
  });

  // ── Onboard tab — scan flow ──────────────────────────────────────────

  describe("onboard scan flow", () => {
    it("scans a directory on Enter and shows results", async () => {
      mockScanProject.mockResolvedValue(makeScanResult({
        projectName: "cool-app",
        techStack: ["Go", "Docker"],
        isGitRepo: true,
        gitBranch: "develop",
        hasSpecs: true,
      }));

      const { user } = renderPage();
      const input = screen.getByRole("textbox", { name: /directory path/i });

      await user.type(input, "/home/user/cool-app{enter}");

      await waitFor(() => {
        expect(screen.getByText("cool-app")).toBeInTheDocument();
      });
      expect(screen.getByText("Go")).toBeInTheDocument();
      expect(screen.getByText("Docker")).toBeInTheDocument();
      expect(screen.getByText(/repository detected.*develop/i)).toBeInTheDocument();
      expect(screen.getByText("Existing spec set found")).toBeInTheDocument();
      expect(mockScanProject).toHaveBeenCalledWith("/home/user/cool-app");
    });

    it("shows scan error on failure", async () => {
      mockScanProject.mockRejectedValue(new Error("Directory not found"));

      const { user } = renderPage();
      const input = screen.getByRole("textbox", { name: /directory path/i });

      await user.type(input, "/nonexistent{enter}");

      await waitFor(() => {
        expect(screen.getByText("Directory not found")).toBeInTheDocument();
      });
    });

    it("opens onboard confirmation dialog and completes onboarding", { retry: 3 }, async () => {
      const onOnboarded = vi.fn();
      mockScanProject.mockResolvedValue(makeScanResult({ projectName: "my-app", hasSpecs: false }));
      mockOnboardProject.mockResolvedValue(makeOnboardResult());

      const { user } = renderPage({ onProjectOnboarded: onOnboarded });
      const input = screen.getByRole("textbox", { name: /directory path/i });

      await user.type(input, "/home/user/my-app{enter}");

      // Wait for the onboard button to appear (scan completed)
      await waitFor(() => {
        expect(screen.getByRole("button", { name: /onboard project/i })).toBeInTheDocument();
      });

      await user.click(screen.getByRole("button", { name: /onboard project/i }));

      // Gate on dialog + confirm button via findByRole (async, timeout-aware).
      // Fluent v9's Dialog portal can attach role="dialog" before the body content
      // paints, so `within(dialog).getByRole(...)` (synchronous) is racy.
      // findByRole polls until the button is actually in the DOM.
      const dialog = await screen.findByRole("dialog", undefined, { timeout: 5000 });
      const confirmBtn = await within(dialog).findByRole("button", { name: /^onboard$/i }, { timeout: 5000 });
      await user.click(confirmBtn);

      await waitFor(() => {
        expect(onOnboarded).toHaveBeenCalledWith(expect.objectContaining({
          workspace: expect.objectContaining({ path: "/home/user/project" }),
        }));
      });
    });

    it("allows canceling the onboard dialog", { retry: 3 }, async () => {
      const onOnboarded = vi.fn();
      mockScanProject.mockResolvedValue(makeScanResult());
      const { user } = renderPage({ onProjectOnboarded: onOnboarded });

      const input = screen.getByRole("textbox", { name: /directory path/i });
      await user.type(input, "/home/user/project{enter}");

      await waitFor(() => {
        expect(screen.getByRole("button", { name: /onboard project/i })).toBeInTheDocument();
      });

      await user.click(screen.getByRole("button", { name: /onboard project/i }));

      // Dialog is open — wait for its title
      await waitFor(() => {
        expect(screen.getByText(/no specification found/i)).toBeInTheDocument();
      });

      await user.click(screen.getByRole("button", { name: /cancel/i }));

      await waitFor(() => {
        expect(screen.queryByText(/no specification found/i)).not.toBeInTheDocument();
      });
      expect(onOnboarded).not.toHaveBeenCalled();
    });

    it("shows onboard error in dialog when onboarding fails", { retry: 3 }, async () => {
      mockScanProject.mockResolvedValue(makeScanResult());
      mockOnboardProject.mockRejectedValue(new Error("Workspace init failed"));

      const { user } = renderPage();
      const input = screen.getByRole("textbox", { name: /directory path/i });

      await user.type(input, "/home/user/project{enter}");
      await waitFor(() => {
        expect(screen.getByRole("button", { name: /onboard project/i })).toBeInTheDocument();
      });

      await user.click(screen.getByRole("button", { name: /onboard project/i }));
      // findByRole is async and timeout-aware — avoids the sync `within().getByRole`
      // race where Fluent's Dialog role attaches before the body paints.
      const dialog = await screen.findByRole("dialog", undefined, { timeout: 5000 });
      const confirmBtn = await within(dialog).findByRole("button", { name: /^onboard$/i }, { timeout: 5000 });
      await user.click(confirmBtn);

      await waitFor(() => {
        expect(within(dialog).getByText("Workspace init failed")).toBeInTheDocument();
      });
    });
  });

  // ── Create tab ───────────────────────────────────────────────────────

  describe("create flow", () => {
    it("creates a project on button click", async () => {
      const onOnboarded = vi.fn();
      mockOnboardProject.mockResolvedValue(makeOnboardResult({
        workspace: makeWorkspace({ path: "/home/user/new-thing" }),
      }));

      const { user } = renderPage({ onProjectOnboarded: onOnboarded });
      await user.click(screen.getByRole("tab", { name: /create/i }));

      const input = screen.getByRole("textbox", { name: /directory path/i });
      await user.type(input, "/home/user/new-thing");

      await user.click(screen.getByRole("button", { name: /create & open/i }));

      await waitFor(() => {
        expect(onOnboarded).toHaveBeenCalledWith(expect.objectContaining({
          workspace: expect.objectContaining({ path: "/home/user/new-thing" }),
        }));
      });
    });

    it("disables the create button when path is empty", async () => {
      const { user } = renderPage();
      await user.click(screen.getByRole("tab", { name: /create/i }));

      expect(screen.getByRole("button", { name: /create & open/i })).toBeDisabled();
    });

    it("creates a project on Enter key", async () => {
      const onOnboarded = vi.fn();
      mockOnboardProject.mockResolvedValue(makeOnboardResult());

      const { user } = renderPage({ onProjectOnboarded: onOnboarded });
      await user.click(screen.getByRole("tab", { name: /create/i }));

      const input = screen.getByRole("textbox", { name: /directory path/i });
      await user.type(input, "/home/user/enter-project{enter}");

      await waitFor(() => {
        expect(onOnboarded).toHaveBeenCalled();
      });
      expect(mockOnboardProject).toHaveBeenCalledWith("/home/user/enter-project");
    });

    it("shows create error on failure", async () => {
      mockOnboardProject.mockRejectedValue(new Error("Permission denied"));

      const { user } = renderPage();
      await user.click(screen.getByRole("tab", { name: /create/i }));

      const input = screen.getByRole("textbox", { name: /directory path/i });
      await user.type(input, "/root/forbidden{enter}");

      await waitFor(() => {
        expect(screen.getByText("Permission denied")).toBeInTheDocument();
      });
    });
  });

  // ── User personalization ─────────────────────────────────────────────

  describe("user personalization", () => {
    it("shows personalized welcome when user is present", () => {
      renderPage({ user: makeUser({ name: "Alice" }), onLogout: vi.fn() });
      expect(screen.getByText(/welcome back, alice/i)).toBeInTheDocument();
    });

    it("shows generic copy when no user", () => {
      renderPage({ user: null });
      expect(screen.getByText(/move from directory discovery/i)).toBeInTheDocument();
    });
  });

  // ── Browse flow ──────────────────────────────────────────────────────

  describe("browse flow", () => {
    it("opens browse panel, shows entries, and selects a directory to scan", async () => {
      mockBrowseDirectory.mockResolvedValue(makeBrowseResult());
      mockScanProject.mockResolvedValue(makeScanResult({ projectName: "project-a" }));

      const { user } = renderPage();

      // Click the Browse button in the onboard tab
      const browseBtn = screen.getByRole("button", { name: /browse/i });
      await user.click(browseBtn);

      await waitFor(() => {
        expect(screen.getByText("project-a")).toBeInTheDocument();
      });
      expect(screen.getByText("project-b")).toBeInTheDocument();
      expect(mockBrowseDirectory).toHaveBeenCalled();

      // Select "this directory" to trigger a scan with the browsed path
      const selectBtn = screen.getByRole("button", { name: /select this directory/i });
      await user.click(selectBtn);

      await waitFor(() => {
        expect(mockScanProject).toHaveBeenCalledWith("/home/user");
      });
    });
  });
});
