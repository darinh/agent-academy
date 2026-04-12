// @vitest-environment jsdom
/**
 * Standalone tests for V3Badge component.
 *
 * Covers: rendering children, color variants, custom className,
 * custom style overrides, uppercase text transform.
 */
import "@testing-library/jest-dom/vitest";
import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import { createElement } from "react";
import { FluentProvider, webDarkTheme } from "@fluentui/react-components";

import V3Badge from "../V3Badge";
import type { BadgeColor } from "../V3Badge";

function renderBadge(props: Partial<Parameters<typeof V3Badge>[0]> & { children?: string; color?: BadgeColor } = {}) {
  const defaults = {
    children: "active",
    color: "active" as BadgeColor,
  };
  const merged = { ...defaults, ...props };
  render(
    createElement(
      FluentProvider,
      { theme: webDarkTheme },
      createElement(V3Badge, merged),
    ),
  );
  return merged;
}

afterEach(() => {
  cleanup();
  document.body.innerHTML = "";
});

describe("V3Badge", () => {
  describe("rendering", () => {
    it("renders children text", () => {
      renderBadge({ children: "Running" });
      expect(screen.getByText("Running")).toBeInTheDocument();
    });

    it("renders as a span element", () => {
      renderBadge({ children: "test" });
      const el = screen.getByText("test");
      expect(el.tagName).toBe("SPAN");
    });

    it("applies uppercase text-transform", () => {
      renderBadge({ children: "done" });
      const el = screen.getByText("done");
      expect(el.style.textTransform).toBe("uppercase");
    });
  });

  describe("color variants", () => {
    const colors: BadgeColor[] = [
      "active", "review", "done", "cancel", "feat",
      "bug", "info", "warn", "err", "ok", "muted", "tool",
    ];

    for (const color of colors) {
      it(`renders '${color}' variant without crashing`, () => {
        renderBadge({ children: color, color });
        expect(screen.getByText(color)).toBeInTheDocument();
      });
    }

    it("applies correct background for 'done' color", () => {
      renderBadge({ children: "done", color: "done" });
      const el = screen.getByText("done");
      expect(el.style.background).toBe("rgba(76, 175, 80, 0.15)");
    });

    it("applies correct color for 'err' variant", () => {
      renderBadge({ children: "error", color: "err" });
      const el = screen.getByText("error");
      expect(el.style.color).toBe("var(--aa-copper)");
    });
  });

  describe("props", () => {
    it("applies custom className", () => {
      renderBadge({ children: "tagged", className: "my-custom-class" });
      const el = screen.getByText("tagged");
      expect(el.classList.contains("my-custom-class")).toBe(true);
    });

    it("merges custom style overrides", () => {
      renderBadge({ children: "styled", style: { marginLeft: "8px" } });
      const el = screen.getByText("styled");
      expect(el.style.marginLeft).toBe("8px");
    });

    it("custom style does not remove base styles", () => {
      renderBadge({ children: "styled", style: { marginLeft: "8px" } });
      const el = screen.getByText("styled");
      expect(el.style.fontSize).toBe("9px");
      expect(el.style.textTransform).toBe("uppercase");
    });
  });
});
