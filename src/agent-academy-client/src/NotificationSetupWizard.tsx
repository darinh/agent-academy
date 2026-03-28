import { useState, useCallback } from "react";
import {
  Button,
  Spinner,
} from "@fluentui/react-components";
import {
  CheckmarkCircle24Filled,
  DismissCircle24Filled,
} from "@fluentui/react-icons";
import {
  configureProvider,
  connectProvider,
  testNotification,
} from "./api";
import "./NotificationSetupWizard.css";

// ── Constants ──────────────────────────────────────────────────────────

const TOTAL_STEPS = 4;

/**
 * Discord bot permissions for Send Messages, Embed Links,
 * Read Message History, Use Slash Commands.
 * Uses BigInt to avoid signed-32-bit overflow with bit 31.
 */
const BOT_PERMISSIONS = (
  (1n << 11n) | // Send Messages
  (1n << 14n) | // Embed Links
  (1n << 16n) | // Read Message History
  (1n << 31n)   // Use Slash Commands (APPLICATION_COMMANDS)
).toString();

// ── Types ──────────────────────────────────────────────────────────────

interface Props {
  /** Called when the user closes or completes the wizard. */
  onClose?: () => void;
  /** When true, renders as inline content without the overlay backdrop. */
  inline?: boolean;
}

type ConnectionStatus = "idle" | "loading" | "success" | "error";

// ── Component ──────────────────────────────────────────────────────────

