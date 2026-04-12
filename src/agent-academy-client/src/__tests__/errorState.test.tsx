import { describe, expect, it } from "vitest";
import { renderToStaticMarkup } from "react-dom/server";
import { createElement } from "react";
import ErrorState from "../ErrorState";
import { FluentProvider, webDarkTheme } from "@fluentui/react-components";

function render(props: { message: string; detail?: string; onRetry?: () => void }) {
  return renderToStaticMarkup(
    createElement(FluentProvider, { theme: webDarkTheme },
      createElement(ErrorState, props),
    ),
  );
}

describe("ErrorState", () => {
  it("renders message text", () => {
    const html = render({ message: "Something went wrong" });
    expect(html).toContain("Something went wrong");
  });

  it("renders detail when provided", () => {
    const html = render({ message: "Error", detail: "Check your connection" });
    expect(html).toContain("Check your connection");
  });

  it("does not render detail when omitted", () => {
    const html = render({ message: "Error" });
    expect(html).not.toContain("Check your connection");
  });

  it("renders Try again button when onRetry provided", () => {
    const html = render({ message: "Error", onRetry: () => {} });
    expect(html).toContain("Try again");
  });

  it("does not render retry button when onRetry omitted", () => {
    const html = render({ message: "Error" });
    expect(html).not.toContain("Try again");
  });
});
