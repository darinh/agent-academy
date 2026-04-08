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
  type AgentDefinition,
  type AgentConfigResponse,
  type InstructionTemplate,
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

  // Load config when expanded
  useEffect(() => {
    if (!expanded) return;
    let cancelled = false;
    setLoading(true);
    setError(null);

    getAgentConfig(agent.id)
      .then((data) => {
        if (cancelled) return;
        setConfig(data);
        setModelOverride(data.override?.modelOverride ?? "");
        setStartupPromptOverride(data.override?.startupPromptOverride ?? "");
        setCustomInstructions(data.override?.customInstructions ?? "");
        setTemplateId(data.override?.instructionTemplateId ?? "");
      })
      .catch((err) => {
        if (!cancelled) setError(err instanceof Error ? err.message : "Failed to load config");
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

  const hasChanges = config && (
    (modelOverride.trim() || null) !== (config.override?.modelOverride ?? null) ||
    (startupPromptOverride.trim() || null) !== (config.override?.startupPromptOverride ?? null) ||
    (customInstructions.trim() || null) !== (config.override?.customInstructions ?? null) ||
    (templateId || null) !== (config.override?.instructionTemplateId ?? null)
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
    </div>
  );
}
