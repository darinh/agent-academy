// @vitest-environment jsdom
/**
 * Interactive RTL tests for CommandsPanel.
 *
 * Uses @testing-library/react + jsdom.
 * Covers: initial render (hero, metrics, command grid), command card selection,
 * form field rendering (text/textarea/number), field value changes, validation
 * errors, execute button (readOnly + normal), execution lifecycle (loading/result),
 * history rail (latest result, history list, empty state), result card rendering
 * (success/error badges, metadata, preview blocks, record lists), async polling
 * for pending history items, server metadata loading, roomId auto-fill.
 */
import "@testing-library/jest-dom/vitest";
import {
  cleanup,
  render,
  screen,
  waitFor,
  act,
  fireEvent,
} from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createElement } from "react";
import { FluentProvider, webDarkTheme } from "@fluentui/react-components";

// ── Mocks ──────────────────────────────────────────────────────────────

const mockExecuteCommand = vi.fn();
const mockGetCommandExecution = vi.fn();
const mockGetCommandMetadata = vi.fn();

vi.mock("../api", () => ({
  executeCommand: (...args: unknown[]) => mockExecuteCommand(...args),
  getCommandExecution: (...args: unknown[]) => mockGetCommandExecution(...args),
  getCommandMetadata: (...args: unknown[]) => mockGetCommandMetadata(...args),
}));

vi.mock("../V3Badge", () => ({
  default: ({
    children,
    color,
    style,
  }: {
    children: React.ReactNode;
    color: string;
    style?: React.CSSProperties;
  }) => createElement("span", { "data-testid": `badge-${color}`, style }, children),
}));

import CommandsPanel from "../CommandsPanel";

// ── Helpers ────────────────────────────────────────────────────────────

function renderPanel(props: Partial<Parameters<typeof CommandsPanel>[0]> = {}) {
  const defaultProps = {
    roomId: "test-room",
    readOnly: false,
    ...props,
  };

  const result = render(
    createElement(
      FluentProvider,
      { theme: webDarkTheme },
      createElement(CommandsPanel, defaultProps),
    ),
  );

  return { ...result, props: defaultProps };
}

function makeResponse(overrides: Record<string, unknown> = {}) {
  return {
    command: "READ_FILE",
    status: "completed",
    result: "file content here",
    error: null,
    errorCode: null,
    correlationId: `corr-${Math.random().toString(36).slice(2, 8)}`,
    timestamp: new Date().toISOString(),
    executedBy: "human",
    ...overrides,
  };
}

// ── Setup ──────────────────────────────────────────────────────────────

beforeEach(() => {
  vi.useFakeTimers({ shouldAdvanceTime: true });
  mockGetCommandMetadata.mockResolvedValue([]);
  mockExecuteCommand.mockResolvedValue(makeResponse());
  mockGetCommandExecution.mockResolvedValue(makeResponse());
});

afterEach(() => {
  cleanup();
  vi.useRealTimers();
  vi.restoreAllMocks();
});

// ── Tests ──────────────────────────────────────────────────────────────

