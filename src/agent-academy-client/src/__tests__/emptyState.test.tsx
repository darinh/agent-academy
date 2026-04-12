import { describe, expect, it } from "vitest";
import { renderToStaticMarkup } from "react-dom/server";
import { createElement } from "react";
import EmptyState from "../EmptyState";
import { FluentProvider, webDarkTheme } from "@fluentui/react-components";

function render(props: {
  icon: React.ReactNode;
  title: string;
  detail?: string;
  action?: { label: string; onClick: () => void };
}) {
  return renderToStaticMarkup(
    createElement(FluentProvider, { theme: webDarkTheme },
      createElement(EmptyState, props),
    ),
  );
}

describe("EmptyState", () => {
  it("renders icon and title", () => {
    const html = render({ icon: createElement("span", null, "★"), title: "No items" });
    expect(html).toContain("★");
    expect(html).toContain("No items");
  });

  it("renders detail when provided", () => {
    const html = render({ icon: createElement("span", null, "★"), title: "No items", detail: "Try adding one" });
    expect(html).toContain("Try adding one");
  });

  it("does not render detail when omitted", () => {
    const html = render({ icon: createElement("span", null, "★"), title: "No items" });
    expect(html).not.toContain("Try adding one");
  });

  it("renders action button with label when provided", () => {
    const html = render({
      icon: createElement("span", null, "★"),
      title: "No items",
      action: { label: "Add item", onClick: () => {} },
    });
    expect(html).toContain("Add item");
  });

  it("does not render action button when omitted", () => {
    const html = render({ icon: createElement("span", null, "★"), title: "No items" });
    expect(html).not.toContain("button");
  });
});
