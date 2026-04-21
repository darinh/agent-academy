import { useState, useCallback } from "react";
import {
  Button,
  Spinner,
  makeStyles,
  shorthands,
} from "@fluentui/react-components";
import {
  BotRegular,
  AddRegular,
  DeleteRegular,
} from "@fluentui/react-icons";
import type { AgentDefinition } from "../api";
import { createCustomAgent, deleteCustomAgent } from "../api";
import { useSettingsStyles } from "./settingsStyles";

const useStyles = makeStyles({
  createAgentForm: {
    ...shorthands.padding("20px"),
    ...shorthands.borderRadius("12px"),
    border: "1px dashed rgba(99,179,237,0.25)",
    background: "rgba(99,179,237,0.03)",
    display: "flex",
    flexDirection: "column",
    gap: "14px",
    marginTop: "12px",
  },
  idPreview: {
    fontSize: "12px",
    color: "rgba(99,179,237,0.6)",
    fontFamily: "'JetBrains Mono', monospace",
    marginTop: "4px",
  },
  customAgentCard: {
    display: "flex",
    alignItems: "center",
    justifyContent: "space-between",
    ...shorthands.padding("12px", "16px"),
    ...shorthands.borderRadius("10px"),
    border: "1px solid rgba(255,255,255,0.05)",
    backgroundColor: "rgba(255,255,255,0.015)",
    marginBottom: "8px",
  },
  customAgentInfo: {
    display: "flex",
    alignItems: "center",
    gap: "10px",
  },
  customAgentName: {
    fontSize: "14px",
    fontWeight: 600,
    color: "#e2e8f0",
  },
  customAgentId: {
    fontSize: "12px",
    color: "rgba(148,163,184,0.5)",
    fontFamily: "'JetBrains Mono', monospace",
  },
});

function toKebabCase(name: string): string {
  return name
    .replace(/[^a-zA-Z0-9\s_-]/g, "")
    .trim()
    .toLowerCase()
    .replace(/[\s_]+/g, "-")
    .replace(/-+/g, "-")
    .replace(/^-|-$/g, "");
}

interface CustomAgentsTabProps {
  customAgents: AgentDefinition[];
  onAgentsChanged: () => void;
}

export default function CustomAgentsTab({ customAgents, onAgentsChanged }: CustomAgentsTabProps) {
  const s = useStyles();
  const shared = useSettingsStyles();

  const [showCreateAgent, setShowCreateAgent] = useState(false);
  const [newAgentName, setNewAgentName] = useState("");
  const [newAgentPrompt, setNewAgentPrompt] = useState("");
  const [newAgentModel, setNewAgentModel] = useState("");
  const [creatingAgent, setCreatingAgent] = useState(false);
  const [createAgentError, setCreateAgentError] = useState<string | null>(null);

  const agentIdPreview = toKebabCase(newAgentName);

  const handleCreateAgent = useCallback(async () => {
    if (!newAgentName.trim() || !newAgentPrompt.trim()) return;
    setCreatingAgent(true);
    setCreateAgentError(null);
    try {
      await createCustomAgent({
        name: newAgentName.trim(),
        prompt: newAgentPrompt.trim(),
        model: newAgentModel.trim() || undefined,
      });
      setNewAgentName("");
      setNewAgentPrompt("");
      setNewAgentModel("");
      setShowCreateAgent(false);
      onAgentsChanged();
    } catch (err) {
      const msg = err instanceof Error ? err.message : "Failed to create agent";
      setCreateAgentError(msg);
    } finally {
      setCreatingAgent(false);
    }
  }, [newAgentName, newAgentPrompt, newAgentModel, onAgentsChanged]);

  const handleDeleteCustomAgent = useCallback(async (agentId: string) => {
    try {
      await deleteCustomAgent(agentId);
      onAgentsChanged();
    } catch {
      // silent
    }
  }, [onAgentsChanged]);

  return (
    <>
      <div className={shared.sectionTitle}>Custom Agents</div>

      {customAgents.map(agent => (
        <div key={agent.id} className={s.customAgentCard}>
          <div className={s.customAgentInfo}>
            <BotRegular style={{ color: "rgba(99,179,237,0.6)" }} />
            <span className={s.customAgentName}>{agent.name}</span>
            <span className={s.customAgentId}>{agent.id}</span>
          </div>
          <Button
            appearance="subtle"
            size="small"
            icon={<DeleteRegular />}
            onClick={() => handleDeleteCustomAgent(agent.id)}
            aria-label={`Delete ${agent.name}`}
          />
        </div>
      ))}

      {showCreateAgent ? (
        <div className={s.createAgentForm}>
          <div>
            <div className={shared.fieldLabel}>Agent Name</div>
            <input
              className={shared.inputField}
              placeholder="e.g. Purview Expert"
              value={newAgentName}
              onChange={e => setNewAgentName(e.target.value)}
              autoFocus
            />
            {agentIdPreview && (
              <div className={s.idPreview}>ID: {agentIdPreview}</div>
            )}
          </div>
          <div>
            <div className={shared.fieldLabel}>Agent Prompt (agent.md)</div>
            <textarea
              className={shared.textareaField}
              placeholder={"You are a specialist in...\n\nProvide guidance on..."}
              value={newAgentPrompt}
              onChange={e => setNewAgentPrompt(e.target.value)}
              rows={8}
            />
            <div className={shared.fieldHint}>
              Paste the full agent.md content — this becomes the agent's system prompt.
            </div>
          </div>
          <div>
            <div className={shared.fieldLabel}>Model (optional)</div>
            <input
              className={shared.inputField}
              placeholder="e.g. claude-sonnet-4.5 (leave empty for default)"
              value={newAgentModel}
              onChange={e => setNewAgentModel(e.target.value)}
            />
          </div>
          {createAgentError && (
            <div className={shared.errorText}>{createAgentError}</div>
          )}
          <div style={{ display: "flex", gap: 8, justifyContent: "flex-end" }}>
            <Button appearance="subtle" size="small" onClick={() => { setShowCreateAgent(false); setCreateAgentError(null); }} disabled={creatingAgent}>
              Cancel
            </Button>
            <Button appearance="primary" size="small" disabled={creatingAgent || !newAgentName.trim() || !newAgentPrompt.trim()} onClick={handleCreateAgent}>
              {creatingAgent ? <Spinner size="tiny" /> : "Create Agent"}
            </Button>
          </div>
        </div>
      ) : (
        <Button
          appearance="subtle"
          size="small"
          icon={<AddRegular />}
          onClick={() => setShowCreateAgent(true)}
          style={{ marginTop: customAgents.length > 0 ? 8 : 0 }}
        >
          Add Custom Agent
        </Button>
      )}

      {customAgents.length === 0 && !showCreateAgent && (
        <div className={shared.emptyState}>
          No custom agents yet. Add one to use domain-specific experts in your rooms.
        </div>
      )}
    </>
  );
}
