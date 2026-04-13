// @vitest-environment jsdom
import { describe, expect, it, vi, beforeEach } from "vitest";
import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import type { AgentDefinition } from "../api";

vi.mock("../api", () => ({
  createCustomAgent: vi.fn(),
  deleteCustomAgent: vi.fn(),
}));

// Mock Fluent UI to avoid makeStyles/Griffel jsdom issues
vi.mock("@fluentui/react-components", () => ({
  Button: ({ children, onClick, disabled, icon, ...rest }: any) => (
    <button onClick={onClick} disabled={disabled} {...rest}>{children}</button>
  ),
  Spinner: () => <span>Loading...</span>,
  makeStyles: () => () => ({}),
  shorthands: new Proxy({}, { get: () => () => ({}) }),
}));

vi.mock("@fluentui/react-icons", () => ({
  BotRegular: () => <span>🤖</span>,
  AddRegular: () => <span>+</span>,
  DeleteRegular: () => <span>🗑</span>,
}));

vi.mock("../settings/settingsStyles", () => ({
  useSettingsStyles: () => ({}),
}));

import { createCustomAgent, deleteCustomAgent } from "../api";
import CustomAgentsTab from "../settings/CustomAgentsTab";

const mockCreateCustomAgent = vi.mocked(createCustomAgent);
const mockDeleteCustomAgent = vi.mocked(deleteCustomAgent);

function makeAgent(overrides: Partial<AgentDefinition> = {}): AgentDefinition {
  return {
    id: "test-agent",
    name: "Test Agent",
    role: "Custom",
    summary: "A test agent",
    startupPrompt: "You are a test agent",
    capabilityTags: [],
    enabledTools: [],
    autoJoinDefaultRoom: false,
    ...overrides,
  };
}

const noop = () => {};

beforeEach(() => {
  vi.clearAllMocks();
});

/** Helper to find buttons by text content */
function findButton(container: HTMLElement, text: RegExp): HTMLButtonElement {
  const buttons = Array.from(container.querySelectorAll("button"));
  const match = buttons.find((b) => text.test(b.textContent ?? ""));
  if (!match) throw new Error("Button not found: " + text);
  return match as HTMLButtonElement;
}