export default function NotificationSetupWizard({ onClose, inline }: Props) {
  const [step, setStep] = useState(1);

  // Step 1
  const [botToken, setBotToken] = useState("");

  // Step 2
  const [appId, setAppId] = useState("");
  const [guildId, setGuildId] = useState("");

  // Step 3
  const [channelId, setChannelId] = useState("");

  // Step 4
  const [configStatus, setConfigStatus] = useState<ConnectionStatus>("idle");
  const [connectStatus, setConnectStatus] = useState<ConnectionStatus>("idle");
  const [testStatus, setTestStatus] = useState<ConnectionStatus>("idle");
  const [error, setError] = useState<string | null>(null);

  // ── Navigation helpers ─────────────────────────────────────────────

  const next = useCallback(() => setStep((s) => Math.min(s + 1, TOTAL_STEPS)), []);
  const back = useCallback(() => setStep((s) => Math.max(s - 1, 1)), []);

  // ── Step 4 actions ─────────────────────────────────────────────────

  const handleConfigure = useCallback(async () => {
    setConfigStatus("loading");
    setError(null);
    try {
      await configureProvider("discord", {
        BotToken: botToken,
        GuildId: guildId,
        ChannelId: channelId,
      });
      setConfigStatus("success");
    } catch (err) {
      setConfigStatus("error");
      setError(err instanceof Error ? err.message : "Configuration failed");
    }
  }, [botToken, guildId, channelId]);

  const handleConnect = useCallback(async () => {
    setConnectStatus("loading");
    setError(null);
    try {
      await connectProvider("discord");
      setConnectStatus("success");
    } catch (err) {
      setConnectStatus("error");
      setError(err instanceof Error ? err.message : "Connection failed");
    }
  }, []);

  const handleTest = useCallback(async () => {
    setTestStatus("loading");
    setError(null);
    try {
      const res = await testNotification();
      if (res.sent > 0) {
        setTestStatus("success");
      } else {
        setTestStatus("error");
        setError("Test notification was not delivered. Check that the bot has access to the channel.");
      }
    } catch (err) {
      setTestStatus("error");
      setError(err instanceof Error ? err.message : "Test notification failed");
    }
  }, []);

  // ── Invite URL ─────────────────────────────────────────────────────

  const inviteUrl = appId.trim()
    ? `https://discord.com/oauth2/authorize?client_id=${encodeURIComponent(appId.trim())}&scope=bot&permissions=${BOT_PERMISSIONS}`
    : "";

  // ── Render ─────────────────────────────────────────────────────────

  const handleClose = onClose ?? (() => {});

  const panel = (
    <div className={inline ? "wizard-panel wizard-panel--inline" : "wizard-panel"} onClick={(e) => e.stopPropagation()}>
        {/* Step indicator */}
        <div className="wizard-steps">
          {Array.from({ length: TOTAL_STEPS }, (_, i) => (
            <div
              key={i}
              className="wizard-step-dot"
              data-active={i + 1 === step ? "true" : undefined}
              data-done={i + 1 < step ? "true" : undefined}
            />
          ))}
        </div>

        {/* Header */}
        <div className="wizard-header">
          <span className="step-label">
            Step {step} of {TOTAL_STEPS}
          </span>
          <h2>{stepTitle(step)}</h2>
        </div>

        {/* Body */}
        <div className="wizard-body">
          {step === 1 && (
            <>
              <ol>
                <li>
                  Go to the{" "}
                  <a
                    href="https://discord.com/developers/applications"
                    target="_blank"
                    rel="noopener noreferrer"
                  >
                    Discord Developer Portal
                  </a>
                </li>
                <li>Click <strong>New Application</strong> and give it a name</li>
                <li>Navigate to <strong>Bot</strong> in the left sidebar</li>
                <li>Click <strong>Reset Token</strong> and copy the token</li>
              </ol>

              <div className="wizard-field">
                <label htmlFor="bot-token">Bot Token</label>
                <input
                  id="bot-token"
                  type="password"
                  autoComplete="off"
                  placeholder="Paste your bot token here"
                  value={botToken}
                  onChange={(e) => setBotToken(e.target.value)}
                />
              </div>
            </>
          )}

          {step === 2 && (
            <>
              <p>
                First, enter your <strong>Application ID</strong> (found on the
                Developer Portal under <em>General Information</em>).
              </p>

              <div className="wizard-field">
                <label htmlFor="app-id">Application ID</label>
                <input
                  id="app-id"
                  type="text"
                  inputMode="numeric"
                  autoComplete="off"
                  placeholder="e.g. 110270667856912345"
                  value={appId}
                  onChange={(e) => setAppId(e.target.value)}
                />
              </div>

              {inviteUrl ? (
                <>
                  <p>
                    Use the link below to invite your bot to a Discord server.
                    You'll need the <strong>Manage Server</strong> permission on
                    the target server.
                  </p>
                  <div className="invite-link-box">
                    <a href={inviteUrl} target="_blank" rel="noopener noreferrer">
                      {inviteUrl}
                    </a>
                  </div>
                </>
              ) : (
                <p>Enter your Application ID above to generate the invite link.</p>
              )}

              <p style={{ marginTop: 16 }}>
                To get your <strong>Server ID</strong>:
              </p>
              <ol>
                <li>Open Discord and go to <strong>User Settings → Advanced</strong></li>
                <li>Enable <strong>Developer Mode</strong></li>
                <li>Right-click your server name → <strong>Copy Server ID</strong></li>
              </ol>

              <div className="wizard-field">
                <label htmlFor="guild-id">Server ID (Guild ID)</label>
                <input
                  id="guild-id"
                  type="text"
                  inputMode="numeric"
                  autoComplete="off"
                  placeholder="e.g. 123456789012345678"
                  value={guildId}
                  onChange={(e) => setGuildId(e.target.value)}
                />
              </div>
            </>
          )}

          {step === 3 && (
            <>
              <p>
                Choose which channel the bot should post notifications to.
              </p>
              <ol>
                <li>Make sure <strong>Developer Mode</strong> is enabled (see previous step)</li>
                <li>Right-click the target text channel → <strong>Copy Channel ID</strong></li>
              </ol>

              <div className="wizard-field">
                <label htmlFor="channel-id">Channel ID</label>
                <input
                  id="channel-id"
                  type="text"
                  inputMode="numeric"
                  autoComplete="off"
                  placeholder="e.g. 987654321098765432"
                  value={channelId}
                  onChange={(e) => setChannelId(e.target.value)}
                />
              </div>
            </>
          )}

          {step === 4 && (
            <>
              <p>
                Configure, connect, and verify your Discord bot. Run each step
                in order.
              </p>

              <div className="wizard-action-row">
                <Button
                  appearance="primary"
                  disabled={configStatus === "loading"}
                  onClick={handleConfigure}
                >
                  {configStatus === "loading" ? (
                    <Spinner size="tiny" />
                  ) : (
                    "1. Configure"
                  )}
                </Button>
                <StatusBadge status={configStatus} />
              </div>

              <div className="wizard-action-row">
                <Button
                  appearance="primary"
                  disabled={
                    configStatus !== "success" || connectStatus === "loading"
                  }
                  onClick={handleConnect}
                >
                  {connectStatus === "loading" ? (
                    <Spinner size="tiny" />
                  ) : (
                    "2. Connect"
                  )}
                </Button>
                <StatusBadge status={connectStatus} />
              </div>

              <div className="wizard-action-row">
                <Button
                  appearance="secondary"
                  disabled={
                    connectStatus !== "success" || testStatus === "loading"
                  }
                  onClick={handleTest}
                >
                  {testStatus === "loading" ? (
                    <Spinner size="tiny" />
                  ) : (
                    "3. Send Test Notification"
                  )}
                </Button>
                <StatusBadge status={testStatus} />
              </div>

              {error && <div className="wizard-error">{error}</div>}
            </>
          )}
        </div>

        {/* Footer */}
        <div className="wizard-footer">
          {step > 1 && step < 4 && (
            <Button appearance="subtle" onClick={back}>
              Back
            </Button>
          )}

          {step < 4 && (
            <Button
              appearance="primary"
              disabled={!canAdvance(step, botToken, appId, guildId, channelId)}
              onClick={next}
            >
              Next
            </Button>
          )}

          {step === 4 && onClose && (
            <Button
              appearance="primary"
              disabled={connectStatus !== "success"}
              onClick={handleClose}
            >
              Done
            </Button>
          )}

          {onClose && (
            <Button appearance="subtle" onClick={handleClose}>
              Cancel
            </Button>
          )}
        </div>
      </div>
  );

  if (inline) {
    return panel;
  }

  return (
    <div className="wizard-overlay" onClick={handleClose}>
      {panel}
    </div>
  );
}

// ── Helpers ────────────────────────────────────────────────────────────

function stepTitle(step: number): string {
  switch (step) {
    case 1: return "Create a Discord Bot";
    case 2: return "Add Bot to Server";
    case 3: return "Select Channel";
    case 4: return "Connect & Test";
    default: return "";
  }
}

function canAdvance(
  step: number,
  botToken: string,
  appId: string,
  guildId: string,
  channelId: string,
): boolean {
  switch (step) {
    case 1: return botToken.trim().length > 0;
    case 2: return appId.trim().length > 0 && guildId.trim().length > 0;
    case 3: return channelId.trim().length > 0;
    default: return false;
  }
}

function StatusBadge({ status }: { status: ConnectionStatus }) {
  if (status === "idle") return null;

  return (
    <span className="wizard-status" data-status={status}>
      {status === "loading" && <Spinner size="tiny" />}
      {status === "success" && <CheckmarkCircle24Filled />}
      {status === "error" && <DismissCircle24Filled />}
      {status === "loading" && "Working…"}
      {status === "success" && "Done"}
      {status === "error" && "Failed"}
    </span>
  );
}