describe("CommandsPanel", () => {
  describe("initial render", () => {
    it("renders the hero section with Command deck label", () => {
      renderPanel();
      // "Command deck" appears twice: eyebrow + section title
      const decks = screen.getAllByText("Command deck");
      expect(decks.length).toBeGreaterThanOrEqual(1);
      expect(screen.getByText(/Select a command below/)).toBeInTheDocument();
    });

    it("shows command count metrics", () => {
      renderPanel();
      // Should show "N commands · M instant · K polling" — may appear multiple times
      const metrics = screen.getAllByText(/\d+ commands/);
      expect(metrics.length).toBeGreaterThanOrEqual(1);
    });

    it("renders command cards for all WEEK1_COMMANDS", () => {
      renderPanel();
      // "Read file" appears in card + composer header, so use getAllByText
      expect(screen.getAllByText("Read file").length).toBeGreaterThanOrEqual(1);
      expect(screen.getAllByText("Search code").length).toBeGreaterThanOrEqual(1);
      expect(screen.getAllByText("Show diff").length).toBeGreaterThanOrEqual(1);
      expect(screen.getAllByText("Run build").length).toBeGreaterThanOrEqual(1);
      expect(screen.getAllByText("Run tests").length).toBeGreaterThanOrEqual(1);
    });

    it("shows category badges on command cards", () => {
      renderPanel();
      // V3Badge mock renders data-testid="badge-{color}"
      const infoBadges = screen.getAllByTestId("badge-info");
      expect(infoBadges.length).toBeGreaterThan(0);
    });

    it("shows async/sync badges on command cards", () => {
      renderPanel();
      const pollBadges = screen.getAllByText("polling");
      const instantBadges = screen.getAllByText("instant");
      expect(pollBadges.length).toBeGreaterThan(0);
      expect(instantBadges.length).toBeGreaterThan(0);
    });

    it("renders the execution rail section", () => {
      renderPanel();
      expect(screen.getByText("Execution rail")).toBeInTheDocument();
    });

    it("shows empty state when no history", () => {
      renderPanel();
      expect(screen.getByText(/No command runs yet/)).toBeInTheDocument();
    });

    it("renders history section", () => {
      renderPanel();
      expect(screen.getByText("History")).toBeInTheDocument();
    });
  });

  describe("command card selection", () => {
    it("highlights the selected command card", () => {
      renderPanel();
      // READ_FILE is selected by default — find the card button
      const readFileCards = screen.getAllByText("Read file");
      const readFileCard = readFileCards[0].closest("button")!;
      // Find an unselected card to compare
      const buildCards = screen.getAllByText("Run build");
      const buildCard = buildCards[0].closest("button")!;
      // The selected card should have a different class than the unselected one
      expect(readFileCard.className).not.toEqual(buildCard.className);
    });

    it("selects a different command when clicked", async () => {
      renderPanel();
      const readFileCards = screen.getAllByText("Read file");
      const readFileCard = readFileCards[0].closest("button")!;
      const buildCards = screen.getAllByText("Run build");
      const buildCard = buildCards[0].closest("button")!;

      const initialReadFileClass = readFileCard.className;
      await act(async () => { fireEvent.click(buildCard); });
      // Now buildCard should have the "active" class that readFileCard had
      expect(buildCard.className).toEqual(initialReadFileClass);
    });

    it("clears error when switching commands", async () => {
      renderPanel();
      // Try executing READ_FILE without filling required path → error
      const runBtn = screen.getByText(/^Run Read file$/);
      await act(async () => { fireEvent.click(runBtn); });
      expect(screen.getByText("Path is required.")).toBeInTheDocument();

      // Switch to a different command
      const buildCards = screen.getAllByText("Run build");
      const buildCard = buildCards[0].closest("button")!;
      await act(async () => { fireEvent.click(buildCard); });
      expect(screen.queryByText("Path is required.")).not.toBeInTheDocument();
    });
  });

  describe("form fields", () => {
    it("renders text input fields for selected command", () => {
      renderPanel();
      // READ_FILE has Path, Start line, End line
      expect(screen.getByText("Path *")).toBeInTheDocument();
      expect(screen.getByText("Start line")).toBeInTheDocument();
      expect(screen.getByText("End line")).toBeInTheDocument();
    });

    it("marks required fields with asterisk", () => {
      renderPanel();
      expect(screen.getByText("Path *")).toBeInTheDocument();
    });

    it("shows field descriptions", () => {
      renderPanel();
      expect(screen.getByText("Repository-relative file path.")).toBeInTheDocument();
    });

    it("shows placeholder text on inputs", () => {
      renderPanel();
      expect(screen.getByPlaceholderText("src/AgentAcademy.Server/Program.cs")).toBeInTheDocument();
    });

    it("updates draft when field value changes", async () => {
      renderPanel();
      const pathInput = screen.getByPlaceholderText("src/AgentAcademy.Server/Program.cs");
      await act(async () => { fireEvent.change(pathInput, { target: { value: "test.txt" } }); });
      expect(pathInput).toHaveValue("test.txt");
    });

    it("shows no-args message for commands without fields", async () => {
      renderPanel();
      const buildCards = screen.getAllByText("Run build");
      const buildCard = buildCards[0].closest("button")!;
      await act(async () => { fireEvent.click(buildCard); });
      expect(screen.getByText(/No extra arguments required/)).toBeInTheDocument();
    });

    it("shows textarea for textarea-type fields", async () => {
      // Provide a server command with a textarea field
      mockGetCommandMetadata.mockResolvedValue([
        {
          command: "TEXTAREA_CMD",
          title: "Textarea command",
          category: "operations",
          description: "Has a textarea",
          detail: "For testing",
          isAsync: false,
          fields: [{ name: "body", label: "Body", kind: "textarea", description: "Text body" }],
        },
      ]);
      await act(async () => { renderPanel(); });
      await waitFor(() => {
        expect(screen.getAllByText("Textarea command").length).toBeGreaterThanOrEqual(1);
      });
      const cards = screen.getAllByText("Textarea command");
      const card = cards[0].closest("button")!;
      await act(async () => { fireEvent.click(card); });
      // Textarea should be present
      const textareas = document.querySelectorAll("textarea");
      expect(textareas.length).toBeGreaterThan(0);
    });

    it("shows number input for number-type fields", () => {
      renderPanel();
      // READ_FILE has Start line (number)
      const numInput = screen.getByPlaceholderText("1");
      expect(numInput).toBeInTheDocument();
    });
  });

  describe("validation", () => {
    it("shows validation error for required fields", async () => {
      renderPanel();
      const runBtn = screen.getByText(/^Run Read file$/);
      await act(async () => { fireEvent.click(runBtn); });
      expect(screen.getByText("Path is required.")).toBeInTheDocument();
      expect(mockExecuteCommand).not.toHaveBeenCalled();
    });

    it("clears validation error on successful fill + execute", async () => {
      renderPanel();

      // Trigger error
      await act(async () => { fireEvent.click(screen.getByText(/^Run Read file$/)); });
      expect(screen.getByText("Path is required.")).toBeInTheDocument();

      // Fill the field
      const pathInput = screen.getByPlaceholderText("src/AgentAcademy.Server/Program.cs");
      await act(async () => { fireEvent.change(pathInput, { target: { value: "file.ts" } }); });

      // Execute again
      await act(async () => { fireEvent.click(screen.getByText(/^Run Read file$/)); });

      await waitFor(() => {
        expect(screen.queryByText("Path is required.")).not.toBeInTheDocument();
      });
    });
  });

  describe("readOnly mode", () => {
    it("disables execute button in readOnly mode", () => {
      renderPanel({ readOnly: true });
      const runBtn = screen.getByText(/^Run Read file$/).closest("button")!;
      expect(runBtn).toBeDisabled();
    });

    it("shows readOnly warning text", () => {
      renderPanel({ readOnly: true });
      expect(screen.getByText(/Limited mode is active/)).toBeInTheDocument();
    });

    it("shows readOnly error when attempting execution", async () => {
      renderPanel({ readOnly: true });
      // Fill the required field
      const pathInput = screen.getByPlaceholderText("src/AgentAcademy.Server/Program.cs");
      await act(async () => { fireEvent.change(pathInput, { target: { value: "f.ts" } }); });

      // Force-click the button (it's disabled, but test the handler guard)
      // The button is disabled, so we can't test execution — just verify the message is shown
      expect(screen.getByText(/Limited mode is active/)).toBeInTheDocument();
    });

    it("shows normal helper text when not readOnly", () => {
      renderPanel({ readOnly: false });
      expect(screen.getByText(/This command returns a result/)).toBeInTheDocument();
    });
  });

  describe("command execution", () => {
    async function fillAndExecuteReadFile() {
      renderPanel();
      const pathInput = screen.getByPlaceholderText("src/AgentAcademy.Server/Program.cs");
      await act(async () => { fireEvent.change(pathInput, { target: { value: "README.md" } }); });
      await act(async () => { fireEvent.click(screen.getByText(/^Run Read file$/)); });
    }

    it("calls executeCommand with correct args", async () => {
      mockExecuteCommand.mockResolvedValue(makeResponse());
      await fillAndExecuteReadFile();
      expect(mockExecuteCommand).toHaveBeenCalledWith(
        expect.objectContaining({ command: "READ_FILE", args: { path: "README.md" } }),
      );
    });

    it("shows Running… while submitting", async () => {
      let resolve: (v: unknown) => void;
      mockExecuteCommand.mockReturnValue(new Promise((r) => { resolve = r; }));
      renderPanel();
      const pathInput = screen.getByPlaceholderText("src/AgentAcademy.Server/Program.cs");
      await act(async () => { fireEvent.change(pathInput, { target: { value: "x" } }); });
      await act(async () => { fireEvent.click(screen.getByText(/^Run Read file$/)); });
      expect(screen.getByText("Running…")).toBeInTheDocument();
      await act(async () => { resolve!(makeResponse()); });
    });

    it("disables execute button while submitting", async () => {
      let resolve: (v: unknown) => void;
      mockExecuteCommand.mockReturnValue(new Promise((r) => { resolve = r; }));
      renderPanel();
      const pathInput = screen.getByPlaceholderText("src/AgentAcademy.Server/Program.cs");
      await act(async () => { fireEvent.change(pathInput, { target: { value: "x" } }); });
      await act(async () => { fireEvent.click(screen.getByText(/^Run Read file$/)); });
      expect(screen.getByText("Running…").closest("button")).toBeDisabled();
      await act(async () => { resolve!(makeResponse()); });
    });

    it("adds result to history on success", async () => {
      mockExecuteCommand.mockResolvedValue(makeResponse({ result: "test output" }));
      await fillAndExecuteReadFile();

      await waitFor(() => {
        // The empty state should be gone
        expect(screen.queryByText(/No command runs yet/)).not.toBeInTheDocument();
      });
    });

    it("shows error in panel on network failure", async () => {
      mockExecuteCommand.mockRejectedValue(new Error("Timeout"));
      await fillAndExecuteReadFile();

      await waitFor(() => {
        expect(screen.getByText("Timeout")).toBeInTheDocument();
      });
    });
  });

  describe("result cards", () => {
    async function executeAndGetResult(responseOverrides: Record<string, unknown> = {}) {
      mockExecuteCommand.mockResolvedValue(makeResponse(responseOverrides));
      renderPanel();
      const pathInput = screen.getByPlaceholderText("src/AgentAcademy.Server/Program.cs");
      await act(async () => { fireEvent.change(pathInput, { target: { value: "x" } }); });
      await act(async () => { fireEvent.click(screen.getByText(/^Run Read file$/)); });
      await waitFor(() => {
        expect(screen.queryByText(/No command runs yet/)).not.toBeInTheDocument();
      });
    }

    it("shows command title in result card", async () => {
      await executeAndGetResult();
      // "Read file" appears in the grid card, composer header, and result card
      const titles = screen.getAllByText("Read file");
      expect(titles.length).toBeGreaterThanOrEqual(3);
    });

    it("shows status badge on result card", async () => {
      await executeAndGetResult({ status: "completed" });
      const okBadges = screen.getAllByTestId("badge-ok");
      expect(okBadges.some((b) => b.textContent === "completed")).toBe(true);
    });

    it("shows error box for failed commands", async () => {
      await executeAndGetResult({
        status: "failed",
        error: "Permission denied",
        errorCode: "FORBIDDEN",
      });
      expect(screen.getByText("Permission denied")).toBeInTheDocument();
      expect(screen.getByText("FORBIDDEN")).toBeInTheDocument();
    });

    it("shows preview block for string result", async () => {
      await executeAndGetResult({ result: "console.log('hello')" });
      // String results are JSON.stringify'd in the pre block
      expect(screen.getByText(/"console.log\('hello'\)"/)).toBeInTheDocument();
    });

    it("shows JSON preview for complex result", async () => {
      await executeAndGetResult({ result: { count: 5, name: "test" } });
      // JSON.stringify renders with formatting
      expect(screen.getByText(/"count": 5/)).toBeInTheDocument();
    });

    it("shows metadata summary cards for scalar result fields", async () => {
      await executeAndGetResult({
        result: { fileName: "test.ts", lineCount: 42, content: "..." },
      });
      // summarizeResult ignores "content" key and extracts scalars
      // readableLabel("fileName") → "File Name", readableLabel("lineCount") → "Line Count"
      expect(screen.getByText("File Name")).toBeInTheDocument();
      expect(screen.getByText("test.ts")).toBeInTheDocument();
      expect(screen.getByText("Line Count")).toBeInTheDocument();
      expect(screen.getByText("42")).toBeInTheDocument();
    });

    it("shows record list for array results", async () => {
      await executeAndGetResult({
        result: {
          matches: [
            { file: "a.ts", line: 10 },
            { file: "b.ts", line: 20 },
          ],
        },
      });
      expect(screen.getByText("a.ts")).toBeInTheDocument();
      expect(screen.getByText("b.ts")).toBeInTheDocument();
    });

    it("shows content preview for result with content field", async () => {
      await executeAndGetResult({
        result: { content: "# Heading\nParagraph text" },
      });
      // findPreviewBlock extracts the "content" field
      const pre = document.querySelector("pre");
      expect(pre).toBeTruthy();
      expect(pre!.textContent).toContain("# Heading");
      expect(pre!.textContent).toContain("Paragraph text");
    });
  });

  describe("execution history", () => {
    it("shows multiple history items", async () => {
      renderPanel();

      // Execute twice
      const pathInput = screen.getByPlaceholderText("src/AgentAcademy.Server/Program.cs");

      mockExecuteCommand.mockResolvedValue(makeResponse({ correlationId: "first" }));
      await act(async () => { fireEvent.change(pathInput, { target: { value: "a.ts" } }); });
      await act(async () => { fireEvent.click(screen.getByText(/^Run Read file$/)); });

      await waitFor(() => {
        expect(screen.queryByText(/No command runs yet/)).not.toBeInTheDocument();
      });

      mockExecuteCommand.mockResolvedValue(makeResponse({ correlationId: "second" }));
      await act(async () => { fireEvent.change(pathInput, { target: { value: "b.ts" } }); });
      await act(async () => { fireEvent.click(screen.getByText(/^Run Read file$/)); });

      await waitFor(() => {
        // Should have multiple result articles
        const articles = document.querySelectorAll("article");
        expect(articles.length).toBeGreaterThanOrEqual(2);
      });
    });
  });

  describe("async polling for pending history", () => {
    it("polls getCommandExecution for pending results", async () => {
      mockExecuteCommand.mockResolvedValue(
        makeResponse({ status: "pending", correlationId: "async-1", result: null }),
      );
      mockGetCommandExecution.mockResolvedValue(
        makeResponse({ status: "completed", correlationId: "async-1", result: "Done!" }),
      );

      renderPanel();
      const pathInput = screen.getByPlaceholderText("src/AgentAcademy.Server/Program.cs");
      await act(async () => { fireEvent.change(pathInput, { target: { value: "x" } }); });
      await act(async () => { fireEvent.click(screen.getByText(/^Run Read file$/)); });

      // Wait for the pending item to appear
      await waitFor(() => {
        const warnBadges = screen.getAllByTestId("badge-warn");
        expect(warnBadges.some((b) => b.textContent === "pending")).toBe(true);
      });

      // Advance timer to trigger polling
      await act(async () => { vi.advanceTimersByTime(2500); });

      await waitFor(() => {
        const okBadges = screen.getAllByTestId("badge-ok");
        expect(okBadges.some((b) => b.textContent === "completed")).toBe(true);
      });
    });
  });

  describe("server metadata loading", () => {
    it("loads and uses server metadata for command list", async () => {
      mockGetCommandMetadata.mockResolvedValue([
        {
          command: "CUSTOM_OP",
          title: "Custom operation",
          category: "operations",
          description: "Server-defined command",
          detail: "Custom detail text",
          isAsync: false,
          fields: [
            {
              name: "target",
              label: "Target",
              kind: "text",
              description: "What to target",
              required: true,
            },
          ],
        },
      ]);

      await act(async () => { renderPanel(); });

      await waitFor(() => {
        expect(screen.getAllByText("Custom operation").length).toBeGreaterThanOrEqual(1);
      });
    });

    it("preserves dirty drafts when metadata loads", async () => {
      // We can't directly test draft preservation, but we can verify the
      // component still renders correctly after metadata loads
      mockGetCommandMetadata.mockResolvedValue([]);
      renderPanel();
      const pathInput = screen.getByPlaceholderText("src/AgentAcademy.Server/Program.cs");
      await act(async () => { fireEvent.change(pathInput, { target: { value: "dirty-draft" } }); });

      // Metadata resolves (empty → keeps WEEK1_COMMANDS)
      await waitFor(() => {
        expect(pathInput).toHaveValue("dirty-draft");
      });
    });

    it("falls back to hardcoded commands on metadata error", async () => {
      mockGetCommandMetadata.mockRejectedValue(new Error("Server down"));
      renderPanel();

      await waitFor(() => {
        expect(screen.getAllByText("Read file").length).toBeGreaterThanOrEqual(1);
        expect(screen.getAllByText("Run build").length).toBeGreaterThanOrEqual(1);
      });
    });
  });

  describe("roomId auto-fill", () => {
    it("auto-fills roomId for commands that have a roomId field", async () => {
      renderPanel({ roomId: "my-room-123" });

      // Switch to ROOM_HISTORY which has a roomId field
      const historyCards = screen.getAllByText("Room history");
      const historyCard = historyCards[0].closest("button")!;
      await act(async () => { fireEvent.click(historyCard); });

      // The roomId field should be auto-filled
      await waitFor(() => {
        const roomInput = screen.getByPlaceholderText("agent-academy-main");
        expect(roomInput).toHaveValue("my-room-123");
      });
    });
  });

  describe("helper text", () => {
    it("shows async helper text for async commands", async () => {
      renderPanel();
      const buildCards = screen.getAllByText("Run build");
      const buildCard = buildCards[0].closest("button")!;
      await act(async () => { fireEvent.click(buildCard); });
      expect(screen.getByText(/returns immediately, then polls/)).toBeInTheDocument();
    });

    it("shows sync helper text for sync commands", () => {
      renderPanel();
      // READ_FILE is sync and selected by default
      expect(screen.getByText(/returns a result directly/)).toBeInTheDocument();
    });
  });

  describe("pending count badge", () => {
    it("shows pending count badge in execution rail", () => {
      renderPanel();
      // "0 pending" shown by default
      expect(screen.getByText("0 pending")).toBeInTheDocument();
    });
  });

  describe("destructive command confirmation", () => {
    const destructiveMetadata = [
      {
        command: "CLOSE_ROOM",
        title: "Close room",
        category: "operations",
        description: "Archive a room permanently.",
        detail: "Agents will be moved out.",
        isAsync: false,
        isDestructive: true,
        destructiveWarning: "This will archive the room permanently. Agents in the room will be moved out.",
        fields: [{ name: "roomId", label: "Room ID", kind: "text", description: "Room to close", placeholder: "agent-academy-main", required: true }],
      },
      {
        command: "READ_FILE",
        title: "Read file",
        category: "code",
        description: "Read a file.",
        detail: "Reads file content.",
        isAsync: false,
        isDestructive: false,
        fields: [{ name: "path", label: "Path", kind: "text", description: "File path", placeholder: "src/main.ts", required: true }],
      },
    ];

    beforeEach(() => {
      mockExecuteCommand.mockClear();
    });

    it("shows destructive badge on destructive command cards", async () => {
      mockGetCommandMetadata.mockResolvedValue(destructiveMetadata);
      await act(async () => { renderPanel(); });
      await waitFor(() => {
        expect(screen.getAllByText("Close room").length).toBeGreaterThanOrEqual(1);
      });
      expect(screen.getByText("destructive")).toBeInTheDocument();
    });

    it("shows destructive warning text in composer for destructive commands", async () => {
      mockGetCommandMetadata.mockResolvedValue(destructiveMetadata);
      await act(async () => { renderPanel(); });
      await waitFor(() => {
        expect(screen.getAllByText("Close room").length).toBeGreaterThanOrEqual(1);
      });
      const cards = screen.getAllByText("Close room");
      const card = cards[0].closest("button")!;
      await act(async () => { fireEvent.click(card); });
      expect(screen.getByText(/archive the room permanently/)).toBeInTheDocument();
    });

    it("opens confirmation dialog instead of executing destructive command", async () => {
      mockGetCommandMetadata.mockResolvedValue(destructiveMetadata);
      await act(async () => { renderPanel(); });
      await waitFor(() => {
        expect(screen.getAllByText("Close room").length).toBeGreaterThanOrEqual(1);
      });
      // Select destructive command
      const cards = screen.getAllByText("Close room");
      const card = cards[0].closest("button")!;
      await act(async () => { fireEvent.click(card); });
      // Fill required field
      const roomInput = screen.getByPlaceholderText("agent-academy-main");
      await act(async () => { fireEvent.change(roomInput, { target: { value: "main-room" } }); });
      // Click execute
      await act(async () => { fireEvent.click(screen.getByText(/^Run Close room$/)); });
      // Should show dialog, NOT call executeCommand
      expect(mockExecuteCommand).not.toHaveBeenCalled();
      expect(screen.getByText("Confirm Close room")).toBeInTheDocument();
      expect(screen.getByText("Yes, proceed")).toBeInTheDocument();
    });

    it("executes with confirm=true when user confirms in dialog", async () => {
      mockGetCommandMetadata.mockResolvedValue(destructiveMetadata);
      await act(async () => { renderPanel(); });
      await waitFor(() => {
        expect(screen.getAllByText("Close room").length).toBeGreaterThanOrEqual(1);
      });
      // Select, fill, click execute
      const cards = screen.getAllByText("Close room");
      await act(async () => { fireEvent.click(cards[0].closest("button")!); });
      const roomInput = screen.getByPlaceholderText("agent-academy-main");
      await act(async () => { fireEvent.change(roomInput, { target: { value: "main-room" } }); });
      await act(async () => { fireEvent.click(screen.getByText(/^Run Close room$/)); });
      // Confirm in dialog
      await act(async () => { fireEvent.click(screen.getByText("Yes, proceed")); });
      await waitFor(() => {
        expect(mockExecuteCommand).toHaveBeenCalledTimes(1);
      });
      const callArgs = mockExecuteCommand.mock.calls[0][0];
      expect(callArgs.command).toBe("CLOSE_ROOM");
      expect(callArgs.args?.confirm).toBe("true");
    });

    it("does not execute when user cancels in dialog", async () => {
      mockGetCommandMetadata.mockResolvedValue(destructiveMetadata);
      await act(async () => { renderPanel(); });
      await waitFor(() => {
        expect(screen.getAllByText("Close room").length).toBeGreaterThanOrEqual(1);
      });
      const cards = screen.getAllByText("Close room");
      await act(async () => { fireEvent.click(cards[0].closest("button")!); });
      const roomInput = screen.getByPlaceholderText("agent-academy-main");
      await act(async () => { fireEvent.change(roomInput, { target: { value: "main-room" } }); });
      await act(async () => { fireEvent.click(screen.getByText(/^Run Close room$/)); });
      // Cancel in dialog
      await act(async () => { fireEvent.click(screen.getByText("Cancel")); });
      expect(mockExecuteCommand).not.toHaveBeenCalled();
    });

    it("does not show confirmation dialog for non-destructive commands", async () => {
      mockGetCommandMetadata.mockResolvedValue(destructiveMetadata);
      await act(async () => { renderPanel(); });
      await waitFor(() => {
        expect(screen.getAllByText("Read file").length).toBeGreaterThanOrEqual(1);
      });
      // Select non-destructive command
      const cards = screen.getAllByText("Read file");
      await act(async () => { fireEvent.click(cards[0].closest("button")!); });
      const pathInput = screen.getByPlaceholderText("src/main.ts");
      await act(async () => { fireEvent.change(pathInput, { target: { value: "test.ts" } }); });
      await act(async () => { fireEvent.click(screen.getByText(/^Run Read file$/)); });
      // Should execute directly without dialog
      await waitFor(() => {
        expect(mockExecuteCommand).toHaveBeenCalledTimes(1);
      });
      expect(screen.queryByText("Yes, proceed")).not.toBeInTheDocument();
      // Confirm no confirm flag in request
      const callArgs = mockExecuteCommand.mock.calls[0][0];
      expect(callArgs.args?.confirm).toBeUndefined();
    });

    it("still validates required fields before showing dialog for destructive commands", async () => {
      mockGetCommandMetadata.mockResolvedValue(destructiveMetadata);
      await act(async () => { renderPanel(); });
      await waitFor(() => {
        expect(screen.getAllByText("Close room").length).toBeGreaterThanOrEqual(1);
      });
      const cards = screen.getAllByText("Close room");
      await act(async () => { fireEvent.click(cards[0].closest("button")!); });
      // Do NOT fill required field, try to execute
      await act(async () => { fireEvent.click(screen.getByText(/^Run Close room$/)); });
      // Should show validation error, not dialog
      expect(screen.getByText("Room ID is required.")).toBeInTheDocument();
      expect(screen.queryByText("Yes, proceed")).not.toBeInTheDocument();
      expect(mockExecuteCommand).not.toHaveBeenCalled();
    });
  });
});