describe("CustomAgentsTab", () => {
  describe("empty state", () => {
    it("shows empty message when no agents", () => {
      render(<CustomAgentsTab customAgents={[]} onAgentsChanged={noop} />);
      expect(screen.getByText(/no custom agents yet/i)).toBeDefined();
    });

    it("shows Add Custom Agent button", () => {
      const { container } = render(<CustomAgentsTab customAgents={[]} onAgentsChanged={noop} />);
      expect(findButton(container, /add custom agent/i)).toBeDefined();
    });
  });

  describe("agent list", () => {
    it("renders agent cards", () => {
      const agents = [
        makeAgent({ id: "agent-1", name: "Purview Expert" }),
        makeAgent({ id: "agent-2", name: "SQL Helper" }),
      ];
      render(<CustomAgentsTab customAgents={agents} onAgentsChanged={noop} />);
      expect(screen.getByText("Purview Expert")).toBeDefined();
      expect(screen.getByText("SQL Helper")).toBeDefined();
    });

    it("shows agent IDs", () => {
      render(
        <CustomAgentsTab customAgents={[makeAgent({ id: "purview-expert" })]} onAgentsChanged={noop} />,
      );
      expect(screen.getByText("purview-expert")).toBeDefined();
    });

    it("shows delete button for each agent", () => {
      const agents = [makeAgent({ id: "agent-1", name: "Agent One" })];
      const { container } = render(<CustomAgentsTab customAgents={agents} onAgentsChanged={noop} />);
      const deleteBtn = container.querySelector("[aria-label='Delete Agent One']");
      expect(deleteBtn).toBeDefined();
    });
  });

  describe("delete agent", () => {
    it("calls deleteCustomAgent and onAgentsChanged", async () => {
      mockDeleteCustomAgent.mockResolvedValue(undefined as never);
      const onAgentsChanged = vi.fn();
      const agents = [makeAgent({ id: "agent-1", name: "Agent One" })];

      const { container } = render(<CustomAgentsTab customAgents={agents} onAgentsChanged={onAgentsChanged} />);
      const deleteBtn = container.querySelector("[aria-label='Delete Agent One']") as HTMLElement;
      fireEvent.click(deleteBtn);

      await waitFor(() => {
        expect(mockDeleteCustomAgent).toHaveBeenCalledWith("agent-1");
      });
      expect(onAgentsChanged).toHaveBeenCalled();
    });
  });

  describe("create agent form", () => {
    it("shows form when Add Custom Agent clicked", () => {
      const { container } = render(<CustomAgentsTab customAgents={[]} onAgentsChanged={noop} />);
      fireEvent.click(findButton(container, /add custom agent/i));
      expect(container.querySelector("input[placeholder*='Purview Expert']")).toBeTruthy();
      expect(container.querySelector("textarea")).toBeTruthy();
    });

    it("shows kebab-case ID preview as user types name", () => {
      const { container } = render(<CustomAgentsTab customAgents={[]} onAgentsChanged={noop} />);
      fireEvent.click(findButton(container, /add custom agent/i));
      const nameInput = container.querySelector("input[placeholder*='Purview Expert']") as HTMLInputElement;
      fireEvent.change(nameInput, { target: { value: "My Custom Agent" } });
      expect(container.textContent).toContain("ID: my-custom-agent");
    });

    it("disables Create button when name or prompt is empty", () => {
      const { container } = render(<CustomAgentsTab customAgents={[]} onAgentsChanged={noop} />);
      fireEvent.click(findButton(container, /add custom agent/i));
      const createBtn = findButton(container, /create agent/i);
      expect(createBtn.disabled).toBe(true);
    });

    it("creates agent and resets form on success", async () => {
      mockCreateCustomAgent.mockResolvedValue(undefined as never);
      const onAgentsChanged = vi.fn();

      const { container } = render(<CustomAgentsTab customAgents={[]} onAgentsChanged={onAgentsChanged} />);
      fireEvent.click(findButton(container, /add custom agent/i));

      const nameInput = container.querySelector("input[placeholder*='Purview Expert']") as HTMLInputElement;
      const promptInput = container.querySelector("textarea") as HTMLTextAreaElement;

      fireEvent.change(nameInput, { target: { value: "Graph Expert" } });
      fireEvent.change(promptInput, { target: { value: "You are a graph database specialist" } });

      fireEvent.click(findButton(container, /create agent/i));

      await waitFor(() => {
        expect(mockCreateCustomAgent).toHaveBeenCalledWith({
          name: "Graph Expert",
          prompt: "You are a graph database specialist",
          model: undefined,
        });
      });
      expect(onAgentsChanged).toHaveBeenCalled();
    });

    it("includes model when provided", async () => {
      mockCreateCustomAgent.mockResolvedValue(undefined as never);

      const { container } = render(<CustomAgentsTab customAgents={[]} onAgentsChanged={noop} />);
      fireEvent.click(findButton(container, /add custom agent/i));

      const nameInput = container.querySelector("input[placeholder*='Purview Expert']") as HTMLInputElement;
      const promptInput = container.querySelector("textarea") as HTMLTextAreaElement;
      const modelInput = container.querySelector("input[placeholder*='claude-sonnet']") as HTMLInputElement;

      fireEvent.change(nameInput, { target: { value: "Fast Agent" } });
      fireEvent.change(promptInput, { target: { value: "You are fast" } });
      fireEvent.change(modelInput, { target: { value: "claude-haiku-4.5" } });

      fireEvent.click(findButton(container, /create agent/i));

      await waitFor(() => {
        expect(mockCreateCustomAgent).toHaveBeenCalledWith({
          name: "Fast Agent",
          prompt: "You are fast",
          model: "claude-haiku-4.5",
        });
      });
    });

    it("shows error on creation failure", async () => {
      mockCreateCustomAgent.mockRejectedValue(new Error("Duplicate agent ID"));

      const { container } = render(<CustomAgentsTab customAgents={[]} onAgentsChanged={noop} />);
      fireEvent.click(findButton(container, /add custom agent/i));

      const nameInput = container.querySelector("input[placeholder*='Purview Expert']") as HTMLInputElement;
      const promptInput = container.querySelector("textarea") as HTMLTextAreaElement;

      fireEvent.change(nameInput, { target: { value: "Dupe Agent" } });
      fireEvent.change(promptInput, { target: { value: "You are a duplicate" } });

      fireEvent.click(findButton(container, /create agent/i));

      await waitFor(() => {
        expect(container.textContent).toContain("Duplicate agent ID");
      });
    });

    it("hides form on Cancel", () => {
      const { container } = render(<CustomAgentsTab customAgents={[]} onAgentsChanged={noop} />);
      fireEvent.click(findButton(container, /add custom agent/i));
      expect(container.querySelector("input[placeholder*='Purview Expert']")).toBeTruthy();

      fireEvent.click(findButton(container, /cancel/i));
      expect(container.querySelector("input[placeholder*='Purview Expert']")).toBeFalsy();
    });
  });
});
