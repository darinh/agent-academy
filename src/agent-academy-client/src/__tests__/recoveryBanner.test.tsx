import { describe, expect, it } from "vitest";
import { renderToStaticMarkup } from "react-dom/server";
import { createElement } from "react";
import RecoveryBanner from "../RecoveryBanner";
import type { RecoveryBannerState } from "../RecoveryBanner";
import { FluentProvider, webDarkTheme } from "@fluentui/react-components";

function render(state: RecoveryBannerState) {
  return renderToStaticMarkup(
    createElement(FluentProvider, { theme: webDarkTheme },
      createElement(RecoveryBanner, { state }),
    ),
  );
}

describe("RecoveryBanner", () => {
  it("reconnecting tone shows 'Reconnecting' badge text", () => {
    const html = render({ tone: "reconnecting", message: "Trying to reconnect" });
    expect(html).toContain("Reconnecting");
  });

  it("syncing tone shows 'Recovery sync' badge text", () => {
    const html = render({ tone: "syncing", message: "Syncing data" });
    expect(html).toContain("Recovery sync");
  });

  it("crash tone shows 'Crash recovered' badge text", () => {
    const html = render({ tone: "crash", message: "Service crashed" });
    expect(html).toContain("Crash recovered");
  });

  it("error tone shows 'Recovery needs attention' badge text", () => {
    const html = render({ tone: "error", message: "Recovery failed" });
    expect(html).toContain("Recovery needs attention");
  });

  it("renders message text", () => {
    const html = render({ tone: "syncing", message: "Syncing state from server" });
    expect(html).toContain("Syncing state from server");
  });

  it("renders detail when provided", () => {
    const html = render({ tone: "crash", message: "Crash", detail: "Last seen 2s ago" });
    expect(html).toContain("Last seen 2s ago");
  });

  it("does not render detail when omitted", () => {
    const html = render({ tone: "crash", message: "Crash" });
    expect(html).not.toContain("Last seen 2s ago");
  });

  it("has role='status' for accessibility", () => {
    const html = render({ tone: "reconnecting", message: "Reconnecting..." });
    expect(html).toContain('role="status"');
  });
});
