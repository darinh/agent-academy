import { describe, expect, it } from "vitest";
import { renderToStaticMarkup } from "react-dom/server";
import { createElement } from "react";
import SkeletonLoader from "../SkeletonLoader";
import { FluentProvider, webDarkTheme } from "@fluentui/react-components";

function render(props?: { rows?: number; variant?: "list" | "chat" }) {
  return renderToStaticMarkup(
    createElement(FluentProvider, { theme: webDarkTheme },
      createElement(SkeletonLoader, props ?? {}),
    ),
  );
}

describe("SkeletonLoader", () => {
  it("renders 5 rows by default", () => {
    const html = render();
    // Each row has 2 line divs with inline width styles
    const widthMatches = html.match(/style="width:/g);
    expect(widthMatches).toHaveLength(10); // 5 rows × 2 lines
  });

  it("renders custom number of rows", () => {
    const html = render({ rows: 3 });
    const widthMatches = html.match(/style="width:/g);
    expect(widthMatches).toHaveLength(6); // 3 rows × 2 lines
  });

  it("list variant has fewer elements than chat variant", () => {
    const listHtml = render({ variant: "list", rows: 3 });
    const chatHtml = render({ variant: "chat", rows: 3 });
    // Chat variant adds circle divs before each row's lines, making it longer
    expect(chatHtml.length).toBeGreaterThan(listHtml.length);
  });

  it("chat variant renders circle elements", () => {
    const html = render({ variant: "chat" });
    // Chat variant includes circle divs before lines
    // The circle element renders extra elements compared to list variant
    const widthMatches = html.match(/style="width:/g);
    expect(widthMatches).toHaveLength(10); // still 5 rows × 2 lines
    // Chat variant has more DOM elements due to circle divs
    const listHtml = render({ variant: "list" });
    expect(html.length).toBeGreaterThan(listHtml.length);
  });
});
