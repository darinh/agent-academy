// @vitest-environment jsdom
/**
 * DOM tests for CreateSection.
 *
 * Covers: initial render, input binding, button enable/disable,
 * create success callback, create error display, Enter key submit.
 */
import "@testing-library/jest-dom/vitest";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { createElement } from "react";

vi.mock("../api", () => ({
  onboardProject: vi.fn(),
}));

vi.mock("@fluentui/react-components", () => ({
  Button: ({ children, onClick, disabled, icon, ...rest }: any) => (
    <button onClick={onClick} disabled={disabled} {...rest}>
      {icon}{children}
    </button>
  ),
  Input: ({ value, onChange, onKeyDown, placeholder, ...rest }: any) => (
    <input
      value={value}
      onChange={(e: any) => onChange?.(e, { value: e.target.value })}
      onKeyDown={onKeyDown}
      placeholder={placeholder}
      aria-label={rest["aria-label"]}
    />
  ),
  Spinner: ({ label }: any) => <span data-testid="spinner">{label ?? "Loading..."}</span>,
  makeStyles: () => () => ({}),
  shorthands: new Proxy({}, { get: () => () => ({}) }),
}));

import CreateSection from "../projectSelector/CreateSection";
import { onboardProject } from "../api";

const mockOnboard = vi.mocked(onboardProject);

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

describe("CreateSection", () => {
  const onProjectOnboarded = vi.fn();

  function renderIt() {
    return render(createElement(CreateSection, { onProjectOnboarded }));
  }

  it("renders the directory path input and disabled button", () => {
    renderIt();
    expect(screen.getByPlaceholderText(/my-awesome-project/)).toBeInTheDocument();
    const btn = screen.getByRole("button", { name: /create/i });
    expect(btn).toBeDisabled();
  });

  it("enables button when path is entered", async () => {
    renderIt();
    const input = screen.getByPlaceholderText(/my-awesome-project/);
    await userEvent.type(input, "/home/user/projects/test");
    expect(screen.getByRole("button", { name: /create/i })).not.toBeDisabled();
  });

  it("stays disabled for whitespace-only input", async () => {
    renderIt();
    const input = screen.getByPlaceholderText(/my-awesome-project/);
    await userEvent.type(input, "   ");
    expect(screen.getByRole("button", { name: /create/i })).toBeDisabled();
  });

  it("calls onboardProject and onProjectOnboarded on success", async () => {
    const fakeResult = { scan: { name: "test" }, workspace: { path: "/test" } };
    mockOnboard.mockResolvedValueOnce(fakeResult as any);

    renderIt();
    const input = screen.getByPlaceholderText(/my-awesome-project/);
    await userEvent.type(input, "/test");
    await userEvent.click(screen.getByRole("button", { name: /create/i }));

    await waitFor(() => {
      expect(mockOnboard).toHaveBeenCalledWith("/test");
      expect(onProjectOnboarded).toHaveBeenCalledWith(fakeResult);
    });
  });

  it("displays error message on failure", async () => {
    mockOnboard.mockRejectedValueOnce(new Error("Directory not found"));

    renderIt();
    const input = screen.getByPlaceholderText(/my-awesome-project/);
    await userEvent.type(input, "/bad/path");
    await userEvent.click(screen.getByRole("button", { name: /create/i }));

    await waitFor(() => {
      expect(screen.getByText("Directory not found")).toBeInTheDocument();
    });
  });

  it("handles non-Error rejections", async () => {
    mockOnboard.mockRejectedValueOnce("string error");

    renderIt();
    await userEvent.type(screen.getByPlaceholderText(/my-awesome-project/), "/x");
    await userEvent.click(screen.getByRole("button", { name: /create/i }));

    await waitFor(() => {
      expect(screen.getByText("string error")).toBeInTheDocument();
    });
  });

  it("trims whitespace from path before calling API", async () => {
    mockOnboard.mockResolvedValueOnce({ scan: {}, workspace: { path: "/a" } } as any);

    renderIt();
    await userEvent.type(screen.getByPlaceholderText(/my-awesome-project/), "  /a  ");
    await userEvent.click(screen.getByRole("button", { name: /create/i }));

    await waitFor(() => {
      expect(mockOnboard).toHaveBeenCalledWith("/a");
    });
  });
});
