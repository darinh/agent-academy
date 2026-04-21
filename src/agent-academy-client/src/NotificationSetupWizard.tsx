import { useState, useCallback, useEffect } from "react";
import {
  Button,
  Spinner,
} from "@fluentui/react-components";
import {
  configureProvider,
  connectProvider,
  testNotification,
  getProviderSchema,
  type ConfigField,
} from "./api";
import "./NotificationSetupWizard.css";
import { TOTAL_STEPS, BOT_PERMISSIONS } from "./notificationWizard/constants";
import { getStepTitle, getProviderDisplayName } from "./notificationWizard/helpers";
import { StatusBadge } from "./notificationWizard/StatusBadge";
import type { ConnectionStatus } from "./notificationWizard/StatusBadge";
import { ProviderInstructions } from "./notificationWizard/ProviderInstructions";

// Re-export submodule exports for backward compatibility
export { ProviderInstructions, DiscordInstructions, SlackInstructions, GenericInstructions } from "./notificationWizard/ProviderInstructions";
export { getStepTitle, getProviderDisplayName } from "./notificationWizard/helpers";

interface Props {
  /** Which provider this wizard configures. */
  providerId: string;
  /** Called when the user closes or completes the wizard. */
  onClose?: () => void;
  /** When true, renders as inline content without the overlay backdrop. */
  inline?: boolean;
}

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
