// @vitest-environment jsdom
import "@testing-library/jest-dom/vitest";
import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from "vitest";
import { FluentProvider, webDarkTheme } from "@fluentui/react-components";
import ChunkErrorBoundary from "../ChunkErrorBoundary";

// Fluent UI MessageBar uses ResizeObserver internally.
beforeAll(() => {
  globalThis.ResizeObserver ??= class {
    observe() {}
    unobserve() {}
    disconnect() {}
  } as unknown as typeof ResizeObserver;
});

// Helper that throws on demand to trigger the error boundary.
function ThrowingChild({ shouldThrow }: { shouldThrow: boolean }) {
  if (shouldThrow) throw new Error("chunk load failed");
  return <div>Child content</div>;
}

function renderBoundary(shouldThrow = false) {
  return render(
    <FluentProvider theme={webDarkTheme}>
      <ChunkErrorBoundary>
        <ThrowingChild shouldThrow={shouldThrow} />
      </ChunkErrorBoundary>
    </FluentProvider>,
  );
}

// Suppress React error-boundary console noise.
const originalError = console.error;
beforeEach(() => {
  console.error = vi.fn();
});
afterEach(() => {
  console.error = originalError;
  cleanup();
  document.body.innerHTML = "";
});

describe("ChunkErrorBoundary", () => {
  it("renders children when no error occurs", () => {
    renderBoundary(false);
    expect(screen.getByText("Child content")).toBeInTheDocument();
  });

  it("shows error UI when a child throws", () => {
    renderBoundary(true);
    expect(screen.queryByText("Child content")).not.toBeInTheDocument();
    expect(screen.getByText("Failed to load panel")).toBeInTheDocument();
  });

  it("shows 'Failed to load panel' title in error state", () => {
    renderBoundary(true);
    expect(screen.getByText("Failed to load panel")).toBeInTheDocument();
  });

  it("shows descriptive error message in error state", () => {
    renderBoundary(true);
    expect(
      screen.getByText(/a code chunk failed to load/i),
    ).toBeInTheDocument();
  });

  it("shows Retry and Reload page buttons in error state", () => {
    renderBoundary(true);
    expect(screen.getByRole("button", { name: "Retry" })).toBeInTheDocument();
    expect(
      screen.getByRole("button", { name: "Reload page" }),
    ).toBeInTheDocument();
  });

  it("clicking Retry resets the boundary and renders children again", async () => {
    // Use a mutable ref so the throw persists across React 19 concurrent/sync retries.
    const shouldThrow = { current: true };
    function ConditionalThrower() {
      if (shouldThrow.current) throw new Error("chunk load failed");
      return <div>Child content</div>;
    }

    render(
      <FluentProvider theme={webDarkTheme}>
        <ChunkErrorBoundary>
          <ConditionalThrower />
        </ChunkErrorBoundary>
      </FluentProvider>,
    );
    expect(screen.getByText("Failed to load panel")).toBeInTheDocument();

    // Stop throwing, then click Retry so the boundary re-renders children successfully.
    shouldThrow.current = false;
    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "Retry" }));

    expect(screen.getByText("Child content")).toBeInTheDocument();
    expect(screen.queryByText("Failed to load panel")).not.toBeInTheDocument();
  });

  it("Reload page button exists in error state", () => {
    renderBoundary(true);
    const btn = screen.getByRole("button", { name: "Reload page" });
    expect(btn).toBeInTheDocument();
  });
});
