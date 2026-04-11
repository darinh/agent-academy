import { useCallback, useEffect, useState } from "react";
import {
  Button,
  Spinner,
  Textarea,
  Input,
  Select,
  makeStyles,
  shorthands,
  Dialog,
  DialogSurface,
  DialogBody,
  DialogActions,
  DialogContent,
  DialogTitle,
} from "@fluentui/react-components";
import V3Badge from "./V3Badge";
import {
  ChevronDownRegular,
  ChevronUpRegular,
  ArrowResetRegular,
  SaveRegular,
  BotRegular,
} from "@fluentui/react-icons";
import {
  getAgentConfig,
  upsertAgentConfig,
  resetAgentConfig,
  getAgentQuota,
  updateAgentQuota,
  removeAgentQuota,
  type AgentDefinition,
  type AgentConfigResponse,
  type InstructionTemplate,
  type QuotaStatus,
} from "./api";

// ── Styles ──────────────────────────────────────────────────────────────

const useLocalStyles = makeStyles({
  card: {
    ...shorthands.padding("16px"),
    ...shorthands.borderRadius("12px"),
    border: "1px solid var(--aa-hairline)",
    backgroundColor: "rgba(255, 255, 255, 0.03)",
    marginBottom: "12px",
  },
  cardHeader: {
    display: "flex",
    alignItems: "center",
    justifyContent: "space-between",
    cursor: "pointer",
    gap: "12px",
    userSelect: "none",
  },
  agentInfo: {
    display: "flex",
    alignItems: "center",
    gap: "10px",
    minWidth: 0,
  },
  agentName: {
    fontSize: "14px",
    fontWeight: 600,
    color: "var(--aa-text-strong)",
  },
  agentRole: {
    fontSize: "12px",
    color: "var(--aa-muted)",
  },
  badges: {
    display: "flex",
    alignItems: "center",
    gap: "6px",
    flexShrink: 0,
  },
  formContainer: {
    marginTop: "16px",
    ...shorthands.borderTop("1px", "solid", "var(--aa-hairline)"),
    paddingTop: "16px",
    display: "flex",
    flexDirection: "column",
    gap: "16px",
  },
  fieldGroup: {
    display: "flex",
    flexDirection: "column",
    gap: "4px",
  },
  fieldLabel: {
    fontSize: "12px",
    fontWeight: 600,
    color: "var(--aa-soft)",
    textTransform: "uppercase" as const,
    letterSpacing: "0.5px",
  },
  fieldHint: {
    fontSize: "11px",
    color: "var(--aa-soft)",
  },
  actions: {
    display: "flex",
    alignItems: "center",
    gap: "8px",
    justifyContent: "flex-end",
    marginTop: "4px",
  },
  error: {
    fontSize: "13px",
    color: "var(--aa-copper)",
  },
  quotaSection: {
    ...shorthands.borderTop("1px", "solid", "var(--aa-hairline)"),
    paddingTop: "16px",
    display: "flex",
    flexDirection: "column",
    gap: "12px",
  },
  quotaHeader: {
    display: "flex",
    alignItems: "center",
    justifyContent: "space-between",
  },
  quotaSectionLabel: {
    fontSize: "12px",
    fontWeight: 700,
    color: "var(--aa-soft)",
    textTransform: "uppercase" as const,
    letterSpacing: "0.8px",
  },
  quotaGrid: {
    display: "grid",
    gridTemplateColumns: "1fr 1fr",
    gap: "12px",
  },
  quotaUsage: {
    fontSize: "11px",
    color: "var(--aa-muted)",
    marginTop: "2px",
  },
  quotaUnlimited: {
    fontSize: "12px",
    color: "var(--aa-muted)",
    fontStyle: "italic",
  },
});

// ── Helpers ──────────────────────────────────────────────────────────────

function roleLabel(role: string): string {
  const map: Record<string, string> = {
    Planner: "Planner",
    Architect: "Architect",
    SoftwareEngineer: "Engineer",
    Reviewer: "Reviewer",
    TechnicalWriter: "Writer",
  };
  return map[role] ?? role;
}

/** Parse a quota input string. Returns null for empty/whitespace, a valid number, or NaN on bad input. */
function parseQuotaInt(value: string): number | null {
  const trimmed = value.trim();
  if (!trimmed) return null;
  const n = Number(trimmed);
  if (!Number.isFinite(n) || !Number.isInteger(n) || n < 0) return NaN;
  return n;
}

function parseQuotaFloat(value: string): number | null {
  const trimmed = value.trim();
  if (!trimmed) return null;
  const n = Number(trimmed);
  if (!Number.isFinite(n) || n < 0) return NaN;
  return n;
}

