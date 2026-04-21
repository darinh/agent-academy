// @vitest-environment jsdom
import { describe, expect, it, vi, beforeEach } from "vitest";
import { render, fireEvent, waitFor, act } from "@testing-library/react";
import type { BrowseResult, OnboardResult, ProjectScanResult } from "../api";

vi.mock("../api", () => ({
  scanProject: vi.fn(),
  onboardProject: vi.fn(),
  browseDirectory: vi.fn(),
}));

vi.mock("@fluentui/react-components", () => ({
  Button: ({ children, onClick, disabled, appearance, size, icon, ...rest }: any) => (
    <button onClick={onClick} disabled={disabled} data-appearance={appearance} {...rest}>
      {icon}{children}
    </button>
  ),
  Dialog: ({ children, open }: any) => (open ? <div data-testid="dialog">{children}</div> : null),
  DialogActions: ({ children }: any) => <div data-testid="dialog-actions">{children}</div>,
  DialogBody: ({ children }: any) => <div>{children}</div>,
  DialogContent: ({ children }: any) => <div>{children}</div>,
  DialogSurface: ({ children }: any) => <div>{children}</div>,
  DialogTitle: ({ children }: any) => <div data-testid="dialog-title">{children}</div>,
  Input: ({ value, onChange, onKeyDown, placeholder, contentAfter, ...rest }: any) => (
    <div>
      <input
        value={value}
        onChange={(e: any) => onChange?.(e, { value: e.target.value })}
        onKeyDown={onKeyDown}
        placeholder={placeholder}
        aria-label={rest["aria-label"]}
      />
      {contentAfter}
    </div>
  ),
  Spinner: ({ label }: any) => <span data-testid="spinner">{label ?? "Loading..."}</span>,
  makeStyles: () => () => ({}),
  shorthands: new Proxy({}, { get: () => () => ({}) }),
}));

vi.mock("../projectSelector/projectSelectorStyles", () => ({
  useProjectSelectorStyles: () => ({}),
}));

import { scanProject, onboardProject, browseDirectory } from "../api";
import OnboardSection from "../projectSelector/OnboardSection";

const mockScanProject = vi.mocked(scanProject);
const mockOnboardProject = vi.mocked(onboardProject);
const mockBrowseDirectory = vi.mocked(browseDirectory);

function makeScanResult(overrides: Partial<ProjectScanResult> = {}): ProjectScanResult {
  return {
    path: "/home/user/project",
    projectName: "my-project",
    techStack: ["TypeScript", "React"],
    hasSpecs: false,
    hasReadme: true,
    isGitRepo: true,
    gitBranch: "main",
    detectedFiles: ["package.json", "tsconfig.json"],
    ...overrides,
  };
}

function makeBrowseResult(overrides: Partial<BrowseResult> = {}): BrowseResult {
  return {
    current: "/home/user",
    parent: "/home",
    entries: [
      { name: "projects", path: "/home/user/projects", isDirectory: true },
      { name: "documents", path: "/home/user/documents", isDirectory: true },
      { name: "readme.txt", path: "/home/user/readme.txt", isDirectory: false },
    ],
    ...overrides,
  };
}

function makeOnboardResult(overrides: Partial<OnboardResult> = {}): OnboardResult {
  return {
    scan: makeScanResult(),
    workspace: { path: "/home/user/project", projectName: "my-project", lastAccessedAt: new Date().toISOString() },
    specTaskCreated: true,
    roomId: "room-1",
    ...overrides,
  };
}

