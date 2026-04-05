import { useState, useCallback, useEffect } from "react";
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
  getProviderSchema,
  type ConfigField,
} from "./api";
import "./NotificationSetupWizard.css";

// ── Constants ──────────────────────────────────────────────────────────

const TOTAL_STEPS = 3; // Instructions → Credentials → Connect & Test

/**
 * Discord bot permissions for Send Messages, Embed Links,
 * Read Message History, Use Slash Commands.
 */
const BOT_PERMISSIONS = (
  (1n << 11n) | // Send Messages
  (1n << 14n) | // Embed Links
  (1n << 16n) | // Read Message History
  (1n << 31n)   // Use Slash Commands (APPLICATION_COMMANDS)
).toString();

// ── Types ──────────────────────────────────────────────────────────────

interface Props {
  /** Which provider this wizard configures. */
  providerId: string;
  /** Called when the user closes or completes the wizard. */
  onClose?: () => void;
  /** When true, renders as inline content without the overlay backdrop. */
  inline?: boolean;
}

type ConnectionStatus = "idle" | "loading" | "success" | "error";

// ── Component ──────────────────────────────────────────────────────────

export default function NotificationSetupWizard({ providerId, onClose, inline }: Props) {
  const [step, setStep] = useState(1);

  // Schema loaded from backend
  const [fields, setFields] = useState<ConfigField[]>([]);
  const [schemaLoading, setSchemaLoading] = useState(true);

  // Dynamic form values keyed by field key
  const [formValues, setFormValues] = useState<Record<string, string>>({});

  // Discord-specific: invite URL helper (not a config field)
  const [appId, setAppId] = useState("");

  // Step 3
  const [configStatus, setConfigStatus] = useState<ConnectionStatus>("idle");
  const [connectStatus, setConnectStatus] = useState<ConnectionStatus>("idle");
  const [testStatus, setTestStatus] = useState<ConnectionStatus>("idle");
  const [error, setError] = useState<string | null>(null);

  // Schema fetch error
  const [schemaError, setSchemaError] = useState(false);

  // ── Fetch schema and reset state on provider change ────────────────

  useEffect(() => {
    let cancelled = false;
    setSchemaLoading(true);
    setSchemaError(false);
    // Reset all wizard state on provider change
    setStep(1);
    setAppId("");
    setConfigStatus("idle");
    setConnectStatus("idle");
    setTestStatus("idle");
    setError(null);

    getProviderSchema(providerId)
      .then((schema) => {
        if (cancelled) return;
        setFields(schema.fields);
        const initial: Record<string, string> = {};
        for (const f of schema.fields) initial[f.key] = "";
        setFormValues(initial);
      })
      .catch(() => {
        if (cancelled) return;
        setFields([]);
        setSchemaError(true);
      })
      .finally(() => {
        if (!cancelled) setSchemaLoading(false);
      });
    return () => { cancelled = true; };
  }, [providerId]);

  // ── Navigation helpers ─────────────────────────────────────────────

  const next = useCallback(() => setStep((s) => Math.min(s + 1, TOTAL_STEPS)), []);
  const back = useCallback(() => setStep((s) => Math.max(s - 1, 1)), []);

  // ── Form field change handler ──────────────────────────────────────

  const setFieldValue = useCallback((key: string, value: string) => {
    setFormValues((prev) => ({ ...prev, [key]: value }));
  }, []);

  // ── Step 3 actions ─────────────────────────────────────────────────

  const handleConfigure = useCallback(async () => {
    setConfigStatus("loading");
    setError(null);
    try {
      await configureProvider(providerId, formValues);
      setConfigStatus("success");
    } catch (err) {
      setConfigStatus("error");
      setError(err instanceof Error ? err.message : "Configuration failed");
    }
  }, [providerId, formValues]);

  const handleConnect = useCallback(async () => {
    setConnectStatus("loading");
    setError(null);
    try {
      await connectProvider(providerId);
      setConnectStatus("success");
    } catch (err) {
      setConnectStatus("error");
      setError(err instanceof Error ? err.message : "Connection failed");
    }
  }, [providerId]);

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

  // ── Invite URL (Discord only) ──────────────────────────────────────

  const inviteUrl = providerId === "discord" && appId.trim()
    ? `https://discord.com/oauth2/authorize?client_id=${encodeURIComponent(appId.trim())}&scope=bot&permissions=${BOT_PERMISSIONS}`
    : "";

  // ── Can advance check ──────────────────────────────────────────────

  const canAdvanceToNext = (currentStep: number): boolean => {
    if (currentStep === 1) return true; // instructions step — always can advance
    if (currentStep === 2) {
      // All required fields must be filled
      return fields
        .filter((f) => f.required)
        .every((f) => (formValues[f.key] ?? "").trim().length > 0);
    }
    return false;
  };

  // ── Render ─────────────────────────────────────────────────────────

  const handleClose = onClose ?? (() => {});

  // Loading or error state — show panel shell with appropriate content
  if (schemaLoading || schemaError) {
    const shellContent = schemaLoading ? (
      <Spinner size="small" label="Loading setup wizard…" />
    ) : (
      <div className="wizard-error">
        Failed to load provider configuration. Please try again.
        {onClose && (
          <div style={{ marginTop: 12 }}>
            <Button appearance="subtle" size="small" onClick={handleClose}>Close</Button>
          </div>
        )}
      </div>
    );

    const shell = (
      <div className={inline ? "wizard-panel wizard-panel--inline" : "wizard-panel"} onClick={(e) => e.stopPropagation()}>
        {shellContent}
      </div>
    );

    if (inline) return shell;
    return <div className="wizard-overlay" onClick={handleClose}>{shell}</div>;
  }

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
          <h2>{getStepTitle(providerId, step)}</h2>
        </div>

        {/* Body */}
        <div className="wizard-body">
          {step === 1 && (
            <ProviderInstructions providerId={providerId} appId={appId} onAppIdChange={setAppId} inviteUrl={inviteUrl} />
          )}

          {step === 2 && (
            <>
              <p>Enter your {getProviderDisplayName(providerId)} credentials below.</p>
              {fields.map((field) => (
                <div className="wizard-field" key={field.key}>
                  <label htmlFor={`wizard-${field.key}`}>{field.label}</label>
                  <input
                    id={`wizard-${field.key}`}
                    type={field.type === "secret" ? "password" : "text"}
                    autoComplete="off"
                    placeholder={field.placeholder ?? ""}
                    value={formValues[field.key] ?? ""}
                    onChange={(e) => setFieldValue(field.key, e.target.value)}
                  />
                  {field.description && (
                    <span className="wizard-field-hint">{field.description}</span>
                  )}
                </div>
              ))}
            </>
          )}

          {step === 3 && (
            <>
              <p>
                Configure, connect, and verify your {getProviderDisplayName(providerId)} integration.
                Run each step in order.
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
          {step > 1 && step < TOTAL_STEPS && (
            <Button appearance="subtle" onClick={back}>
              Back
            </Button>
          )}

          {step < TOTAL_STEPS && (
            <Button
              appearance="primary"
              disabled={!canAdvanceToNext(step)}
              onClick={next}
            >
              Next
            </Button>
          )}

          {step === TOTAL_STEPS && onClose && (
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

// ── Provider-specific instructions (exported for testing) ──────────────

export function ProviderInstructions({
  providerId,
  appId,
  onAppIdChange,
  inviteUrl,
}: {
  providerId: string;
  appId: string;
  onAppIdChange: (v: string) => void;
  inviteUrl: string;
}) {
  if (providerId === "discord") return <DiscordInstructions appId={appId} onAppIdChange={onAppIdChange} inviteUrl={inviteUrl} />;
  if (providerId === "slack") return <SlackInstructions />;
  return <GenericInstructions />;
}

export function DiscordInstructions({
  appId,
  onAppIdChange,
  inviteUrl,
}: {
  appId: string;
  onAppIdChange: (v: string) => void;
  inviteUrl: string;
}) {
  return (
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
        <li>Click <strong>Reset Token</strong> and copy the token (you'll need it in the next step)</li>
      </ol>

      <p style={{ marginTop: 16 }}>
        <strong>Invite your bot to a server:</strong> Enter your Application ID
        (found under <em>General Information</em>) to generate the invite link.
      </p>

      <div className="wizard-field">
        <label htmlFor="discord-app-id">Application ID</label>
        <input
          id="discord-app-id"
          type="text"
          inputMode="numeric"
          autoComplete="off"
          placeholder="e.g. 110270667856912345"
          value={appId}
          onChange={(e) => onAppIdChange(e.target.value)}
        />
      </div>

      {inviteUrl ? (
        <div className="invite-link-box">
          <a href={inviteUrl} target="_blank" rel="noopener noreferrer">
            {inviteUrl}
          </a>
        </div>
      ) : (
        <p className="wizard-field-hint">Enter your Application ID above to generate the invite link.</p>
      )}
    </>
  );
}

export function SlackInstructions() {
  return (
    <>
      <ol>
        <li>
          Go to{" "}
          <a
            href="https://api.slack.com/apps"
            target="_blank"
            rel="noopener noreferrer"
          >
            api.slack.com/apps
          </a>{" "}
          and click <strong>Create New App</strong> → <strong>From scratch</strong>
        </li>
        <li>Name your app (e.g. &quot;Agent Academy&quot;) and select your workspace</li>
        <li>
          Navigate to <strong>OAuth &amp; Permissions</strong> and add these <strong>Bot Token Scopes</strong>:
          <ul className="scope-list">
            <li><code>chat:write</code> — send messages</li>
            <li><code>channels:manage</code> — create channels</li>
            <li><code>channels:read</code> — list channels</li>
            <li><code>channels:join</code> — join public channels</li>
            <li><code>groups:write</code> — manage private channels</li>
            <li><code>groups:read</code> — list private channels</li>
            <li><code>users:read</code> — resolve user info</li>
          </ul>
        </li>
        <li>Click <strong>Install to Workspace</strong> and authorize</li>
        <li>Copy the <strong>Bot User OAuth Token</strong> (starts with <code>xoxb-</code>)</li>
      </ol>
      <p>
        You'll also need a <strong>Default Channel ID</strong> — right-click any channel
        in Slack → <strong>View channel details</strong> → copy the ID at the bottom.
      </p>
    </>
  );
}

export function GenericInstructions() {
  return (
    <p>
      Follow your provider's documentation to obtain the required credentials,
      then proceed to the next step to enter them.
    </p>
  );
}

// ── Helpers (exported for testing) ──────────────────────────────────────

export function getStepTitle(providerId: string, step: number): string {
  const name = getProviderDisplayName(providerId);
  switch (step) {
    case 1: return `Set Up ${name}`;
    case 2: return "Enter Credentials";
    case 3: return "Connect & Test";
    default: return "";
  }
}

export function getProviderDisplayName(providerId: string): string {
  switch (providerId) {
    case "discord": return "Discord";
    case "slack": return "Slack";
    default: return providerId.charAt(0).toUpperCase() + providerId.slice(1);
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
