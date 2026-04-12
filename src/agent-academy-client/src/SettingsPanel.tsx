import { useCallback, useEffect, useRef, useState } from "react";
import type { ReactNode } from "react";
import {
  Button,
  Spinner,
  makeStyles,
  shorthands,
  mergeClasses,
} from "@fluentui/react-components";
import V3Badge from "./V3Badge";
import {
  DismissRegular,
  PlugConnectedRegular,
  PlugDisconnectedRegular,
  AlertRegular,
  BotRegular,
  DocumentTextRegular,
  AddRegular,
  SettingsRegular,
  PersonAddRegular,
  DeleteRegular,
  BranchForkRegular,
  CheckmarkCircleRegular,
  ErrorCircleRegular,
  WarningRegular,
  ArrowSyncRegular,
  OpenRegular,
} from "@fluentui/react-icons";
import {
  getNotificationProviders,
  disconnectProvider,
  getConfiguredAgents,
  getInstructionTemplates,
  getSystemSettings,
  updateSystemSettings,
  createCustomAgent,
  deleteCustomAgent,
  getGitHubStatus,
  type ProviderStatus,
  type AgentDefinition,
  type InstructionTemplate,
  type GitHubStatus,
} from "./api";
import NotificationSetupWizard from "./NotificationSetupWizard";
import AgentConfigCard from "./AgentConfigCard";
import TemplateCard from "./TemplateCard";

// ── Styles ──────────────────────────────────────────────────────────────

