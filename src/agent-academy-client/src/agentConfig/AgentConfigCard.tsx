import { useCallback, useEffect, useState } from "react";
import {
  Button,
  Spinner,
  Textarea,
  Input,
  Select,
  Dialog,
  DialogSurface,
  DialogBody,
  DialogActions,
  DialogContent,
  DialogTitle,
} from "@fluentui/react-components";
import V3Badge from "../V3Badge";
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
} from "../api";
import { useAgentConfigCardStyles } from "./AgentConfigCardStyles";
import { roleLabel, parseQuotaInt, parseQuotaFloat } from "./helpers";
import QuotaSection from "./QuotaSection";

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
  const s = useAgentConfigCardStyles();

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

              <QuotaSection
                s={s}
                quota={quota}
                hasQuotaConfigured={!!hasQuotaConfigured}
                hasQuotaChanges={!!hasQuotaChanges}
                maxRequestsPerHour={maxRequestsPerHour}
                maxTokensPerHour={maxTokensPerHour}
                maxCostPerHour={maxCostPerHour}
                onMaxRequestsChange={setMaxRequestsPerHour}
                onMaxTokensChange={setMaxTokensPerHour}
                onMaxCostChange={setMaxCostPerHour}
                quotaSaving={quotaSaving}
                onSave={handleQuotaSave}
                onRemove={() => setShowRemoveQuotaDialog(true)}
              />
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
