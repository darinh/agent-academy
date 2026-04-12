import { useCallback, useEffect, useRef, useState } from "react";
import {
  Button,
  Spinner,
} from "@fluentui/react-components";
import {
  CheckmarkCircleRegular,
  ErrorCircleRegular,
  WarningRegular,
  ArrowSyncRegular,
  OpenRegular,
} from "@fluentui/react-icons";
import { getGitHubStatus, type GitHubStatus } from "../api";
import { useSettingsStyles } from "./settingsStyles";

export default function GitHubTab() {
  const shared = useSettingsStyles();

  const [ghStatus, setGhStatus] = useState<GitHubStatus | null>(null);
  const [ghLoading, setGhLoading] = useState(true);
  const [ghError, setGhError] = useState<string | null>(null);
  const [ghRefreshing, setGhRefreshing] = useState(false);
  const ghRequestSeq = useRef(0);

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
    fetchGitHubStatus();
  }, [fetchGitHubStatus]);

  return (
    <>
      <div className={shared.sectionTitle}>GitHub Integration</div>

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
              <span className={shared.fieldLabel}>Repository</span>
              <span style={{ color: "#e2e8f0", fontSize: 13, fontFamily: "'JetBrains Mono', monospace" }}>
                {ghStatus.repository ?? "—"}
              </span>
              <span className={shared.fieldLabel}>Auth source</span>
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
  );
}
