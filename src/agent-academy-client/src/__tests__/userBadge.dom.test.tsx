// @vitest-environment jsdom
/**
 * Standalone tests for UserBadge component.
 *
 * Covers: rendering with avatar, fallback initials, name display,
 * menu interactions (sign out, settings).
 */
import "@testing-library/jest-dom/vitest";
import {
  cleanup,
  render,
  screen,
  fireEvent,
  act,
} from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { createElement } from "react";
import { FluentProvider, webDarkTheme } from "@fluentui/react-components";

import UserBadge from "../UserBadge";
import type { AuthUser } from "../api";

function renderBadge(props: Partial<Parameters<typeof UserBadge>[0]> = {}) {
  const defaults = {
    user: { login: "octocat", name: "Mona Lisa", avatarUrl: "https://example.com/avatar.png" } as AuthUser,
    onLogout: vi.fn(),
  };
  const merged = { ...defaults, ...props };
  render(
    createElement(
      FluentProvider,
      { theme: webDarkTheme },
      createElement(UserBadge, merged),
    ),
  );
  return merged;
}

afterEach(() => {
  cleanup();
  document.body.innerHTML = "";
});

describe("UserBadge", () => {
  describe("rendering", () => {
    it("displays user name when provided", () => {
      renderBadge();
      expect(screen.getByText("Mona Lisa")).toBeInTheDocument();
    });

    it("falls back to login when name is null", () => {
      renderBadge({ user: { login: "octocat", name: null } });
      expect(screen.getByText("octocat")).toBeInTheDocument();
    });

    it("falls back to login when name is undefined", () => {
      renderBadge({ user: { login: "octocat" } });
      expect(screen.getByText("octocat")).toBeInTheDocument();
    });

    it("renders avatar image when avatarUrl is provided", () => {
      renderBadge();
      const img = document.querySelector("img");
      expect(img).toBeInTheDocument();
      expect(img?.src).toBe("https://example.com/avatar.png");
    });

    it("renders fallback initials when no avatarUrl", () => {
      renderBadge({ user: { login: "octocat", name: "Mona Lisa" } });
      expect(screen.getByText("ML")).toBeInTheDocument();
    });

    it("renders single initial for single-word name", () => {
      renderBadge({ user: { login: "mono", name: "Mono" } });
      expect(screen.getByText("M")).toBeInTheDocument();
    });

    it("renders 'AA' fallback for empty name string", () => {
      renderBadge({ user: { login: "", name: "" } });
      expect(screen.getByText("AA")).toBeInTheDocument();
    });

    it("renders user menu trigger button with aria-label", () => {
      renderBadge();
      expect(screen.getByRole("button", { name: "User menu" })).toBeInTheDocument();
    });
  });

  describe("menu interactions", () => {
    it("calls onLogout when Sign out is clicked", async () => {
      const props = renderBadge();
      // Open the menu by clicking the trigger
      await act(async () => {
        fireEvent.click(screen.getByRole("button", { name: "User menu" }));
      });
      await act(async () => {
        fireEvent.click(screen.getByText("Sign out"));
      });
      expect(props.onLogout).toHaveBeenCalledTimes(1);
    });

    it("shows Settings when onOpenSettings is provided", async () => {
      const onOpenSettings = vi.fn();
      renderBadge({ onOpenSettings });
      await act(async () => {
        fireEvent.click(screen.getByRole("button", { name: "User menu" }));
      });
      expect(screen.getByText("Settings")).toBeInTheDocument();
    });

    it("calls onOpenSettings when Settings is clicked", async () => {
      const onOpenSettings = vi.fn();
      renderBadge({ onOpenSettings });
      await act(async () => {
        fireEvent.click(screen.getByRole("button", { name: "User menu" }));
      });
      await act(async () => {
        fireEvent.click(screen.getByText("Settings"));
      });
      expect(onOpenSettings).toHaveBeenCalledTimes(1);
    });

    it("does not show Settings when onOpenSettings is not provided", async () => {
      renderBadge();
      await act(async () => {
        fireEvent.click(screen.getByRole("button", { name: "User menu" }));
      });
      expect(screen.queryByText("Settings")).not.toBeInTheDocument();
    });
  });
});
