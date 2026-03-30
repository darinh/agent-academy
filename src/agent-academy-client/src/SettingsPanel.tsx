import { useCallback, useEffect, useState } from "react";
import {
  Button,
  Badge,
  Spinner,
  makeStyles,
  shorthands,
} from "@fluentui/react-components";
import {
  DismissRegular,
  PlugConnectedRegular,
  PlugDisconnectedRegular,
  AlertRegular,
  BotRegular,
  DocumentTextRegular,
  AddRegular,
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

// ── Styles ──────────────────────────────────────────────────────────────

const useLocalStyles = makeStyles({
  overlay: {
    position: "fixed",
    inset: "0",
    zIndex: 100,
    display: "flex",
    flexDirection: "column",
    background:
      "radial-gradient(circle at top left, rgba(65, 135, 255, 0.18), transparent 26%), radial-gradient(circle at top right, rgba(183, 148, 255, 0.14), transparent 24%), linear-gradient(180deg, #09111f 0%, #0b1425 100%)",
    overflowY: "auto",
  },
  header: {
    display: "flex",
    alignItems: "center",
    justifyContent: "space-between",
    ...shorthands.padding("24px", "32px", "0"),
  },
  title: {
    fontSize: "22px",
    fontWeight: 700,
    color: "#eff5ff",
    margin: "0",
  },
  body: {
    ...shorthands.padding("24px", "32px", "32px"),
    maxWidth: "680px",
    width: "100%",
  },
  section: {
    marginBottom: "32px",
  },
  sectionTitle: {
    display: "flex",
    alignItems: "center",
    gap: "8px",
    fontSize: "15px",
    fontWeight: 600,
    color: "#eff5ff",
    marginBottom: "16px",
  },
  providerCard: {
    ...shorthands.padding("16px"),
    ...shorthands.borderRadius("12px"),
    border: "1px solid rgba(155, 176, 210, 0.16)",
    backgroundColor: "rgba(255, 255, 255, 0.03)",
    marginBottom: "12px",
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
    color: "#eff5ff",
  },
  providerActions: {
    display: "flex",
    alignItems: "center",
    gap: "8px",
  },
  wizardContainer: {
    marginTop: "16px",
    ...shorthands.borderTop("1px", "solid", "rgba(155, 176, 210, 0.12)"),
    paddingTop: "16px",
  },
  emptyState: {
    fontSize: "13px",
    color: "#7c90b2",
    ...shorthands.padding("12px"),
  },
});

// ── Component ───────────────────────────────────────────────────────────

interface SettingsPanelProps {
  onClose: () => void;
}

export default function SettingsPanel({ onClose }: SettingsPanelProps) {
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

  // Close on Escape
  useEffect(() => {
    const handleKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") onClose();
    };
    window.addEventListener("keydown", handleKey);
    return () => window.removeEventListener("keydown", handleKey);
  }, [onClose]);

  return (
    <div className={s.overlay}>
      <div className={s.header}>
        <h1 className={s.title}>Settings</h1>
        <Button
          appearance="subtle"
          icon={<DismissRegular />}
          onClick={onClose}
          aria-label="Close settings"
        />
      </div>

      <div className={s.body}>
        {/* ── Agents Section ───────────────────────────────── */}
        <div className={s.section}>
          <div className={s.sectionTitle}>
            <BotRegular />
            Agents
          </div>

          {agentsLoading ? (
            <Spinner size="small" label="Loading agents…" />
          ) : agents.length === 0 ? (
            <div className={s.emptyState}>
              No agents configured.
            </div>
          ) : (
            agents.map((agent) => (
              <AgentConfigCard
                key={agent.id}
                agent={agent}
                templates={templates}
                expanded={expandedAgent === agent.id}
                onToggle={() =>
                  setExpandedAgent(
                    expandedAgent === agent.id ? null : agent.id
                  )
                }
                onSaved={handleAgentSaved}
              />
            ))
          )}
        </div>

        {/* ── Instruction Templates Section ─────────────────── */}
        <div className={s.section}>
          <div className={s.sectionTitle}>
            <DocumentTextRegular />
            Instruction Templates
          </div>

          {agentsLoading ? (
            <Spinner size="small" label="Loading templates…" />
          ) : (
            <>
              {templates.map((t) => (
                <TemplateCard
                  key={t.id}
                  template={t}
                  expanded={expandedTemplate === t.id}
                  onToggle={() =>
                    setExpandedTemplate(
                      expandedTemplate === t.id ? null : t.id
                    )
                  }
                  onSaved={handleTemplateSaved}
                />
              ))}

              {showNewTemplate ? (
                <TemplateCard
                  isNew
                  expanded
                  onToggle={() => {}}
                  onSaved={handleTemplateSaved}
                  onCancelNew={() => setShowNewTemplate(false)}
                />
              ) : (
                <Button
                  appearance="subtle"
                  size="small"
                  icon={<AddRegular />}
                  onClick={() => setShowNewTemplate(true)}
                >
                  Create Template
                </Button>
              )}
            </>
          )}
        </div>

        {/* ── Notifications Section ─────────────────────────── */}
        <div className={s.section}>
          <div className={s.sectionTitle}>
            <AlertRegular />
            Notifications
          </div>

          {loading ? (
            <Spinner size="small" label="Loading providers…" />
          ) : providers.length === 0 ? (
            <div className={s.emptyState}>
              No notification providers available.
            </div>
          ) : (
            providers.map((p) => (
              <div key={p.providerId} className={s.providerCard}>
                <div className={s.providerHeader}>
                  <div className={s.providerInfo}>
                    <span className={s.providerName}>{p.displayName}</span>
                    {p.isConnected ? (
                      <Badge
                        appearance="filled"
                        color="success"
                        icon={<PlugConnectedRegular />}
                      >
                        Connected
                      </Badge>
                    ) : p.isConfigured ? (
                      <Badge appearance="filled" color="warning">
                        Configured
                      </Badge>
                    ) : (
                      <Badge
                        appearance="filled"
                        color="informative"
                        icon={<PlugDisconnectedRegular />}
                      >
                        Not set up
                      </Badge>
                    )}
                  </div>

                  <div className={s.providerActions}>
                    {p.isConnected ? (
                      <Button
                        appearance="subtle"
                        size="small"
                        disabled={disconnecting === p.providerId}
                        onClick={() => handleDisconnect(p.providerId)}
                      >
                        {disconnecting === p.providerId ? (
                          <Spinner size="tiny" />
                        ) : (
                          "Disconnect"
                        )}
                      </Button>
                    ) : (
                      <Button
                        appearance="primary"
                        size="small"
                        onClick={() =>
                          setExpandedProvider(
                            expandedProvider === p.providerId
                              ? null
                              : p.providerId
                          )
                        }
                      >
                        {expandedProvider === p.providerId
                          ? "Cancel"
                          : "Set Up"}
                      </Button>
                    )}
                  </div>
                </div>

                {expandedProvider === p.providerId && (
                  <div className={s.wizardContainer}>
                    {p.providerId === "discord" ? (
                      <NotificationSetupWizard
                        inline
                        onClose={handleSetupComplete}
                      />
                    ) : (
                      <div className={s.emptyState}>
                        Setup wizard not available for this provider.
                      </div>
                    )}
                  </div>
                )}
              </div>
            ))
          )}
        </div>
      </div>
    </div>
  );
}
