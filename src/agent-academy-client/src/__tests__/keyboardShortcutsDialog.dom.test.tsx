// @vitest-environment jsdom
/**
 * Tests for KeyboardShortcutsDialog component.
 *
 * Covers: rendering, close callback, dismiss button, keyboard accessibility,
 * platform-aware modifier key display, closed state.
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

import KeyboardShortcutsDialog from "../KeyboardShortcutsDialog";

function renderDialog(props: Partial<Parameters<typeof KeyboardShortcutsDialog>[0]> = {}) {
  const defaults = {
    open: true,
    onClose: vi.fn(),
  };
  const merged = { ...defaults, ...props };
  render(
    createElement(
      FluentProvider,
      { theme: webDarkTheme },
      createElement(KeyboardShortcutsDialog, merged),
    ),
  );
  return merged;
}

afterEach(() => {
  cleanup();
});

describe("KeyboardShortcutsDialog", () => {
  describe("rendering", () => {
    it("displays the title when open", () => {
      renderDialog();
      expect(screen.getByText("Keyboard Shortcuts")).toBeInTheDocument();
    });

    it("shows Navigation section", () => {
      renderDialog();
      expect(screen.getByText("Navigation")).toBeInTheDocument();
    });

    it("shows Chat & Messages section", () => {
      renderDialog();
      expect(screen.getByText("Chat & Messages")).toBeInTheDocument();
    });

    it("shows Panels section", () => {
      renderDialog();
      expect(screen.getByText("Panels")).toBeInTheDocument();
    });

    it("lists the command palette shortcut", () => {
      renderDialog();
      expect(screen.getByText("Command palette")).toBeInTheDocument();
    });

    it("lists the search shortcut", () => {
      renderDialog();
      expect(screen.getByText("Search")).toBeInTheDocument();
    });

    it("lists the keyboard shortcuts shortcut", () => {
      renderDialog();
      expect(screen.getByText("Show keyboard shortcuts")).toBeInTheDocument();
    });

    it("lists the send message shortcut", () => {
      renderDialog();
      expect(screen.getByText("Send message")).toBeInTheDocument();
    });

    it("lists the new line shortcut", () => {
      renderDialog();
      expect(screen.getByText("New line in message")).toBeInTheDocument();
    });

    it("lists the close shortcut", () => {
      renderDialog();
      expect(screen.getByText("Close settings / palette")).toBeInTheDocument();
    });

    it("does not render when closed", () => {
      renderDialog({ open: false });
      expect(screen.queryByText("Keyboard Shortcuts")).not.toBeInTheDocument();
    });

    it("renders kbd elements for key bindings", () => {
      renderDialog();
      const kbds = screen.getAllByText("K");
      expect(kbds.length).toBeGreaterThanOrEqual(1);
      // Verify it's rendered as a <kbd> element
      const kbdEl = kbds.find((el) => el.tagName === "KBD");
      expect(kbdEl).toBeDefined();
    });
  });

  describe("callbacks", () => {
    it("calls onClose when dismiss button is clicked", async () => {
      const props = renderDialog();
      const dismissBtn = screen.getByLabelText("Close");
      await act(async () => {
        fireEvent.click(dismissBtn);
      });
      expect(props.onClose).toHaveBeenCalled();
    });

    it("calls onClose when Enter is pressed on dismiss button", async () => {
      const props = renderDialog();
      const dismissBtn = screen.getByLabelText("Close");
      await act(async () => {
        fireEvent.keyDown(dismissBtn, { key: "Enter" });
      });
      expect(props.onClose).toHaveBeenCalled();
    });
  });

  describe("accessibility", () => {
    it("dismiss button has aria-label", () => {
      renderDialog();
      const dismissBtn = screen.getByLabelText("Close");
      expect(dismissBtn).toBeInTheDocument();
    });

    it("dismiss button is keyboard accessible with tabIndex", () => {
      renderDialog();
      const dismissBtn = screen.getByLabelText("Close");
      expect(dismissBtn).toHaveAttribute("tabindex", "0");
    });

    it("dismiss button responds to Space key", async () => {
      const props = renderDialog();
      const dismissBtn = screen.getByLabelText("Close");
      await act(async () => {
        fireEvent.keyDown(dismissBtn, { key: " " });
      });
      expect(props.onClose).toHaveBeenCalled();
    });
  });

  describe("modifier key display", () => {
    it("shows Esc key binding", () => {
      renderDialog();
      const escKeys = screen.getAllByText("Esc");
      expect(escKeys.length).toBeGreaterThanOrEqual(1);
    });

    it("shows / key binding", () => {
      renderDialog();
      const slashKeys = screen.getAllByText("/");
      expect(slashKeys.length).toBeGreaterThanOrEqual(1);
    });

    it("shows ? key binding", () => {
      renderDialog();
      const qKeys = screen.getAllByText("?");
      expect(qKeys.length).toBeGreaterThanOrEqual(1);
    });

    it("shows Enter key binding", () => {
      renderDialog();
      const enterKeys = screen.getAllByText("Enter");
      expect(enterKeys.length).toBeGreaterThanOrEqual(1);
    });
  });
});