// ── Component ───────────────────────────────────────────────────────────

interface AgentConfigCardProps {
  agent: AgentDefinition;
  templates: InstructionTemplate[];
  expanded: boolean;
  onToggle: () => void;
  onSaved: () => void;
}

export default function AgentConfigCard({
  agent,
  templates,
  expanded,
  onToggle,
  onSaved,
}: AgentConfigCardProps) {
  const s = useLocalStyles();

  const [loading, setLoading] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [config, setConfig] = useState<AgentConfigResponse | null>(null);
  const [showResetDialog, setShowResetDialog] = useState(false);

  // Form state
  const [modelOverride, setModelOverride] = useState("");
  const [startupPromptOverride, setStartupPromptOverride] = useState("");
  const [customInstructions, setCustomInstructions] = useState("");
  const [templateId, setTemplateId] = useState("");

  // Quota state
  const [quota, setQuota] = useState<QuotaStatus | null>(null);
  const [maxRequestsPerHour, setMaxRequestsPerHour] = useState("");
  const [maxTokensPerHour, setMaxTokensPerHour] = useState("");
  const [maxCostPerHour, setMaxCostPerHour] = useState("");
  const [quotaSaving, setQuotaSaving] = useState(false);
  const [showRemoveQuotaDialog, setShowRemoveQuotaDialog] = useState(false);

  // Load config and quota when expanded (independent — quota failure doesn't block config)
  useEffect(() => {
    if (!expanded) return;
    let cancelled = false;
    setLoading(true);
    setError(null);

    const configPromise = getAgentConfig(agent.id);
    const quotaPromise = getAgentQuota(agent.id);

    Promise.allSettled([configPromise, quotaPromise])
      .then(([configResult, quotaResult]) => {
        if (cancelled) return;

        if (configResult.status === "fulfilled") {
          const configData = configResult.value;
          setConfig(configData);
          setModelOverride(configData.override?.modelOverride ?? "");
          setStartupPromptOverride(configData.override?.startupPromptOverride ?? "");
          setCustomInstructions(configData.override?.customInstructions ?? "");
          setTemplateId(configData.override?.instructionTemplateId ?? "");
        } else {
          setError(configResult.reason instanceof Error
            ? configResult.reason.message : "Failed to load config");
        }

        if (quotaResult.status === "fulfilled") {
          const quotaData = quotaResult.value;
          setQuota(quotaData);
          setMaxRequestsPerHour(quotaData.configuredQuota?.maxRequestsPerHour?.toString() ?? "");
          setMaxTokensPerHour(quotaData.configuredQuota?.maxTokensPerHour?.toString() ?? "");
          setMaxCostPerHour(quotaData.configuredQuota?.maxCostPerHour?.toString() ?? "");
        }
        // Quota failure is non-fatal — config editing still works
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });

    return () => { cancelled = true; };
  }, [expanded, agent.id]);

  const handleSave = useCallback(async () => {
    setSaving(true);
    setError(null);
    try {
      await upsertAgentConfig(agent.id, {
        modelOverride: modelOverride.trim() || null,
        startupPromptOverride: startupPromptOverride.trim() || null,
        customInstructions: customInstructions.trim() || null,
        instructionTemplateId: templateId || null,
      });
      onSaved();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to save config");
    } finally {
      setSaving(false);
    }
  }, [agent.id, modelOverride, startupPromptOverride, customInstructions, templateId, onSaved]);

  const handleReset = useCallback(async () => {
    setShowResetDialog(false);
    setSaving(true);
    setError(null);
    try {
      await resetAgentConfig(agent.id);
      onSaved();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to reset config");
    } finally {
      setSaving(false);
    }
  }, [agent.id, onSaved]);

  const handleQuotaSave = useCallback(async () => {
    const reqPerHour = parseQuotaInt(maxRequestsPerHour);
    const tokPerHour = parseQuotaInt(maxTokensPerHour);
    const costPerHour = parseQuotaFloat(maxCostPerHour);

    if (Number.isNaN(reqPerHour) || Number.isNaN(tokPerHour) || Number.isNaN(costPerHour)) {
      setError("Quota values must be non-negative numbers. Requests and tokens must be whole numbers.");
      return;
    }

    setQuotaSaving(true);
    setError(null);
    try {
      const req = {
        maxRequestsPerHour: reqPerHour,
        maxTokensPerHour: tokPerHour,
        maxCostPerHour: costPerHour,
      };
      const updated = await updateAgentQuota(agent.id, req);
      setQuota(updated);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to save quota");
    } finally {
      setQuotaSaving(false);
    }
  }, [agent.id, maxRequestsPerHour, maxTokensPerHour, maxCostPerHour]);

  const handleRemoveQuota = useCallback(async () => {
    setShowRemoveQuotaDialog(false);
    setQuotaSaving(true);
    setError(null);
    try {
      await removeAgentQuota(agent.id);
      setMaxRequestsPerHour("");
      setMaxTokensPerHour("");
      setMaxCostPerHour("");
      const updated = await getAgentQuota(agent.id);
      setQuota(updated);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to remove quota");
    } finally {
      setQuotaSaving(false);
    }
  }, [agent.id]);

  const hasChanges = config && (
    (modelOverride.trim() || null) !== (config.override?.modelOverride ?? null) ||
    (startupPromptOverride.trim() || null) !== (config.override?.startupPromptOverride ?? null) ||
    (customInstructions.trim() || null) !== (config.override?.customInstructions ?? null) ||
    (templateId || null) !== (config.override?.instructionTemplateId ?? null)
  );

  const hasQuotaConfigured = quota?.configuredQuota != null && (
    quota.configuredQuota.maxRequestsPerHour != null ||
    quota.configuredQuota.maxTokensPerHour != null ||
    quota.configuredQuota.maxCostPerHour != null
  );

  const hasQuotaChanges = quota && (
    parseQuotaInt(maxRequestsPerHour) !== (quota.configuredQuota?.maxRequestsPerHour ?? null) ||
    parseQuotaInt(maxTokensPerHour) !== (quota.configuredQuota?.maxTokensPerHour ?? null) ||
    parseQuotaFloat(maxCostPerHour) !== (quota.configuredQuota?.maxCostPerHour ?? null)
  );

  return (
    <div className={s.card}>
      <div className={s.cardHeader} onClick={onToggle}>
        <div className={s.agentInfo}>
          <BotRegular />
          <span className={s.agentName}>{agent.name}</span>
          <span className={s.agentRole}>{roleLabel(agent.role)}</span>
        </div>
        <div className={s.badges}>
          <V3Badge color="info">
            {agent.model ?? "default"}
          </V3Badge>
          {config?.hasOverride && (
            <V3Badge color="warn">
              Customized
            </V3Badge>
          )}
          {hasQuotaConfigured && (
            <V3Badge color="info">
              Quota
            </V3Badge>
          )}
          {expanded ? <ChevronUpRegular /> : <ChevronDownRegular />}
        </div>
      </div>

      {expanded && (
        <div className={s.formContainer}>
          {loading ? (
            <Spinner size="small" label="Loading configuration…" />
          ) : (
            <>
              {/* Model override */}
              <div className={s.fieldGroup}>
                <label className={s.fieldLabel}>Model Override</label>
                <Input
                  placeholder={agent.model ?? "gpt-5 (default)"}
                  value={modelOverride}
                  onChange={(_, data) => setModelOverride(data.value)}
                />
                <span className={s.fieldHint}>
                  Leave empty to use catalog default: {agent.model ?? "gpt-5"}
                </span>
              </div>

              {/* Startup prompt override */}
              <div className={s.fieldGroup}>
                <label className={s.fieldLabel}>Startup Prompt Override</label>
                <Textarea
                  placeholder="Override the agent's startup prompt (leave empty to use default)"
                  value={startupPromptOverride}
                  onChange={(_, data) => setStartupPromptOverride(data.value)}
                  resize="vertical"
                  rows={4}
                />
                <span className={s.fieldHint}>
                  Replaces the catalog startup prompt. The effective prompt layers: this → template → custom instructions.
                </span>
              </div>

              {/* Instruction template */}
              <div className={s.fieldGroup}>
                <label className={s.fieldLabel}>Instruction Template</label>
                <Select
                  value={templateId}
                  onChange={(_, data) => setTemplateId(data.value)}
                >
                  <option value="">None</option>
                  {templates.map((t) => (
                    <option key={t.id} value={t.id}>
                      {t.name}
                    </option>
                  ))}
                </Select>
                <span className={s.fieldHint}>
                  Template content is appended after the startup prompt.
                </span>
              </div>

              {/* Custom instructions */}
              <div className={s.fieldGroup}>
                <label className={s.fieldLabel}>Custom Instructions</label>
                <Textarea
                  placeholder="Additional instructions appended after prompt and template"
                  value={customInstructions}
                  onChange={(_, data) => setCustomInstructions(data.value)}
                  resize="vertical"
                  rows={3}
                />
                <span className={s.fieldHint}>
                  Appended at the end of the effective prompt, after template content.
                </span>
              </div>

              {error && <div className={s.error}>{error}</div>}

              <div className={s.actions}>
                {config?.hasOverride && (
                  <Button
                    appearance="subtle"
                    size="small"
                    icon={<ArrowResetRegular />}
                    disabled={saving}
                    onClick={() => setShowResetDialog(true)}
                  >
                    Reset to Defaults
                  </Button>
                )}
                <Button
                  appearance="primary"
                  size="small"
                  icon={<SaveRegular />}
                  disabled={saving || !hasChanges}
                  onClick={handleSave}
                >
                  {saving ? <Spinner size="tiny" /> : "Save"}
                </Button>
              </div>

              {/* ── Quota Section ── */}
              <div className={s.quotaSection}>
                <div className={s.quotaHeader}>
                  <span className={s.quotaSectionLabel}>Resource Quotas</span>
                  {hasQuotaConfigured && (
                    <Button
                      appearance="subtle"
                      size="small"
                      icon={<ArrowResetRegular />}
                      disabled={quotaSaving}
                      onClick={() => setShowRemoveQuotaDialog(true)}
                    >
                      Remove Limits
                    </Button>
                  )}
                </div>

                {!hasQuotaConfigured && !hasQuotaChanges && (
                  <span className={s.quotaUnlimited}>No limits configured — agent has unlimited access.</span>
                )}

                <div className={s.quotaGrid}>
                  <div className={s.fieldGroup}>
                    <label className={s.fieldLabel}>Max Requests / Hour</label>
                    <Input
                      type="number"
                      placeholder="Unlimited"
                      value={maxRequestsPerHour}
                      onChange={(_, data) => setMaxRequestsPerHour(data.value)}
                      min={0}
                    />
                    {quota?.currentUsage != null && (
                      <span className={s.quotaUsage}>
                        Current: {quota.currentUsage.requestCount} requests this hour
                      </span>
                    )}
                  </div>
                  <div className={s.fieldGroup}>
                    <label className={s.fieldLabel}>Max Tokens / Hour</label>
                    <Input
                      type="number"
                      placeholder="Unlimited"
                      value={maxTokensPerHour}
                      onChange={(_, data) => setMaxTokensPerHour(data.value)}
                      min={0}
                    />
                    {quota?.currentUsage != null && (
                      <span className={s.quotaUsage}>
                        Current: {quota.currentUsage.totalTokens.toLocaleString()} tokens this hour
                      </span>
                    )}
                  </div>
                  <div className={s.fieldGroup}>
                    <label className={s.fieldLabel}>Max Cost / Hour ($)</label>
                    <Input
                      type="number"
                      placeholder="Unlimited"
                      value={maxCostPerHour}
                      onChange={(_, data) => setMaxCostPerHour(data.value)}
                      min={0}
                      step={0.01}
                    />
                    {quota?.currentUsage != null && (
                      <span className={s.quotaUsage}>
                        Current: ${quota.currentUsage.totalCost.toFixed(4)} this hour
                      </span>
                    )}
                  </div>
                </div>

                <div className={s.actions}>
                  <Button
                    appearance="primary"
                    size="small"
                    icon={<SaveRegular />}
                    disabled={quotaSaving || !hasQuotaChanges}
                    onClick={handleQuotaSave}
                  >
                    {quotaSaving ? <Spinner size="tiny" /> : "Save Quotas"}
                  </Button>
                </div>
              </div>
            </>
          )}
        </div>
      )}

      {/* Reset confirmation dialog */}
      <Dialog open={showResetDialog} onOpenChange={(_, data) => setShowResetDialog(data.open)}>
        <DialogSurface>
          <DialogBody>
            <DialogTitle>Reset {agent.name}'s Configuration?</DialogTitle>
            <DialogContent>
              This will remove all overrides and revert to the catalog defaults.
              The agent will use its original startup prompt, model, and no custom instructions.
            </DialogContent>
            <DialogActions>
              <Button appearance="secondary" onClick={() => setShowResetDialog(false)}>
                Cancel
              </Button>
              <Button appearance="primary" onClick={handleReset}>
                Reset
              </Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>

      {/* Remove quota confirmation dialog */}
      <Dialog open={showRemoveQuotaDialog} onOpenChange={(_, data) => setShowRemoveQuotaDialog(data.open)}>
        <DialogSurface>
          <DialogBody>
            <DialogTitle>Remove {agent.name}'s Quotas?</DialogTitle>
            <DialogContent>
              This will remove all resource limits. The agent will have unlimited
              requests, tokens, and cost per hour.
            </DialogContent>
            <DialogActions>
              <Button appearance="secondary" onClick={() => setShowRemoveQuotaDialog(false)}>
                Cancel
              </Button>
              <Button appearance="primary" onClick={handleRemoveQuota}>
                Remove Limits
              </Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
    </div>
  );
}
