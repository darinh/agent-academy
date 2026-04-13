// @vitest-environment jsdom
import { describe, expect, it, vi, beforeEach } from "vitest";
import { render, fireEvent, waitFor } from "@testing-library/react";
import type { GitHubStatus } from "../api";

vi.mock("../api", () => ({
  getGitHubStatus: vi.fn(),
}));

vi.mock("@fluentui/react-components", () => ({
  Button: ({ children, onClick, disabled, ...rest }: any) => (
    <button onClick={onClick} disabled={disabled} {...rest}>{children}</button>
  ),
  Spinner: () => <span>Loading...</span>,
  makeStyles: () => () => ({}),
  shorthands: new Proxy({}, { get: () => () => ({}) }),
}));

vi.mock("@fluentui/react-icons", () => ({
  CheckmarkCircleRegular: () => <span>✓</span>,
  ErrorCircleRegular: () => <span>✗</span>,
  WarningRegular: () => <span>⚠</span>,
  ArrowSyncRegular: () => <span>↻</span>,
  OpenRegular: () => <span>↗</span>,
}));

vi.mock("../settings/settingsStyles", () => ({
  useSettingsStyles: () => ({}),
}));

import { getGitHubStatus } from "../api";
import GitHubTab from "../settings/GitHubTab";

const mockGetGitHubStatus = vi.mocked(getGitHubStatus);

function makeGhStatus(overrides: Partial<GitHubStatus> = {}): GitHubStatus {
  return {
    isConfigured: true,
    repository: "darinh/agent-academy",
    authSource: "oauth",
    ...overrides,
  };
}

function findButton(container: HTMLElement, text: RegExp): HTMLButtonElement {
  const buttons = Array.from(container.querySelectorAll("button"));
  const match = buttons.find((b) => text.test(b.textContent ?? ""));
  if (!match) throw new Error("Button not found: " + text);
  return match as HTMLButtonElement;
}

beforeEach(() => {
  vi.clearAllMocks();
});

describe("GitHubTab", () => {
  describe("loading state", () => {
    it("shows loading spinner initially", () => {
      mockGetGitHubStatus.mockReturnValue(new Promise(() => {}));
      const { container } = render(<GitHubTab />);
      expect(container.textContent).toContain("Checking GitHub status");
    });
  });

  describe("error state", () => {
    it("shows error message on fetch failure", async () => {
      mockGetGitHubStatus.mockRejectedValue(new Error("Connection refused"));
      const { container } = render(<GitHubTab />);

      await waitFor(() => {
        expect(container.textContent).toContain("Connection Error");
      });
      expect(container.textContent).toContain("Connection refused");
    });

    it("shows retry button on error", async () => {
      mockGetGitHubStatus.mockRejectedValue(new Error("fail"));
      const { container } = render(<GitHubTab />);

      await waitFor(() => {
        expect(findButton(container, /retry/i)).toBeDefined();
      });
    });

    it("retries fetch when Retry clicked", async () => {
      mockGetGitHubStatus
        .mockRejectedValueOnce(new Error("fail"))
        .mockResolvedValueOnce(makeGhStatus());

      const { container } = render(<GitHubTab />);

      await waitFor(() => {
        expect(findButton(container, /retry/i)).toBeDefined();
      });

      fireEvent.click(findButton(container, /retry/i));

      await waitFor(() => {
        expect(container.textContent).toContain("Connected");
      });
      expect(mockGetGitHubStatus).toHaveBeenCalledTimes(2);
    });
  });

  describe("connected state (oauth)", () => {
    it("shows Connected status and OAuth explanation", async () => {
      mockGetGitHubStatus.mockResolvedValue(makeGhStatus({ authSource: "oauth" }));
      const { container } = render(<GitHubTab />);

      await waitFor(() => {
        expect(container.textContent).toContain("Connected");
      });
      expect(container.textContent).toContain("Authenticated via browser OAuth");
      expect(container.textContent).toContain("Create PRs");
      expect(container.textContent).toContain("Status sync");
    });

    it("shows repository name", async () => {
      mockGetGitHubStatus.mockResolvedValue(makeGhStatus({ repository: "org/repo" }));
      const { container } = render(<GitHubTab />);

      await waitFor(() => {
        expect(container.textContent).toContain("org/repo");
      });
    });

    it("shows refresh button", async () => {
      mockGetGitHubStatus.mockResolvedValue(makeGhStatus());
      const { container } = render(<GitHubTab />);

      await waitFor(() => {
        expect(container.querySelector("[aria-label='Refresh GitHub status']")).toBeTruthy();
      });
    });
  });

  describe("connected state (cli)", () => {
    it("shows CLI auth explanation", async () => {
      mockGetGitHubStatus.mockResolvedValue(makeGhStatus({ authSource: "cli" }));
      const { container } = render(<GitHubTab />);

      await waitFor(() => {
        expect(container.textContent).toContain("Authenticated via server-side");
      });
    });
  });

  describe("not connected state", () => {
    it("shows Not Connected with login button", async () => {
      mockGetGitHubStatus.mockResolvedValue(makeGhStatus({ isConfigured: false, authSource: "none" }));
      const { container } = render(<GitHubTab />);

      await waitFor(() => {
        expect(container.textContent).toContain("Not Connected");
      });
      expect(container.textContent).toContain("GitHub is not configured");
      expect(findButton(container, /login with github/i)).toBeDefined();
    });
  });
});