describe("OnboardSection", () => {
  const onProjectOnboarded = vi.fn();

  beforeEach(() => {
    vi.clearAllMocks();
  });

  // ── Initial render ──

  it("renders directory path input with placeholder", () => {
    const { container } = render(<OnboardSection onProjectOnboarded={onProjectOnboarded} />);
    const input = container.querySelector("input")!;
    expect(input).toBeTruthy();
    expect(input.placeholder).toContain("/home/user/projects");
  });

  it("renders Scan button (disabled when input is empty)", () => {
    const { container } = render(<OnboardSection onProjectOnboarded={onProjectOnboarded} />);
    const scanBtn = findButton(container, /^Scan$/);
    expect(scanBtn).toBeTruthy();
    expect(scanBtn!.disabled).toBe(true);
  });

  it("renders Browse directories button", () => {
    const { container } = render(<OnboardSection onProjectOnboarded={onProjectOnboarded} />);
    const browseBtn = findButton(container, /Browse directories/);
    expect(browseBtn).toBeTruthy();
  });

  // ── Scan flow ──

  it("enables Scan button when directory path is entered", () => {
    const { container } = render(<OnboardSection onProjectOnboarded={onProjectOnboarded} />);
    const input = container.querySelector("input")!;
    fireEvent.change(input, { target: { value: "/some/path" } });
    const scanBtn = findButton(container, /^Scan$/)!;
    expect(scanBtn.disabled).toBe(false);
  });

  it("calls scanProject and shows results on Scan click", async () => {
    const result = makeScanResult();
    mockScanProject.mockResolvedValueOnce(result);

    const { container } = render(<OnboardSection onProjectOnboarded={onProjectOnboarded} />);
    const input = container.querySelector("input")!;
    fireEvent.change(input, { target: { value: "/home/user/project" } });

    await act(async () => {
      fireEvent.click(findButton(container, /^Scan$/)!);
    });

    await waitFor(() => {
      expect(mockScanProject).toHaveBeenCalledWith("/home/user/project");
      expect(container.textContent).toContain("my-project");
    });
  });

  it("triggers scan on Enter key in input", async () => {
    mockScanProject.mockResolvedValueOnce(makeScanResult());

    const { container } = render(<OnboardSection onProjectOnboarded={onProjectOnboarded} />);
    const input = container.querySelector("input")!;
    fireEvent.change(input, { target: { value: "/home/user/project" } });

    await act(async () => {
      fireEvent.keyDown(input, { key: "Enter" });
    });

    await waitFor(() => {
      expect(mockScanProject).toHaveBeenCalledWith("/home/user/project");
    });
  });

  it("shows spinner while scanning", async () => {
    let resolvePromise: (v: ProjectScanResult) => void;
    mockScanProject.mockReturnValueOnce(
      new Promise((resolve) => { resolvePromise = resolve; }),
    );

    const { container } = render(<OnboardSection onProjectOnboarded={onProjectOnboarded} />);
    const input = container.querySelector("input")!;
    fireEvent.change(input, { target: { value: "/path" } });

    await act(async () => {
      fireEvent.click(findButton(container, /^Scan$/)!);
    });

    expect(container.textContent).toContain("Scanning project");

    await act(async () => {
      resolvePromise!(makeScanResult());
    });
  });

  it("shows scan error on failure", async () => {
    mockScanProject.mockRejectedValueOnce(new Error("Directory not found"));

    const { container } = render(<OnboardSection onProjectOnboarded={onProjectOnboarded} />);
    const input = container.querySelector("input")!;
    fireEvent.change(input, { target: { value: "/bad/path" } });

    await act(async () => {
      fireEvent.click(findButton(container, /^Scan$/)!);
    });

    await waitFor(() => {
      expect(container.textContent).toContain("Directory not found");
    });
  });

  it("handles non-Error rejection in scan", async () => {
    mockScanProject.mockRejectedValueOnce("string error");

    const { container } = render(<OnboardSection onProjectOnboarded={onProjectOnboarded} />);
    const input = container.querySelector("input")!;
    fireEvent.change(input, { target: { value: "/bad/path" } });

    await act(async () => {
      fireEvent.click(findButton(container, /^Scan$/)!);
    });

    await waitFor(() => {
      expect(container.textContent).toContain("string error");
    });
  });

  // ── Scan results display ──

  it("displays tech stack badges", async () => {
    mockScanProject.mockResolvedValueOnce(
      makeScanResult({ techStack: ["C#", "ASP.NET", "React"] }),
    );

    const { container } = render(<OnboardSection onProjectOnboarded={onProjectOnboarded} />);
    const input = container.querySelector("input")!;
    fireEvent.change(input, { target: { value: "/path" } });

    await act(async () => {
      fireEvent.click(findButton(container, /^Scan$/)!);
    });

    await waitFor(() => {
      expect(container.textContent).toContain("C#");
      expect(container.textContent).toContain("ASP.NET");
      expect(container.textContent).toContain("React");
    });
  });

  it("shows git repo detected when isGitRepo is true", async () => {
    mockScanProject.mockResolvedValueOnce(makeScanResult({ isGitRepo: true, gitBranch: "develop" }));

    const { container } = render(<OnboardSection onProjectOnboarded={onProjectOnboarded} />);
    const input = container.querySelector("input")!;
    fireEvent.change(input, { target: { value: "/path" } });

    await act(async () => {
      fireEvent.click(findButton(container, /^Scan$/)!);
    });

    await waitFor(() => {
      expect(container.textContent).toContain("Repository detected");
      expect(container.textContent).toContain("develop");
    });
  });

  it("shows no git repo message when isGitRepo is false", async () => {
    mockScanProject.mockResolvedValueOnce(makeScanResult({ isGitRepo: false }));

    const { container } = render(<OnboardSection onProjectOnboarded={onProjectOnboarded} />);
    const input = container.querySelector("input")!;
    fireEvent.change(input, { target: { value: "/path" } });

    await act(async () => {
      fireEvent.click(findButton(container, /^Scan$/)!);
    });

    await waitFor(() => {
      expect(container.textContent).toContain("No git repository detected");
    });
  });

  it("shows existing specs message when hasSpecs is true", async () => {
    mockScanProject.mockResolvedValueOnce(makeScanResult({ hasSpecs: true }));

    const { container } = render(<OnboardSection onProjectOnboarded={onProjectOnboarded} />);
    const input = container.querySelector("input")!;
    fireEvent.change(input, { target: { value: "/path" } });

    await act(async () => {
      fireEvent.click(findButton(container, /^Scan$/)!);
    });

    await waitFor(() => {
      expect(container.textContent).toContain("Existing spec set found");
    });
  });

  it("shows no specs message when hasSpecs is false", async () => {
    mockScanProject.mockResolvedValueOnce(makeScanResult({ hasSpecs: false }));

    const { container } = render(<OnboardSection onProjectOnboarded={onProjectOnboarded} />);
    const input = container.querySelector("input")!;
    fireEvent.change(input, { target: { value: "/path" } });

    await act(async () => {
      fireEvent.click(findButton(container, /^Scan$/)!);
    });

    await waitFor(() => {
      expect(container.textContent).toContain("No specs found");
    });
  });

  it("falls back to path when projectName is null", async () => {
    mockScanProject.mockResolvedValueOnce(makeScanResult({ projectName: null }));

    const { container } = render(<OnboardSection onProjectOnboarded={onProjectOnboarded} />);
    const input = container.querySelector("input")!;
    fireEvent.change(input, { target: { value: "/home/user/project" } });

    await act(async () => {
      fireEvent.click(findButton(container, /^Scan$/)!);
    });

    await waitFor(() => {
      expect(container.textContent).toContain("/home/user/project");
    });
  });

  it("clears scan result when input changes from scanned path", async () => {
    mockScanProject.mockResolvedValueOnce(makeScanResult());

    const { container } = render(<OnboardSection onProjectOnboarded={onProjectOnboarded} />);
    const input = container.querySelector("input")!;
    fireEvent.change(input, { target: { value: "/home/user/project" } });

    await act(async () => {
      fireEvent.click(findButton(container, /^Scan$/)!);
    });

    await waitFor(() => {
      expect(container.textContent).toContain("my-project");
    });

    // Change the input to a different path
    fireEvent.change(input, { target: { value: "/different/path" } });
    expect(container.textContent).not.toContain("Onboard project");
  });

  // ── Onboard dialog ──

  it("shows Onboard project button after successful scan", async () => {
    mockScanProject.mockResolvedValueOnce(makeScanResult());

    const { container } = render(<OnboardSection onProjectOnboarded={onProjectOnboarded} />);
    const input = container.querySelector("input")!;
    fireEvent.change(input, { target: { value: "/path" } });

    await act(async () => {
      fireEvent.click(findButton(container, /^Scan$/)!);
    });

    await waitFor(() => {
      const onboardBtn = findButton(container, /^Onboard project$/);
      expect(onboardBtn).toBeTruthy();
    });
  });

  it("opens confirmation dialog on Onboard project click", async () => {
    mockScanProject.mockResolvedValueOnce(makeScanResult());

    const { container } = render(<OnboardSection onProjectOnboarded={onProjectOnboarded} />);
    const input = container.querySelector("input")!;
    fireEvent.change(input, { target: { value: "/path" } });

    await act(async () => {
      fireEvent.click(findButton(container, /^Scan$/)!);
    });

    await waitFor(() => {
      fireEvent.click(findButton(container, /^Onboard project$/)!);
    });

    await waitFor(() => {
      expect(container.querySelector("[data-testid='dialog']")).toBeTruthy();
      expect(container.textContent).toContain("my-project");
    });
  });

  it("shows spec note in dialog based on hasSpecs", async () => {
    mockScanProject.mockResolvedValueOnce(makeScanResult({ hasSpecs: true }));

    const { container } = render(<OnboardSection onProjectOnboarded={onProjectOnboarded} />);
    const input = container.querySelector("input")!;
    fireEvent.change(input, { target: { value: "/path" } });

    await act(async () => {
      fireEvent.click(findButton(container, /^Scan$/)!);
    });

    await waitFor(() => {
      fireEvent.click(findButton(container, /^Onboard project$/)!);
    });

    await waitFor(() => {
      expect(container.textContent).toContain("Existing specification found");
    });
  });

  it("shows no-spec note in dialog when hasSpecs is false", async () => {
    mockScanProject.mockResolvedValueOnce(makeScanResult({ hasSpecs: false }));

    const { container } = render(<OnboardSection onProjectOnboarded={onProjectOnboarded} />);
    const input = container.querySelector("input")!;
    fireEvent.change(input, { target: { value: "/path" } });

    await act(async () => {
      fireEvent.click(findButton(container, /^Scan$/)!);
    });

    await waitFor(() => {
      fireEvent.click(findButton(container, /^Onboard project$/)!);
    });

    await waitFor(() => {
      expect(container.textContent).toContain("No specification found");
    });
  });

  it("calls onboardProject and onProjectOnboarded on confirm", async () => {
    mockScanProject.mockResolvedValueOnce(makeScanResult());
    const result = makeOnboardResult();
    mockOnboardProject.mockResolvedValueOnce(result);

    const { container } = render(<OnboardSection onProjectOnboarded={onProjectOnboarded} />);
    const input = container.querySelector("input")!;
    fireEvent.change(input, { target: { value: "/home/user/project" } });

    await act(async () => {
      fireEvent.click(findButton(container, /^Scan$/)!);
    });

    await waitFor(() => {
      fireEvent.click(findButton(container, /^Onboard project$/)!);
    });

    await act(async () => {
      const dialog = container.querySelector("[data-testid='dialog']")!;
      const onboardBtn = findButton(dialog as HTMLElement, /^Onboard$/)!;
      fireEvent.click(onboardBtn);
    });

    await waitFor(() => {
      expect(mockOnboardProject).toHaveBeenCalledWith("/home/user/project");
      expect(onProjectOnboarded).toHaveBeenCalledWith(result);
    });
  });

  it("shows error in dialog on onboard failure", async () => {
    mockScanProject.mockResolvedValueOnce(makeScanResult());
    mockOnboardProject.mockRejectedValueOnce(new Error("Onboard failed: disk full"));

    const { container } = render(<OnboardSection onProjectOnboarded={onProjectOnboarded} />);
    const input = container.querySelector("input")!;
    fireEvent.change(input, { target: { value: "/path" } });

    await act(async () => {
      fireEvent.click(findButton(container, /^Scan$/)!);
    });

    await waitFor(() => {
      fireEvent.click(findButton(container, /^Onboard project$/)!);
    });

    await act(async () => {
      const dialog = container.querySelector("[data-testid='dialog']")!;
      fireEvent.click(findButton(dialog as HTMLElement, /^Onboard$/)!);
    });

    await waitFor(() => {
      expect(container.textContent).toContain("Onboard failed: disk full");
    });
  });

  it("closes dialog on Cancel click", async () => {
    mockScanProject.mockResolvedValueOnce(makeScanResult());

    const { container } = render(<OnboardSection onProjectOnboarded={onProjectOnboarded} />);
    const input = container.querySelector("input")!;
    fireEvent.change(input, { target: { value: "/path" } });

    await act(async () => {
      fireEvent.click(findButton(container, /^Scan$/)!);
    });

    await waitFor(() => {
      fireEvent.click(findButton(container, /^Onboard project$/)!);
    });

    await waitFor(() => {
      const dialog = container.querySelector("[data-testid='dialog']")!;
      fireEvent.click(findButton(dialog as HTMLElement, /Cancel/)!);
    });

    await waitFor(() => {
      expect(container.querySelector("[data-testid='dialog']")).toBeNull();
    });
  });

  // ── Browse flow ──

  it("opens directory browser on Browse button click", async () => {
    mockBrowseDirectory.mockResolvedValueOnce(makeBrowseResult());

    const { container } = render(<OnboardSection onProjectOnboarded={onProjectOnboarded} />);

    await act(async () => {
      fireEvent.click(findButton(container, /Browse directories/)!);
    });

    await waitFor(() => {
      expect(mockBrowseDirectory).toHaveBeenCalledWith(undefined);
      expect(container.textContent).toContain("/home/user");
      expect(container.textContent).toContain("projects");
      expect(container.textContent).toContain("documents");
    });
  });

  it("filters browse entries to only directories", async () => {
    mockBrowseDirectory.mockResolvedValueOnce(makeBrowseResult());

    const { container } = render(<OnboardSection onProjectOnboarded={onProjectOnboarded} />);

    await act(async () => {
      fireEvent.click(findButton(container, /Browse directories/)!);
    });

    await waitFor(() => {
      // readme.txt is a file, should not appear as a browsable entry
      expect(container.textContent).not.toContain("readme.txt");
    });
  });

  it("navigates to parent directory", async () => {
    mockBrowseDirectory.mockResolvedValueOnce(makeBrowseResult());

    const { container } = render(<OnboardSection onProjectOnboarded={onProjectOnboarded} />);

    await act(async () => {
      fireEvent.click(findButton(container, /Browse directories/)!);
    });

    await waitFor(() => {
      expect(container.textContent).toContain("Parent directory");
    });

    mockBrowseDirectory.mockResolvedValueOnce(
      makeBrowseResult({ current: "/home", parent: "/", entries: [] }),
    );

    // Click parent directory
    const parentBtn = Array.from(container.querySelectorAll("button[type='button']"))
      .find((b) => b.textContent?.includes("Parent directory"));
    expect(parentBtn).toBeTruthy();

    await act(async () => {
      fireEvent.click(parentBtn!);
    });

    await waitFor(() => {
      expect(mockBrowseDirectory).toHaveBeenCalledWith("/home");
    });
  });

  it("navigates into a subdirectory", async () => {
    mockBrowseDirectory.mockResolvedValueOnce(makeBrowseResult());

    const { container } = render(<OnboardSection onProjectOnboarded={onProjectOnboarded} />);

    await act(async () => {
      fireEvent.click(findButton(container, /Browse directories/)!);
    });

    await waitFor(() => {
      expect(container.textContent).toContain("projects");
    });

    mockBrowseDirectory.mockResolvedValueOnce(
      makeBrowseResult({ current: "/home/user/projects", parent: "/home/user", entries: [] }),
    );

    const dirBtn = Array.from(container.querySelectorAll("button[type='button']"))
      .find((b) => b.textContent?.includes("projects"));

    await act(async () => {
      fireEvent.click(dirBtn!);
    });

    await waitFor(() => {
      expect(mockBrowseDirectory).toHaveBeenCalledWith("/home/user/projects");
    });
  });

  it("selects browsed directory and triggers scan", async () => {
    mockBrowseDirectory.mockResolvedValueOnce(makeBrowseResult({ current: "/home/user/project" }));
    mockScanProject.mockResolvedValueOnce(makeScanResult());

    const { container } = render(<OnboardSection onProjectOnboarded={onProjectOnboarded} />);

    await act(async () => {
      fireEvent.click(findButton(container, /Browse directories/)!);
    });

    await waitFor(() => {
      expect(findButton(container, /Select this directory/)).toBeTruthy();
    });

    await act(async () => {
      fireEvent.click(findButton(container, /Select this directory/)!);
    });

    await waitFor(() => {
      expect(mockScanProject).toHaveBeenCalledWith("/home/user/project");
    });
  });

  it("toggles to 'Close browser' when browsing", async () => {
    mockBrowseDirectory.mockResolvedValueOnce(makeBrowseResult());

    const { container } = render(<OnboardSection onProjectOnboarded={onProjectOnboarded} />);

    await act(async () => {
      fireEvent.click(findButton(container, /Browse directories/)!);
    });

    await waitFor(() => {
      expect(findButton(container, /Close browser/)).toBeTruthy();
    });

    // Close the browser
    fireEvent.click(findButton(container, /Close browser/)!);
    expect(findButton(container, /Browse directories/)).toBeTruthy();
  });

  it("shows browse error on failure", async () => {
    mockBrowseDirectory.mockRejectedValueOnce(new Error("Permission denied"));

    const { container } = render(<OnboardSection onProjectOnboarded={onProjectOnboarded} />);

    await act(async () => {
      fireEvent.click(findButton(container, /Browse directories/)!);
    });

    await waitFor(() => {
      expect(container.textContent).toContain("Permission denied");
    });
  });

  it("does not show parent button when parent is null", async () => {
    mockBrowseDirectory.mockResolvedValueOnce(
      makeBrowseResult({ parent: null }),
    );

    const { container } = render(<OnboardSection onProjectOnboarded={onProjectOnboarded} />);

    await act(async () => {
      fireEvent.click(findButton(container, /Browse directories/)!);
    });

    await waitFor(() => {
      expect(container.textContent).not.toContain("Parent directory");
    });
  });

  // ── Scan nonce (race condition prevention) ──

  it("ignores stale scan results when a newer scan completes after the latest", async () => {
    const staleResult = makeScanResult({ projectName: "stale-project" });
    const freshResult = makeScanResult({ projectName: "fresh-project" });

    let resolveStale: (v: ProjectScanResult) => void;
    const stalePromise = new Promise<ProjectScanResult>((r) => { resolveStale = r; });

    mockScanProject
      .mockReturnValueOnce(stalePromise)
      .mockResolvedValueOnce(freshResult);

    const { container } = render(<OnboardSection onProjectOnboarded={onProjectOnboarded} />);
    const input = container.querySelector("input")!;

    // Start first scan via Enter key
    fireEvent.change(input, { target: { value: "/path1" } });
    await act(async () => {
      fireEvent.keyDown(input, { key: "Enter" });
    });

    // Start second scan via Enter key (button is disabled during scan, but Enter works)
    fireEvent.change(input, { target: { value: "/path2" } });
    await act(async () => {
      fireEvent.keyDown(input, { key: "Enter" });
    });

    // Second scan resolves immediately (mockResolvedValueOnce)
    await waitFor(() => {
      expect(container.textContent).toContain("fresh-project");
    });

    // Now resolve stale scan — should be ignored due to nonce mismatch
    await act(async () => {
      resolveStale!(staleResult);
    });

    // Should still show fresh project
    expect(container.textContent).toContain("fresh-project");
    expect(container.textContent).not.toContain("stale-project");
  });

  // ── Empty path guard ──

  it("does not scan when input is whitespace-only", async () => {
    const { container } = render(<OnboardSection onProjectOnboarded={onProjectOnboarded} />);
    const input = container.querySelector("input")!;
    fireEvent.change(input, { target: { value: "   " } });

    await act(async () => {
      fireEvent.keyDown(input, { key: "Enter" });
    });

    expect(mockScanProject).not.toHaveBeenCalled();
  });

  it("trims whitespace from path before scanning", async () => {
    mockScanProject.mockResolvedValueOnce(makeScanResult());

    const { container } = render(<OnboardSection onProjectOnboarded={onProjectOnboarded} />);
    const input = container.querySelector("input")!;
    fireEvent.change(input, { target: { value: "  /home/user/project  " } });

    await act(async () => {
      fireEvent.click(findButton(container, /^Scan$/)!);
    });

    await waitFor(() => {
      expect(mockScanProject).toHaveBeenCalledWith("/home/user/project");
    });
  });
});

// ── Helpers ──

function findButton(container: HTMLElement, text: RegExp): HTMLButtonElement | null {
  const buttons = Array.from(container.querySelectorAll("button"));
  return buttons.find((b) => text.test(b.textContent ?? "")) ?? null;
}
