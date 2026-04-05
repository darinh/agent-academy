import { describe, expect, it } from "vitest";
import { renderToStaticMarkup } from "react-dom/server";
import { createElement } from "react";
import Sparkline from "../Sparkline";

function render(data: number[], props: Record<string, unknown> = {}) {
  return renderToStaticMarkup(createElement(Sparkline, { data, ...props }));
}

describe("Sparkline", () => {
  it("renders nothing with fewer than 2 data points", () => {
    expect(render([])).toBe("");
    expect(render([5])).toBe("");
  });

  it("renders an SVG for 2+ data points", () => {
    const html = render([1, 2, 3]);
    expect(html).toContain("<svg");
    expect(html).toContain("<polyline");
    expect(html).toContain("<polygon");
  });

  it("includes aria-label for accessibility", () => {
    const html = render([1, 2, 3]);
    expect(html).toContain('aria-label="Sparkline trend"');
    expect(html).toContain('role="img"');
  });

  it("uses custom color for stroke", () => {
    const html = render([1, 2, 3], { color: "#f85149" });
    expect(html).toContain('stroke="#f85149"');
  });

  it("respects custom dimensions", () => {
    const html = render([1, 2, 3], { width: 200, height: 40 });
    expect(html).toContain('width="200"');
    expect(html).toContain('height="40"');
  });

  it("creates a gradient fill definition", () => {
    const html = render([1, 2, 3], { color: "#6cb6ff" });
    expect(html).toContain("linearGradient");
    expect(html).toContain("sparkfill-");
  });

  it("handles flat data (all same values) without NaN", () => {
    const html = render([5, 5, 5, 5]);
    expect(html).not.toContain("NaN");
    expect(html).toContain("<polyline");
  });

  it("handles data with zeros", () => {
    const html = render([0, 0, 3, 0, 0]);
    expect(html).not.toContain("NaN");
    expect(html).toContain("<polyline");
  });
});
