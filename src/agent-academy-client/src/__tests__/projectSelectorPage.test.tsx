import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it, vi, beforeEach } from "vitest";
import { FluentProvider, webDarkTheme } from "@fluentui/react-components";
import { createElement } from "react";

// ── Mocks ──────────────────────────────────────────────────────────────

vi.mock("../api", () => ({
  listWorkspaces: vi.fn(),
  scanProject: vi.fn(),
  onboardProject: vi.fn(),
  browseDirectory: vi.fn(),
}));

import ProjectSelectorPage from "../ProjectSelectorPage";
import type { AuthUser, OnboardResult } from "../api";
import { listWorkspaces } from "../api";

const mockListWorkspaces = vi.mocked(listWorkspaces);

// ── Factories ──────────────────────────────────────────────────────────

function makeUser(overrides: Partial<AuthUser> = {}): AuthUser {
  return { login: "testuser", name: "Test User", avatarUrl: null, ...overrides };
}

// ── Render helper ──────────────────────────────────────────────────────

interface RenderProps {
  user?: AuthUser | null;
  onLogout?: () => void;
  onProjectSelected?: (path: string) => void;
  onProjectOnboarded?: (result: OnboardResult) => void;
}

function render(props: RenderProps = {}) {
  return renderToStaticMarkup(
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
}

// ── Tests ──────────────────────────────────────────────────────────────

beforeEach(() => {
  vi.resetAllMocks();
  mockListWorkspaces.mockResolvedValue([]);
});

describe("ProjectSelectorPage", () => {
  // ── Layout & Structure ───────────────────────────────────────────────

  describe("page structure", () => {
    it("renders the workspace staging kicker badge", () => {
      const html = render();
      expect(html).toContain("Workspace staging");
    });

    it("renders the main heading", () => {
      const html = render();
      expect(html).toContain("Choose the project with intent.");
    });

    it("renders all three rail information cards with correct content", () => {
      const html = render();
      // Collaboration card
      expect(html).toContain("Collaboration");
      expect(html).toContain("Six specialists, one room");
      expect(html).toContain("visible in the same interface");

      // Branch flow card
      expect(html).toContain("Branch flow");
      expect(html).toContain("Task branches by default");
      expect(html).toContain("breakout rounds can ship incrementally");

      // Spec discipline card
      expect(html).toContain("Spec discipline");
      expect(html).toContain("Reality over aspiration");
      expect(html).toContain("missing specs get surfaced early");
    });

    it("renders all three tab labels in the tab list", () => {
      const html = render();
      expect(html).toContain("Existing");
      expect(html).toContain("Onboard");
      expect(html).toContain("Create");
    });

    it("renders the footnote about Copilot availability", () => {
      const html = render();
      expect(html).toContain("The frontend now treats Copilot availability as a first-class state");
    });
  });

  // ── Default Tab (Onboard) ────────────────────────────────────────────

  describe("default tab state", () => {
    it("defaults to the onboard tab with its header copy", () => {
      const html = render();
      expect(html).toContain("Inspect before entering");
      expect(html).toContain("Scan a repository and onboard it cleanly");
      expect(html).toContain("Review the project shape first");
    });

    it("renders the directory path input with placeholder", () => {
      const html = render();
      expect(html).toContain("Directory path");
      expect(html).toContain("/home/user/projects/my-project");
    });

    it("renders the Scan button inside the input content-after slot", () => {
      const html = render();
      // "Scan" as a standalone button label (not substring of "Scan a repository...")
      // Verify it appears as button text in the input's contentAfter area
      expect(html).toContain(">Scan<");
    });

    it("renders the Browse directories button", () => {
      const html = render();
      expect(html).toContain("Browse directories");
    });

    it("does not render scan results, errors, or spinners in initial state", () => {
      const html = render();
      expect(html).not.toContain("Scanning project");
      expect(html).not.toContain("Onboard project");
      expect(html).not.toContain("Loading directories");
    });

    it("renders the input with the correct aria-label for accessibility", () => {
      const html = render();
      expect(html).toContain('aria-label="Directory path"');
    });
  });

  // ── User Personalization ─────────────────────────────────────────────

  describe("user personalization", () => {
    it("shows personalized welcome when user has a display name", () => {
      const html = render({ user: makeUser({ name: "Athena Pallas" }) });
      expect(html).toContain("Welcome back, Athena Pallas");
    });

    it("falls back to login for the welcome when name is null", () => {
      const html = render({ user: makeUser({ name: null, login: "athena42" }) });
      expect(html).toContain("Welcome back, athena42");
    });

    it("falls back to login for the welcome when name is undefined", () => {
      const html = render({ user: makeUser({ name: undefined, login: "hermes" }) });
      expect(html).toContain("Welcome back, hermes");
    });

    it("shows the generic copy when no user is provided", () => {
      const html = render({ user: null });
      expect(html).toContain("Move from directory discovery into collaboration");
      expect(html).not.toContain("Welcome back");
    });
  });

  // ── UserBadge Rendering ──────────────────────────────────────────────

  describe("UserBadge conditional rendering", () => {
    it("renders the user menu button when user and onLogout are both provided", () => {
      const html = render({ user: makeUser(), onLogout: vi.fn() });
      expect(html).toContain("User menu");
    });

    it("renders the user display name inside the badge", () => {
      const html = render({ user: makeUser({ name: "Darin" }), onLogout: vi.fn() });
      expect(html).toContain("Darin");
    });

    it("renders avatar image when user has an avatarUrl", () => {
      const html = render({
        user: makeUser({ avatarUrl: "https://avatars.example.com/u/123" }),
        onLogout: vi.fn(),
      });
      expect(html).toContain("https://avatars.example.com/u/123");
    });

    it("renders initials fallback when user has no avatarUrl", () => {
      const html = render({
        user: makeUser({ name: "Test User", avatarUrl: null }),
        onLogout: vi.fn(),
      });
      // UserBadge generates initials from the name: "Test User" → "TU"
      expect(html).toContain("TU");
    });

    it("renders single initial for single-word name", () => {
      const html = render({
        user: makeUser({ name: "Zeus", avatarUrl: null }),
        onLogout: vi.fn(),
      });
      expect(html).toContain("Z");
    });

    it("does not render the user menu when user is null", () => {
      const html = render({ user: null, onLogout: vi.fn() });
      expect(html).not.toContain("User menu");
    });

    it("does not render the user menu when onLogout is undefined", () => {
      const html = render({ user: makeUser(), onLogout: undefined });
      expect(html).not.toContain("User menu");
    });

    it("renders the user name text inside the badge trigger", () => {
      // MenuPopover content (Sign out, Settings) is portal-based and not in SSR markup.
      // We verify the trigger button renders the user's name.
      const html = render({ user: makeUser({ name: "Hermes" }), onLogout: vi.fn() });
      expect(html).toContain("Hermes");
    });
  });

  // ── Tab Copy Variants ────────────────────────────────────────────────
  // The component initializes to "onboard" tab. SSR cannot switch tabs,
  // so we verify the onboard copy thoroughly and confirm the tab labels
  // for the other two tabs are present (content only visible after click).

  describe("tab copy", () => {
    it("shows the onboard tab kicker", () => {
      const html = render();
      expect(html).toContain("Inspect before entering");
    });

    it("shows the onboard tab title", () => {
      const html = render();
      expect(html).toContain("Scan a repository and onboard it cleanly");
    });

    it("shows the onboard tab description", () => {
      const html = render();
      expect(html).toContain("right spec expectations and workspace metadata");
    });

    it("includes the existing tab label for later selection", () => {
      const html = render();
      expect(html).toContain("Existing");
    });

    it("includes the create tab label for later selection", () => {
      const html = render();
      expect(html).toContain("Create");
    });
  });

  // ── Props Wiring ─────────────────────────────────────────────────────

  describe("props wiring", () => {
    it("renders without crashing when all optional props are omitted", () => {
      const html = render({
        user: null,
        onLogout: undefined,
        onProjectOnboarded: undefined,
      });
      expect(html).toContain("Choose the project with intent.");
    });

    it("renders without crashing when all props are provided", () => {
      const html = render({
        user: makeUser(),
        onLogout: vi.fn(),
        onProjectSelected: vi.fn(),
        onProjectOnboarded: vi.fn(),
      });
      expect(html).toContain("Choose the project with intent.");
      expect(html).toContain("User menu");
    });

    it("does not call listWorkspaces synchronously during SSR", () => {
      // Note: SSR never runs useEffect, so this validates only that no
      // synchronous API call happens during render. Interactive fetch
      // behavior would require a client-side test environment.
      render();
      expect(mockListWorkspaces).not.toHaveBeenCalled();
    });
  });

  // ── Edge Cases ───────────────────────────────────────────────────────

  describe("edge cases", () => {
    it("handles user with empty string name by showing generic welcome", () => {
      // `""` is not nullish, so `name ?? login` gives `""`.
      // `""` is falsy, so the ternary shows the generic (non-personalized) copy.
      const html = render({ user: makeUser({ name: "", login: "ghost" }) });
      expect(html).toContain("Move from directory discovery into collaboration");
      expect(html).not.toContain("Welcome back");
    });

    it("handles user with very long name without breaking layout", () => {
      const longName = "A".repeat(200);
      const html = render({ user: makeUser({ name: longName }) });
      expect(html).toContain(`Welcome back, ${longName}`);
    });

    it("handles user with special characters in name", () => {
      const html = render({ user: makeUser({ name: "O'Brien <Admin>" }) });
      // HTML encoding of special chars
      expect(html).toContain("O&#x27;Brien");
    });
  });
});