const useLocalStyles = makeStyles({
  overlay: {
    position: "fixed",
    inset: "0",
    zIndex: 100,
    display: "flex",
    flexDirection: "column",
    background: "linear-gradient(168deg, #0a0e1a 0%, #0d1321 40%, #111827 100%)",
    overflow: "hidden",
  },
  header: {
    display: "flex",
    alignItems: "center",
    justifyContent: "space-between",
    ...shorthands.padding("20px", "32px"),
    borderBottom: "1px solid rgba(255,255,255,0.06)",
    flexShrink: 0,
  },
  titleRow: {
    display: "flex",
    alignItems: "baseline",
    gap: "12px",
  },
  title: {
    fontSize: "20px",
    fontWeight: 700,
    color: "#e2e8f0",
    margin: "0",
    letterSpacing: "-0.3px",
  },
  titleAccent: {
    fontSize: "12px",
    color: "rgba(99, 179, 237, 0.5)",
    fontFamily: "'JetBrains Mono', monospace",
  },
  /* Tabbed layout */
  layout: {
    display: "flex",
    flex: 1,
    overflow: "hidden",
  },
  tabSidebar: {
    width: "200px",
    flexShrink: 0,
    borderRight: "1px solid rgba(255,255,255,0.06)",
    ...shorthands.padding("16px", "0"),
    display: "flex",
    flexDirection: "column",
    gap: "2px",
  },
  tabButton: {
    display: "flex",
    alignItems: "center",
    gap: "10px",
    ...shorthands.padding("10px", "24px"),
    border: "none",
    background: "transparent",
    color: "rgba(148,163,184,0.7)",
    fontSize: "13px",
    fontWeight: 500,
    cursor: "pointer",
    textAlign: "left" as const,
    borderRight: "2px solid transparent",
    transition: "all 0.12s ease",
    ":hover": {
      color: "#e2e8f0",
      background: "rgba(255,255,255,0.03)",
    },
  },
  tabButtonActive: {
    color: "#e2e8f0",
    background: "rgba(99,179,237,0.06)",
    borderRightColor: "rgba(99,179,237,0.6)",
    fontWeight: 600,
  },
  tabIcon: {
    fontSize: "15px",
    flexShrink: 0,
  },
  tabContent: {
    flex: 1,
    overflowY: "auto",
    ...shorthands.padding("28px", "36px", "36px"),
  },
  tabContentInner: {
    maxWidth: "640px",
  },
  sectionTitle: {
    fontSize: "13px",
    fontWeight: 600,
    color: "#e2e8f0",
    marginBottom: "20px",
    textTransform: "uppercase" as const,
    letterSpacing: "1px",
  },
  /* Cards */
  providerCard: {
    ...shorthands.padding("14px", "16px"),
    ...shorthands.borderRadius("10px"),
    ...shorthands.borderStyle("solid"),
    ...shorthands.borderWidth("1px"),
    ...shorthands.borderColor("rgba(255,255,255,0.05)"),
    backgroundColor: "rgba(255,255,255,0.015)",
    marginBottom: "10px",
    transition: "border-color 0.15s ease",
    ":hover": { ...shorthands.borderColor("rgba(255,255,255,0.1)") },
  },
  providerHeader: {
    display: "flex",
    alignItems: "center",
    justifyContent: "space-between",
    gap: "12px",
  },
  providerInfo: {
    display: "flex",
    alignItems: "center",
    gap: "10px",
  },
  providerName: {
    fontSize: "14px",
    fontWeight: 600,
    color: "#e2e8f0",
  },
  providerActions: {
    display: "flex",
    alignItems: "center",
    gap: "8px",
  },
  wizardContainer: {
    marginTop: "16px",
    ...shorthands.borderTop("1px", "solid", "rgba(255,255,255,0.06)"),
    paddingTop: "16px",
  },
  emptyState: {
    fontSize: "13px",
    color: "rgba(148,163,184,0.5)",
    ...shorthands.padding("20px"),
    textAlign: "center" as const,
    fontStyle: "italic",
  },
  /* Custom agent form */
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
  inputField: {
    ...shorthands.padding("10px", "14px"),
    ...shorthands.borderRadius("8px"),
    border: "1px solid rgba(255,255,255,0.08)",
    background: "rgba(0,0,0,0.3)",
    color: "#e2e8f0",
    fontSize: "14px",
    fontFamily: "inherit",
    outline: "none",
    width: "100%",
    boxSizing: "border-box" as const,
  },
  textareaField: {
    ...shorthands.padding("12px", "14px"),
    ...shorthands.borderRadius("8px"),
    border: "1px solid rgba(255,255,255,0.08)",
    background: "rgba(0,0,0,0.3)",
    color: "#e2e8f0",
    fontSize: "13px",
    fontFamily: "'JetBrains Mono', 'Fira Code', monospace",
    lineHeight: "1.6",
    outline: "none",
    width: "100%",
    boxSizing: "border-box" as const,
    resize: "vertical" as const,
    minHeight: "140px",
  },
  fieldLabel: {
    fontSize: "11px",
    fontWeight: 600,
    color: "rgba(148,163,184,0.7)",
    textTransform: "uppercase" as const,
    letterSpacing: "0.8px",
    marginBottom: "4px",
  },
  fieldHint: {
    fontSize: "11px",
    color: "rgba(148,163,184,0.4)",
    marginTop: "4px",
  },
  idPreview: {
    fontSize: "12px",
    color: "rgba(99,179,237,0.6)",
    fontFamily: "'JetBrains Mono', monospace",
    marginTop: "4px",
  },
  errorText: {
    fontSize: "13px",
    color: "#f87171",
    ...shorthands.padding("8px", "12px"),
    ...shorthands.borderRadius("6px"),
    background: "rgba(248,113,113,0.08)",
    border: "1px solid rgba(248,113,113,0.15)",
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

// ── Helpers ─────────────────────────────────────────────────────────────

function toKebabCase(name: string): string {
  return name
    .replace(/[^a-zA-Z0-9\s_-]/g, "")
    .trim()
    .toLowerCase()
    .replace(/[\s_]+/g, "-")
    .replace(/-+/g, "-")
    .replace(/^-|-$/g, "");
}

// ── Component ───────────────────────────────────────────────────────────

import type { DesktopNotificationControls } from "./useDesktopNotifications";

interface SettingsPanelProps {
  onClose: () => void;
  desktopNotifications?: DesktopNotificationControls;
}

export default function SettingsPanel({ onClose, desktopNotifications }: SettingsPanelProps) {
  const s = useLocalStyles();
  const [providers, setProviders] = useState<ProviderStatus[]>([]);
  const [loading, setLoading] = useState(true);
  const [expandedProvider, setExpandedProvider] = useState<string | null>(null);
  const [disconnecting, setDisconnecting] = useState<string | null>(null);

  // Agent config state
  const [agents, setAgents] = useState<AgentDefinition[]>([]);
  const [templates, setTemplates] = useState<InstructionTemplate[]>([]);
  const [agentsLoading, setAgentsLoading] = useState(true);
  const [expandedAgent, setExpandedAgent] = useState<string | null>(null);
  const [expandedTemplate, setExpandedTemplate] = useState<string | null>(null);
  const [showNewTemplate, setShowNewTemplate] = useState(false);

  // Custom agent creation
  const [showCreateAgent, setShowCreateAgent] = useState(false);
  const [newAgentName, setNewAgentName] = useState("");
  const [newAgentPrompt, setNewAgentPrompt] = useState("");
  const [newAgentModel, setNewAgentModel] = useState("");
  const [creatingAgent, setCreatingAgent] = useState(false);
  const [createAgentError, setCreateAgentError] = useState<string | null>(null);

  // Advanced settings state
  const [mainRoomEpochSize, setMainRoomEpochSize] = useState("50");
  const [breakoutEpochSize, setBreakoutEpochSize] = useState("30");
  const [settingsSaving, setSettingsSaving] = useState(false);
  const [settingsSaved, setSettingsSaved] = useState(false);

  // GitHub integration state
  const [ghStatus, setGhStatus] = useState<GitHubStatus | null>(null);
  const [ghLoading, setGhLoading] = useState(true);
  const [ghError, setGhError] = useState<string | null>(null);
  const [ghRefreshing, setGhRefreshing] = useState(false);
  const ghRequestSeq = useRef(0);

  const fetchProviders = useCallback(async () => {
    try {
      const data = await getNotificationProviders();
      setProviders(data);
    } catch {
      // Silent — providers list is non-critical
    } finally {
      setLoading(false);
    }
  }, []);

  const fetchAgentsAndTemplates = useCallback(async () => {
    const results = await Promise.allSettled([
      getConfiguredAgents(),
      getInstructionTemplates(),
    ]);

    if (results[0].status === "fulfilled") setAgents(results[0].value);
    if (results[1].status === "fulfilled") setTemplates(results[1].value);

    setAgentsLoading(false);
  }, []);

  const fetchGitHubStatus = useCallback(async (isRefresh = false) => {
    const seq = ++ghRequestSeq.current;
    if (isRefresh) setGhRefreshing(true);
    setGhError(null);
    try {
      const data = await getGitHubStatus();
      if (seq !== ghRequestSeq.current) return;
      setGhStatus(data);
    } catch (err) {
      if (seq !== ghRequestSeq.current) return;
      setGhError(err instanceof Error ? err.message : "Failed to check GitHub status");
    } finally {
      if (seq === ghRequestSeq.current) {
        setGhLoading(false);
        setGhRefreshing(false);
      }
    }
  }, []);

  useEffect(() => {
    fetchProviders();
    fetchAgentsAndTemplates();
    fetchGitHubStatus();
    getSystemSettings()
      .then((s) => {
        if (s["conversation.mainRoomEpochSize"])
          setMainRoomEpochSize(s["conversation.mainRoomEpochSize"]);
        if (s["conversation.breakoutEpochSize"])
          setBreakoutEpochSize(s["conversation.breakoutEpochSize"]);
      })
      .catch(() => {});
  }, [fetchProviders, fetchAgentsAndTemplates, fetchGitHubStatus]);

  const handleSaveSettings = useCallback(async () => {
    setSettingsSaving(true);
    try {
      await updateSystemSettings({
        "conversation.mainRoomEpochSize": mainRoomEpochSize,
        "conversation.breakoutEpochSize": breakoutEpochSize,
      });
      setSettingsSaved(true);
      setTimeout(() => setSettingsSaved(false), 2000);
    } catch {
      // Error handling in API layer
    } finally {
      setSettingsSaving(false);
    }
  }, [mainRoomEpochSize, breakoutEpochSize]);

  const handleDisconnect = useCallback(async (providerId: string) => {
    setDisconnecting(providerId);
    try {
      await disconnectProvider(providerId);
      await fetchProviders();
    } catch {
      // Error handling is in the API layer
    } finally {
      setDisconnecting(null);
    }
  }, [fetchProviders]);

  const handleSetupComplete = useCallback(() => {
    setExpandedProvider(null);
    fetchProviders();
  }, [fetchProviders]);

  const handleAgentSaved = useCallback(() => {
    setExpandedAgent(null);
    fetchAgentsAndTemplates();
  }, [fetchAgentsAndTemplates]);

  const handleTemplateSaved = useCallback(() => {
    setExpandedTemplate(null);
    setShowNewTemplate(false);
    fetchAgentsAndTemplates();
  }, [fetchAgentsAndTemplates]);

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
      fetchAgentsAndTemplates();
    } catch (err) {
      const msg = err instanceof Error ? err.message : "Failed to create agent";
      setCreateAgentError(msg);
    } finally {
      setCreatingAgent(false);
    }
  }, [newAgentName, newAgentPrompt, newAgentModel, fetchAgentsAndTemplates]);

  const handleDeleteCustomAgent = useCallback(async (agentId: string) => {
    try {
      await deleteCustomAgent(agentId);
      fetchAgentsAndTemplates();
    } catch {
      // silent
    }
  }, [fetchAgentsAndTemplates]);

  // Close on Escape
  useEffect(() => {
    const handleKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") onClose();
    };
    window.addEventListener("keydown", handleKey);
    return () => window.removeEventListener("keydown", handleKey);
  }, [onClose]);

  const builtInAgents = agents.filter(a => a.role !== "Custom");
  const customAgents = agents.filter(a => a.role === "Custom");
  const agentIdPreview = toKebabCase(newAgentName);
  const [activeTab, setActiveTab] = useState("custom-agents");

  const TABS: { id: string; icon: ReactNode; label: string }[] = [
    { id: "custom-agents", icon: <PersonAddRegular />, label: "Custom Agents" },
    { id: "built-in", icon: <BotRegular />, label: "Built-in Agents" },
    { id: "templates", icon: <DocumentTextRegular />, label: "Templates" },
    { id: "notifications", icon: <AlertRegular />, label: "Notifications" },
    { id: "github", icon: <BranchForkRegular />, label: "GitHub" },
    { id: "advanced", icon: <SettingsRegular />, label: "Advanced" },
  ];

  return (
    <div className={s.overlay}>
      <div className={s.header}>
        <div className={s.titleRow}>
          <h1 className={s.title}>Settings</h1>
          <span className={s.titleAccent}>// configuration</span>
        </div>
        <Button
          appearance="subtle"
          icon={<DismissRegular />}
          onClick={onClose}
          aria-label="Close settings"
        />
      </div>

      <div className={s.layout}>
        {/* Tab sidebar */}
        <nav className={s.tabSidebar}>
          {TABS.map(tab => (
            <button
              key={tab.id}
              type="button"
              className={mergeClasses(s.tabButton, activeTab === tab.id && s.tabButtonActive)}
              onClick={() => setActiveTab(tab.id)}
            >
              <span className={s.tabIcon}>{tab.icon}</span>
              {tab.label}
            </button>
          ))}
        </nav>

        {/* Tab content */}
        <div className={s.tabContent}>
          <div className={s.tabContentInner}>

            {/* ── Custom Agents ─────────────────────────────── */}
            {activeTab === "custom-agents" && (
              <>
                <div className={s.sectionTitle}>Custom Agents</div>

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
                      <div className={s.fieldLabel}>Agent Name</div>
                      <input
                        className={s.inputField}
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
                      <div className={s.fieldLabel}>Agent Prompt (agent.md)</div>
                      <textarea
                        className={s.textareaField}
                        placeholder={"You are a specialist in...\n\nProvide guidance on..."}
                        value={newAgentPrompt}
                        onChange={e => setNewAgentPrompt(e.target.value)}
                        rows={8}
                      />
                      <div className={s.fieldHint}>
                        Paste the full agent.md content — this becomes the agent's system prompt.
                      </div>
                    </div>
                    <div>
                      <div className={s.fieldLabel}>Model (optional)</div>
                      <input
                        className={s.inputField}
                        placeholder="e.g. claude-sonnet-4.5 (leave empty for default)"
                        value={newAgentModel}
                        onChange={e => setNewAgentModel(e.target.value)}
                      />
                    </div>
                    {createAgentError && (
                      <div className={s.errorText}>{createAgentError}</div>
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
                  <div className={s.emptyState}>
                    No custom agents yet. Add one to use domain-specific experts in your rooms.
                  </div>
                )}
              </>
            )}

            {/* ── Built-in Agents ───────────────────────────── */}
            {activeTab === "built-in" && (
              <>
                <div className={s.sectionTitle}>Built-in Agents</div>
                {agentsLoading ? (
                  <Spinner size="small" label="Loading agents…" />
                ) : builtInAgents.length === 0 ? (
                  <div className={s.emptyState}>No agents configured.</div>
                ) : (
                  builtInAgents.map((agent) => (
                    <AgentConfigCard
                      key={agent.id}
                      agent={agent}
                      templates={templates}
                      expanded={expandedAgent === agent.id}
                      onToggle={() => setExpandedAgent(expandedAgent === agent.id ? null : agent.id)}
                      onSaved={handleAgentSaved}
                    />
                  ))
                )}
              </>
            )}

            {/* ── Templates ─────────────────────────────────── */}
            {activeTab === "templates" && (
              <>
                <div className={s.sectionTitle}>Instruction Templates</div>
                {agentsLoading ? (
                  <Spinner size="small" label="Loading templates…" />
                ) : (
                  <>
                    {templates.map((t) => (
                      <TemplateCard
                        key={t.id}
                        template={t}
                        expanded={expandedTemplate === t.id}
                        onToggle={() => setExpandedTemplate(expandedTemplate === t.id ? null : t.id)}
                        onSaved={handleTemplateSaved}
                      />
                    ))}
                    {showNewTemplate ? (
                      <TemplateCard isNew expanded onToggle={() => {}} onSaved={handleTemplateSaved} onCancelNew={() => setShowNewTemplate(false)} />
                    ) : (
                      <Button appearance="subtle" size="small" icon={<AddRegular />} onClick={() => setShowNewTemplate(true)}>
                        Create Template
                      </Button>
                    )}
                  </>
                )}
              </>
            )}

            {/* ── Notifications ─────────────────────────────── */}
            {activeTab === "notifications" && (
              <>
                <div className={s.sectionTitle}>Notification Providers</div>
                {loading ? (
                  <Spinner size="small" label="Loading providers…" />
                ) : providers.length === 0 ? (
                  <div className={s.emptyState}>No notification providers available.</div>
                ) : (
                  providers.map((p) => (
                    <div key={p.providerId} className={s.providerCard}>
                      <div className={s.providerHeader}>
                        <div className={s.providerInfo}>
                          <span className={s.providerName}>{p.displayName}</span>
                          {p.isConnected ? (
                            <span style={{ display: "inline-flex", alignItems: "center", gap: "4px" }}>
                              <PlugConnectedRegular style={{ fontSize: 14 }} />
                              <V3Badge color="ok">Connected</V3Badge>
                            </span>
                          ) : p.isConfigured ? (
                            <V3Badge color="warn">Configured</V3Badge>
                          ) : (
                            <span style={{ display: "inline-flex", alignItems: "center", gap: "4px" }}>
                              <PlugDisconnectedRegular style={{ fontSize: 14 }} />
                              <V3Badge color="info">Not set up</V3Badge>
                            </span>
                          )}
                        </div>
                        <div className={s.providerActions}>
                          {p.isConnected ? (
                            <Button appearance="subtle" size="small" disabled={disconnecting === p.providerId} onClick={() => handleDisconnect(p.providerId)}>
                              {disconnecting === p.providerId ? <Spinner size="tiny" /> : "Disconnect"}
                            </Button>
                          ) : (
                            <Button appearance="primary" size="small" onClick={() => setExpandedProvider(expandedProvider === p.providerId ? null : p.providerId)}>
                              {expandedProvider === p.providerId ? "Cancel" : "Set Up"}
                            </Button>
                          )}
                        </div>
                      </div>
                      {!p.isConnected && p.lastError && (
                        <div style={{ padding: "6px 12px", fontSize: 12, color: "var(--colorStatusDangerForeground1, #d13438)", background: "var(--colorStatusDangerBackground1, #fde7e9)", borderRadius: 4, margin: "4px 0" }}>
                          ⚠ {p.lastError}
                        </div>
                      )}
                      {expandedProvider === p.providerId && (
                        <div className={s.wizardContainer}>
                          <NotificationSetupWizard providerId={p.providerId} inline onClose={handleSetupComplete} />
                        </div>
                      )}
                    </div>
                  ))
                )}
              </>
            )}

            {/* ── GitHub ──────────────────────────────────── */}
            {activeTab === "github" && (
              <>
                <div className={s.sectionTitle}>GitHub Integration</div>

                {ghLoading ? (
                  <div style={{ display: "flex", alignItems: "center", gap: 10, padding: "20px 0" }}>
                    <Spinner size="tiny" />
                    <span style={{ color: "rgba(148,163,184,0.6)", fontSize: 13 }}>Checking GitHub status…</span>
                  </div>
                ) : ghError ? (
                  <div style={{
                    display: "flex", alignItems: "flex-start", gap: 10, padding: "16px",
                    background: "rgba(239,68,68,0.06)", borderRadius: 8, border: "1px solid rgba(239,68,68,0.15)",
                  }}>
                    <ErrorCircleRegular style={{ color: "#ef4444", fontSize: 18, flexShrink: 0, marginTop: 1 }} />
                    <div>
                      <div style={{ color: "#ef4444", fontWeight: 600, fontSize: 13, marginBottom: 4 }}>Connection Error</div>
                      <div style={{ color: "rgba(148,163,184,0.7)", fontSize: 13, lineHeight: 1.5 }}>{ghError}</div>
                      <Button appearance="subtle" size="small" style={{ marginTop: 8 }} disabled={ghRefreshing} onClick={() => fetchGitHubStatus(true)}>
                        {ghRefreshing ? <Spinner size="tiny" /> : "Retry"}
                      </Button>
                    </div>
                  </div>
                ) : ghStatus ? (
                  <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
                    {/* Status card */}
                    <div style={{
                      padding: "16px", borderRadius: 8, border: "1px solid rgba(255,255,255,0.06)",
                      background: "rgba(255,255,255,0.02)",
                    }}>
                      <div style={{ display: "flex", alignItems: "center", gap: 8, marginBottom: 14 }}>
                        {ghStatus.isConfigured ? (
                          <CheckmarkCircleRegular style={{ color: "#4ade80", fontSize: 18 }} />
                        ) : (
                          <ErrorCircleRegular style={{ color: "#f59e0b", fontSize: 18 }} />
                        )}
                        <span style={{ fontWeight: 600, fontSize: 14, color: "#e2e8f0" }}>
                          {ghStatus.isConfigured ? "Connected" : "Not Connected"}
                        </span>
                        <Button
                          appearance="subtle"
                          size="small"
                          icon={ghRefreshing ? <Spinner size="tiny" /> : <ArrowSyncRegular />}
                          onClick={() => fetchGitHubStatus(true)}
                          disabled={ghRefreshing}
                          style={{ marginLeft: "auto" }}
                          aria-label="Refresh GitHub status"
                        />
                      </div>

                      <div style={{ display: "grid", gridTemplateColumns: "140px 1fr", gap: "8px 12px" }}>
                        <span className={s.fieldLabel}>Repository</span>
                        <span style={{ color: "#e2e8f0", fontSize: 13, fontFamily: "'JetBrains Mono', monospace" }}>
                          {ghStatus.repository ?? "—"}
                        </span>
                        <span className={s.fieldLabel}>Auth source</span>
                        <span style={{ display: "flex", alignItems: "center", gap: 6 }}>
                          <span style={{
                            display: "inline-block", padding: "1px 8px", borderRadius: 4,
                            fontSize: 12, fontWeight: 600, fontFamily: "'JetBrains Mono', monospace",
                            background: ghStatus.authSource === "oauth" ? "rgba(74,222,128,0.12)" :
                              ghStatus.authSource === "cli" ? "rgba(99,179,237,0.12)" : "rgba(239,68,68,0.12)",
                            color: ghStatus.authSource === "oauth" ? "#4ade80" :
                              ghStatus.authSource === "cli" ? "#63b3ed" : "#ef4444",
                            border: `1px solid ${
                              ghStatus.authSource === "oauth" ? "rgba(74,222,128,0.2)" :
                              ghStatus.authSource === "cli" ? "rgba(99,179,237,0.2)" : "rgba(239,68,68,0.2)"
                            }`,
                          }}>
                            {ghStatus.authSource}
                          </span>
                        </span>
                      </div>
                    </div>

                    {/* Auth source explanation */}
                    {ghStatus.authSource === "oauth" && (
                      <div style={{
                        padding: "12px 16px", borderRadius: 8,
                        background: "rgba(74,222,128,0.04)", border: "1px solid rgba(74,222,128,0.1)",
                        display: "flex", alignItems: "flex-start", gap: 10,
                      }}>
                        <CheckmarkCircleRegular style={{ color: "#4ade80", fontSize: 16, flexShrink: 0, marginTop: 2 }} />
                        <div style={{ color: "rgba(148,163,184,0.7)", fontSize: 13, lineHeight: 1.5 }}>
                          Authenticated via browser OAuth. PR operations (create, review, merge) are
                          available through your GitHub session.
                        </div>
                      </div>
                    )}

                    {ghStatus.authSource === "cli" && (
                      <div style={{
                        padding: "12px 16px", borderRadius: 8,
                        background: "rgba(99,179,237,0.04)", border: "1px solid rgba(99,179,237,0.1)",
                        display: "flex", alignItems: "flex-start", gap: 10,
                      }}>
                        <WarningRegular style={{ color: "#63b3ed", fontSize: 16, flexShrink: 0, marginTop: 2 }} />
                        <div style={{ color: "rgba(148,163,184,0.7)", fontSize: 13, lineHeight: 1.5 }}>
                          Authenticated via server-side <code style={{
                            fontFamily: "'JetBrains Mono', monospace", fontSize: 12,
                            background: "rgba(99,179,237,0.1)", padding: "1px 4px", borderRadius: 3,
                          }}>gh auth login</code>. PR operations use the CLI session.
                          Log in via the browser to use your own OAuth token instead.
                        </div>
                      </div>
                    )}

                    {ghStatus.authSource === "none" && (
                      <div style={{
                        padding: "12px 16px", borderRadius: 8,
                        background: "rgba(239,68,68,0.04)", border: "1px solid rgba(239,68,68,0.1)",
                        display: "flex", alignItems: "flex-start", gap: 10,
                      }}>
                        <ErrorCircleRegular style={{ color: "#ef4444", fontSize: 16, flexShrink: 0, marginTop: 2 }} />
                        <div>
                          <div style={{ color: "rgba(148,163,184,0.7)", fontSize: 13, lineHeight: 1.5, marginBottom: 8 }}>
                            GitHub is not configured. PR operations (create, review, merge) are unavailable.
                            Log in via the browser or run{" "}
                            <code style={{
                              fontFamily: "'JetBrains Mono', monospace", fontSize: 12,
                              background: "rgba(239,68,68,0.08)", padding: "1px 4px", borderRadius: 3,
                            }}>gh auth login</code>{" "}
                            on the server to enable GitHub integration.
                          </div>
                          <Button
                            appearance="primary"
                            size="small"
                            icon={<OpenRegular />}
                            onClick={() => { window.location.href = "/api/auth/login"; }}
                          >
                            Login with GitHub
                          </Button>
                        </div>
                      </div>
                    )}

                    {/* Capabilities summary */}
                    <div>
                      <div style={{ fontWeight: 600, marginBottom: 10, color: "#e2e8f0", fontSize: 14 }}>
                        PR Capabilities
                      </div>
                      <div style={{ display: "grid", gridTemplateColumns: "1fr 1fr", gap: 8 }}>
                        {[
                          { label: "Create PRs", needs: ghStatus.isConfigured },
                          { label: "Post reviews", needs: ghStatus.isConfigured },
                          { label: "Merge PRs", needs: ghStatus.isConfigured },
                          { label: "Status sync", needs: ghStatus.isConfigured },
                        ].map((cap) => (
                          <div key={cap.label} style={{ display: "flex", alignItems: "center", gap: 6 }}>
                            {cap.needs ? (
                              <CheckmarkCircleRegular style={{ color: "#4ade80", fontSize: 14 }} />
                            ) : (
                              <ErrorCircleRegular style={{ color: "rgba(148,163,184,0.3)", fontSize: 14 }} />
                            )}
                            <span style={{ color: cap.needs ? "#e2e8f0" : "rgba(148,163,184,0.4)", fontSize: 13 }}>
                              {cap.label}
                            </span>
                          </div>
                        ))}
                      </div>
                    </div>
                  </div>
                ) : null}
              </>
            )}

            {/* ── Advanced ──────────────────────────────────── */}
            {activeTab === "advanced" && (
              <>
                <div className={s.sectionTitle}>Advanced Settings</div>
                <div style={{ fontWeight: 600, marginBottom: 10, color: "#e2e8f0", fontSize: 14 }}>
                  Conversation Epoch Management
                </div>
                <div style={{ marginBottom: 14, color: "rgba(148,163,184,0.6)", fontSize: 13, lineHeight: 1.5 }}>
                  When a room's message count exceeds the epoch size, the conversation is
                  summarized and a new session begins with clean context.
                </div>
                <div style={{ display: "flex", gap: 24, flexWrap: "wrap" }}>
                  <label style={{ display: "flex", flexDirection: "column", gap: 6 }}>
                    <span className={s.fieldLabel}>Main room</span>
                    <input type="number" min="10" max="500" value={mainRoomEpochSize} onChange={(e) => setMainRoomEpochSize(e.target.value)} className={s.inputField} style={{ width: 90 }} />
                  </label>
                  <label style={{ display: "flex", flexDirection: "column", gap: 6 }}>
                    <span className={s.fieldLabel}>Breakout room</span>
                    <input type="number" min="10" max="500" value={breakoutEpochSize} onChange={(e) => setBreakoutEpochSize(e.target.value)} className={s.inputField} style={{ width: 90 }} />
                  </label>
                </div>
                <div style={{ marginTop: 16, display: "flex", alignItems: "center", gap: 12 }}>
                  <Button appearance="primary" size="small" disabled={settingsSaving} onClick={handleSaveSettings}>
                    {settingsSaving ? <Spinner size="tiny" /> : "Save"}
                  </Button>
                  {settingsSaved && <span style={{ color: "#4ade80", fontSize: 13 }}>✓ Saved</span>}
                </div>

                {/* ── Desktop Notifications ──────────── */}
                <div style={{ fontWeight: 600, marginTop: 28, marginBottom: 10, color: "#e2e8f0", fontSize: 14 }}>
                  Desktop Notifications
                </div>
                <div style={{ marginBottom: 14, color: "rgba(148,163,184,0.6)", fontSize: 13, lineHeight: 1.5 }}>
                  Show browser notifications for agent messages, errors, and sprint events when the tab is in the background.
                </div>
                {desktopNotifications ? (
                  <div style={{ display: "flex", alignItems: "center", gap: 12 }}>
                    <label style={{ display: "flex", alignItems: "center", gap: 8, cursor: "pointer" }}>
                      <input
                        type="checkbox"
                        checked={desktopNotifications.enabled}
                        onChange={(e) => desktopNotifications.setEnabled(e.target.checked)}
                        disabled={desktopNotifications.permission === "denied" && !desktopNotifications.enabled}
                        style={{ accentColor: "#818cf8", width: 16, height: 16 }}
                      />
                      <span style={{ color: "#e2e8f0", fontSize: 13 }}>Enable desktop notifications</span>
                    </label>
                    {desktopNotifications.permission === "denied" && (
                      <span style={{ color: "#f87171", fontSize: 12 }}>Blocked by browser — reset in site settings</span>
                    )}
                    {desktopNotifications.permission === "unsupported" && (
                      <span style={{ color: "rgba(148,163,184,0.6)", fontSize: 12 }}>Not supported in this browser</span>
                    )}
                  </div>
                ) : (
                  <div style={{ color: "rgba(148,163,184,0.4)", fontSize: 13 }}>Not available</div>
                )}
              </>
            )}

          </div>
        </div>
      </div>
    </div>
  );
}
