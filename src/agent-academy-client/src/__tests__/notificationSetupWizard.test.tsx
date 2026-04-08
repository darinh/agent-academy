import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it, vi, beforeEach } from "vitest";

// Mock the API module before importing the component
vi.mock("../api", () => ({
  configureProvider: vi.fn(),
  connectProvider: vi.fn(),
  testNotification: vi.fn(),
  getProviderSchema: vi.fn(),
}));

// Mock CSS import
vi.mock("../NotificationSetupWizard.css", () => ({}));

import NotificationSetupWizard, {
  ProviderInstructions,
  DiscordInstructions,
  SlackInstructions,
  GenericInstructions,
  getStepTitle,
  getProviderDisplayName,
} from "../NotificationSetupWizard";
import { getProviderSchema } from "../api";

const mockGetProviderSchema = vi.mocked(getProviderSchema);

// ── Tests ──────────────────────────────────────────────────────────────

describe("NotificationSetupWizard", () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  describe("initial render", () => {
    it("shows a loading spinner while fetching schema", () => {
      mockGetProviderSchema.mockReturnValue(new Promise(() => {}));

      const markup = renderToStaticMarkup(
        <NotificationSetupWizard providerId="discord" inline />,
      );

      expect(markup).toContain("Loading setup wizard");
    });

    it("passes providerId to the component (schema fetched on mount)", () => {
      mockGetProviderSchema.mockReturnValue(new Promise(() => {}));

      // SSR doesn't trigger useEffect, but we verify the component accepts the prop
      // by confirming no crash and proper rendering
      const markup = renderToStaticMarkup(
        <NotificationSetupWizard providerId="slack" inline />,
      );

      expect(markup).toContain("Loading setup wizard");
    });
  });

  describe("Discord instructions (step 1)", () => {
    it("renders Discord-specific setup instructions", () => {
      const markup = renderToStaticMarkup(
        <DiscordInstructions appId="" onAppIdChange={() => {}} inviteUrl="" />,
      );

      expect(markup).toContain("Discord Developer Portal");
      expect(markup).toContain("New Application");
      expect(markup).toContain("Reset Token");
      expect(markup).toContain("Application ID");
    });

    it("shows invite link when appId and inviteUrl are provided", () => {
      const markup = renderToStaticMarkup(
        <DiscordInstructions
          appId="123456"
          onAppIdChange={() => {}}
          inviteUrl="https://discord.com/oauth2/authorize?client_id=123456&scope=bot&permissions=2147534848"
        />,
      );

      expect(markup).toContain("discord.com/oauth2/authorize");
      expect(markup).toContain("client_id=123456");
    });

    it("does not show invite link when inviteUrl is empty", () => {
      const markup = renderToStaticMarkup(
        <DiscordInstructions appId="" onAppIdChange={() => {}} inviteUrl="" />,
      );

      expect(markup).not.toContain("discord.com/oauth2/authorize");
    });
  });

  describe("Slack instructions (step 1)", () => {
    it("renders Slack-specific setup instructions", () => {
      const markup = renderToStaticMarkup(<SlackInstructions />);

      expect(markup).toContain("api.slack.com/apps");
      expect(markup).toContain("Create New App");
      expect(markup).toContain("Bot Token Scopes");
      expect(markup).toContain("chat:write");
      expect(markup).toContain("channels:manage");
      expect(markup).toContain("channels:read");
      expect(markup).toContain("channels:join");
      expect(markup).toContain("Install to Workspace");
      expect(markup).toContain("xoxb-");
    });

    it("includes the Default Channel ID instruction", () => {
      const markup = renderToStaticMarkup(<SlackInstructions />);

      expect(markup).toContain("Default Channel ID");
      expect(markup).toContain("View channel details");
    });
  });

  describe("generic instructions (unknown provider)", () => {
    it("renders generic fallback text", () => {
      const markup = renderToStaticMarkup(<GenericInstructions />);

      expect(markup).toContain("documentation");
      expect(markup).toContain("credentials");
    });
  });

  describe("step titles", () => {
    it("generates correct step titles for Discord", () => {
      expect(getStepTitle("discord", 1)).toBe("Set Up Discord");
      expect(getStepTitle("discord", 2)).toBe("Enter Credentials");
      expect(getStepTitle("discord", 3)).toBe("Connect & Test");
    });

    it("generates correct step titles for Slack", () => {
      expect(getStepTitle("slack", 1)).toBe("Set Up Slack");
      expect(getStepTitle("slack", 2)).toBe("Enter Credentials");
      expect(getStepTitle("slack", 3)).toBe("Connect & Test");
    });

    it("returns empty string for invalid step", () => {
      expect(getStepTitle("discord", 99)).toBe("");
    });
  });

  describe("getProviderDisplayName", () => {
    it("returns Discord for discord", () => {
      expect(getProviderDisplayName("discord")).toBe("Discord");
    });

    it("returns Slack for slack", () => {
      expect(getProviderDisplayName("slack")).toBe("Slack");
    });

    it("capitalizes unknown provider names", () => {
      expect(getProviderDisplayName("teams")).toBe("Teams");
      expect(getProviderDisplayName("webhook")).toBe("Webhook");
    });
  });

  describe("provider routing", () => {
    it("renders DiscordInstructions for discord provider", () => {
      const markup = renderToStaticMarkup(
        <ProviderInstructions providerId="discord" appId="" onAppIdChange={() => {}} inviteUrl="" />,
      );

      expect(markup).toContain("Discord Developer Portal");
    });

    it("renders SlackInstructions for slack provider", () => {
      const markup = renderToStaticMarkup(
        <ProviderInstructions providerId="slack" appId="" onAppIdChange={() => {}} inviteUrl="" />,
      );

      expect(markup).toContain("api.slack.com/apps");
    });

    it("renders GenericInstructions for unknown provider", () => {
      const markup = renderToStaticMarkup(
        <ProviderInstructions providerId="teams" appId="" onAppIdChange={() => {}} inviteUrl="" />,
      );

      expect(markup).toContain("documentation");
      expect(markup).not.toContain("Discord Developer Portal");
      expect(markup).not.toContain("api.slack.com");
    });
  });

  describe("overlay vs inline mode", () => {
    it("applies inline class when inline is true", () => {
      mockGetProviderSchema.mockReturnValue(new Promise(() => {}));
      const markup = renderToStaticMarkup(
        <NotificationSetupWizard providerId="discord" inline />,
      );

      expect(markup).toContain("wizard-panel--inline");
    });

    it("renders without inline class when inline is not set", () => {
      mockGetProviderSchema.mockReturnValue(new Promise(() => {}));
      const markup = renderToStaticMarkup(
        <NotificationSetupWizard providerId="discord" />,
      );

      expect(markup).toContain("wizard-panel");
      expect(markup).not.toContain("wizard-panel--inline");
    });
  });
});
