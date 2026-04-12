import { useCallback, useEffect, useState } from "react";
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
  BranchForkRegular,
} from "@fluentui/react-icons";
import {
  getNotificationProviders,
  disconnectProvider,
  getConfiguredAgents,
  getInstructionTemplates,
  type ProviderStatus,
  type AgentDefinition,
  type InstructionTemplate,
} from "./api";
import NotificationSetupWizard from "./NotificationSetupWizard";
import AgentConfigCard from "./AgentConfigCard";
import TemplateCard from "./TemplateCard";
import { CustomAgentsTab, GitHubTab, AdvancedTab } from "./settings";

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
});

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

  const [agents, setAgents] = useState<AgentDefinition[]>([]);
  const [templates, setTemplates] = useState<InstructionTemplate[]>([]);
  const [agentsLoading, setAgentsLoading] = useState(true);
  const [expandedAgent, setExpandedAgent] = useState<string | null>(null);
  const [expandedTemplate, setExpandedTemplate] = useState<string | null>(null);
  const [showNewTemplate, setShowNewTemplate] = useState(false);

  const fetchProviders = useCallback(async () => {
    try {
      const data = await getNotificationProviders();
      setProviders(data);
    } catch {
      // Silent
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

  useEffect(() => {
    fetchProviders();
    fetchAgentsAndTemplates();
  }, [fetchProviders, fetchAgentsAndTemplates]);

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

  useEffect(() => {
    const handleKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") onClose();
    };
    window.addEventListener("keydown", handleKey);
    return () => window.removeEventListener("keydown", handleKey);
  }, [onClose]);

  const builtInAgents = agents.filter(a => a.role !== "Custom");
  const customAgents = agents.filter(a => a.role === "Custom");
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

        <div className={s.tabContent}>
          <div className={s.tabContentInner}>

            {activeTab === "custom-agents" && (
              <CustomAgentsTab
                customAgents={customAgents}
                onAgentsChanged={fetchAgentsAndTemplates}
              />
            )}

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

            {activeTab === "github" && <GitHubTab />}

            {activeTab === "advanced" && (
              <AdvancedTab desktopNotifications={desktopNotifications} />
            )}

          </div>
        </div>
      </div>
    </div>
  );
}
