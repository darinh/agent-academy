import { describe, expect, it } from "vitest";
import { renderToStaticMarkup } from "react-dom/server";
import { createElement } from "react";
import CircuitBreakerBanner from "../CircuitBreakerBanner";
import { FluentProvider, webDarkTheme } from "@fluentui/react-components";

function renderBanner(state: "Closed" | "Open" | "HalfOpen" | null) {
  return renderToStaticMarkup(
    createElement(FluentProvider, { theme: webDarkTheme },
      createElement(CircuitBreakerBanner, { state }),
    ),
  );
}

describe("CircuitBreakerBanner", () => {
  describe("visibility", () => {
    it("renders nothing when state is null", () => {
      const html = renderBanner(null);
      expect(html).not.toContain("circuit-breaker-banner");
      expect(html).not.toContain("Circuit breaker");
    });

    it("renders nothing when state is Closed", () => {
      const html = renderBanner("Closed");
      expect(html).not.toContain("circuit-breaker-banner");
      expect(html).not.toContain("Circuit breaker");
    });

    it("renders banner when state is Open", () => {
      const html = renderBanner("Open");
      expect(html).toContain("circuit-breaker-banner");
      expect(html).toContain("Requests paused");
      expect(html).toContain("Agent requests are paused while the backend recovers");
    });

    it("renders banner when state is HalfOpen", () => {
      const html = renderBanner("HalfOpen");
      expect(html).toContain("circuit-breaker-banner");
      expect(html).toContain("Checking recovery");
      expect(html).toContain("Sending a test request");
    });
  });

  describe("content", () => {
    it("shows recovery messaging for Open state", () => {
      const html = renderBanner("Open");
      expect(html).toContain("within a minute");
      expect(html).toContain("repeated failures");
    });

    it("shows probe messaging for HalfOpen state", () => {
      const html = renderBanner("HalfOpen");
      expect(html).toContain("probe request");
      expect(html).toContain("resumes immediately");
    });

    it("uses alert role for accessibility", () => {
      const html = renderBanner("Open");
      expect(html).toContain('role="alert"');
      expect(html).toContain('aria-live="assertive"');
    });
  });
});
