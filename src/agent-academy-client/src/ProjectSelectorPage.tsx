import { useCallback, useEffect, useRef, useState } from "react";
import {
  Body1,
  Body1Strong,
  Button,
  Caption1,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Input,
  makeStyles,
  shorthands,
  Spinner,
  Tab,
  TabList,
} from "@fluentui/react-components";
import {
  browseDirectory,
  listWorkspaces,
  onboardProject,
  scanProject,
} from "./api";
import type {
  AuthUser,
  BrowseResult,
  OnboardResult,
  ProjectScanResult,
  WorkspaceMeta,
} from "./api";
import UserBadge from "./UserBadge";

interface ProjectSelectorPageProps {
  onProjectSelected: (workspacePath: string) => void;
  onProjectOnboarded?: (result: OnboardResult) => void;
  user?: AuthUser | null;
  onLogout?: () => void;
}

function relativeTime(iso: string): string {
  const diff = Date.now() - new Date(iso).getTime();
  if (diff < 0) return "just now";
  const seconds = Math.floor(diff / 1000);
  if (seconds < 60) return `${seconds}s ago`;
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `${minutes}m ago`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours}h ago`;
  const days = Math.floor(hours / 24);
  return `${days}d ago`;
}

type SelectorTab = "existing" | "onboard" | "create";

const TAB_COPY: Record<SelectorTab, { kicker: string; title: string; description: string }> = {
  existing: {
    kicker: "Resume work",
    title: "Return to an active workspace",
    description: "Jump back into a project already known to Agent Academy and keep the current room context intact.",
  },
  onboard: {
    kicker: "Inspect before entering",
    title: "Scan a repository and onboard it cleanly",
    description: "Review the project shape first, then activate it with the right spec expectations and workspace metadata.",
  },
  create: {
    kicker: "Start from a fresh directory",
    title: "Open a new project path directly",
    description: "Point Agent Academy at a destination folder and create the workspace without leaving the client.",
  },
};

const RAIL_POINTS = [
  {
    label: "Collaboration",
    value: "Six specialists, one room",
    body: "Planning, implementation, review, validation, and specs stay visible in the same interface.",
  },
  {
    label: "Branch flow",
    value: "Task branches by default",
    body: "Work happens off develop so breakout rounds can ship incrementally without trashing the main line.",
  },
  {
    label: "Spec discipline",
    value: "Reality over aspiration",
    body: "Projects with missing specs get surfaced early so the system can onboard them deliberately.",
  },
];

const useStyles = makeStyles({
  root: {
    position: "relative",
    minHeight: "100vh",
    overflow: "hidden",
    ...shorthands.padding("36px", "24px"),
  },
  backdrop: {
    position: "absolute",
    inset: 0,
    background:
      "radial-gradient(circle at 12% 16%, rgba(91, 141, 239, 0.16), transparent 24%), radial-gradient(circle at 86% 14%, rgba(156, 39, 176, 0.12), transparent 20%), radial-gradient(circle at 66% 84%, rgba(0, 150, 136, 0.12), transparent 26%)",
    pointerEvents: "none",
  },
  container: {
    position: "relative",
    width: "min(1240px, 100%)",
    minHeight: "calc(100vh - 72px)",
    margin: "0 auto",
    display: "grid",
    gridTemplateColumns: "minmax(320px, 0.86fr) minmax(0, 1.14fr)",
    gap: "28px",
    alignItems: "stretch",
    "@media (max-width: 1080px)": {
      gridTemplateColumns: "1fr",
    },
  },
  rail: {
    display: "grid",
    alignContent: "space-between",
    gap: "24px",
    border: "1px solid var(--aa-border)",
    background:
      "linear-gradient(180deg, rgba(13, 22, 37, 0.88), rgba(8, 14, 24, 0.96))",
    boxShadow: "0 32px 90px rgba(0, 0, 0, 0.34)",
    ...shorthands.borderRadius("32px"),
    ...shorthands.padding("34px"),
  },
  railHeader: {
    display: "grid",
    gap: "16px",
  },
  railKicker: {
    display: "inline-flex",
    alignItems: "center",
    width: "fit-content",
    color: "var(--aa-cyan)",
    backgroundColor: "rgba(91, 141, 239, 0.12)",
    border: "1px solid rgba(91, 141, 239, 0.22)",
    ...shorthands.borderRadius("999px"),
    ...shorthands.padding("8px", "14px"),
    fontSize: "11px",
    fontWeight: 700,
    letterSpacing: "0.14em",
    textTransform: "uppercase",
  },
  railTitle: {
    margin: 0,
    color: "var(--aa-text-strong)",
    fontFamily: "var(--heading)",
    fontSize: "clamp(2.8rem, 5.5vw, 4.5rem)",
    lineHeight: 0.95,
    letterSpacing: "-0.05em",
  },
  railBody: {
    margin: 0,
    color: "var(--aa-muted)",
    fontSize: "16px",
    lineHeight: 1.8,
    maxWidth: "38rem",
  },
  railGrid: {
    display: "grid",
    gap: "14px",
  },
  railCard: {
    display: "grid",
    gap: "10px",
    border: "1px solid var(--aa-hairline)",
    background: "linear-gradient(180deg, rgba(255, 255, 255, 0.04), rgba(255, 255, 255, 0.02))",
    ...shorthands.borderRadius("24px"),
    ...shorthands.padding("18px"),
  },
  railCardLabel: {
    color: "var(--aa-cyan)",
    fontSize: "11px",
    fontWeight: 700,
    letterSpacing: "0.12em",
    textTransform: "uppercase",
  },
  railCardValue: {
    color: "var(--aa-text-strong)",
    fontSize: "18px",
    fontWeight: 700,
  },
  railCardBody: {
    color: "var(--aa-soft)",
    fontSize: "13px",
    lineHeight: 1.7,
  },
  railFootnote: {
    color: "var(--aa-soft)",
    fontSize: "12px",
    lineHeight: 1.8,
  },
  deck: {
    display: "grid",
    gridTemplateRows: "auto auto minmax(0, 1fr)",
    gap: "18px",
    border: "1px solid var(--aa-border)",
    background:
      "linear-gradient(180deg, rgba(18, 30, 48, 0.92), rgba(9, 15, 25, 0.98))",
    boxShadow: "0 32px 90px rgba(0, 0, 0, 0.38)",
    ...shorthands.borderRadius("32px"),
    ...shorthands.padding("28px"),
  },
  deckTop: {
    display: "flex",
    alignItems: "flex-start",
    justifyContent: "space-between",
    gap: "18px",
    "@media (max-width: 780px)": {
      flexDirection: "column",
    },
  },
  userWrap: {
    display: "flex",
    justifyContent: "flex-end",
  },
  deckHeader: {
    display: "grid",
    gap: "8px",
    maxWidth: "44rem",
  },
  deckKicker: {
    color: "var(--aa-cyan)",
    fontSize: "11px",
    fontWeight: 700,
    letterSpacing: "0.14em",
    textTransform: "uppercase",
  },
  deckTitle: {
    color: "var(--aa-text-strong)",
    fontFamily: "var(--heading)",
    fontSize: "clamp(2.1rem, 4vw, 3.1rem)",
    lineHeight: 1,
    letterSpacing: "-0.05em",
  },
  deckDescription: {
    color: "var(--aa-muted)",
    fontSize: "15px",
    lineHeight: 1.8,
  },
  tabList: {
    display: "grid",
    gridTemplateColumns: "repeat(3, minmax(0, 1fr))",
    gap: "10px",
  },
  panel: {
    minHeight: 0,
    border: "1px solid var(--aa-border)",
    background: "linear-gradient(180deg, rgba(8, 13, 23, 0.78), rgba(10, 18, 30, 0.92))",
    ...shorthands.borderRadius("28px"),
    ...shorthands.padding("24px"),
  },
  placeholder: {
    display: "grid",
    placeItems: "center",
    minHeight: "180px",
    color: "var(--aa-soft)",
    fontSize: "14px",
  },
  loadingWrap: {
    display: "grid",
    placeItems: "center",
    gap: "12px",
    minHeight: "180px",
  },
  workspaceList: { display: "grid", gap: "12px" },
  workspaceCard: {
    width: "100%",
    display: "grid",
    gridTemplateColumns: "52px 1fr auto",
    gap: "16px",
    alignItems: "center",
    border: "1px solid var(--aa-hairline)",
    background: "linear-gradient(180deg, rgba(255, 255, 255, 0.03), rgba(255, 255, 255, 0.02))",
    color: "inherit",
    cursor: "pointer",
    textAlign: "left",
    boxShadow: "0 18px 40px rgba(0, 0, 0, 0.18)",
    transitionDuration: "160ms",
    transitionProperty: "transform, border-color, background-color",
    ...shorthands.borderRadius("22px"),
    ...shorthands.padding("16px"),
    ":hover": {
      transform: "translateY(-1px)",
      border: "1px solid rgba(91, 141, 239, 0.22)",
      background: "linear-gradient(180deg, rgba(91, 141, 239, 0.06), rgba(255, 255, 255, 0.03))",
    },
  },
  workspaceIcon: {
    width: "52px",
    height: "52px",
    display: "grid",
    placeItems: "center",
    color: "var(--aa-bg)",
    fontSize: "18px",
    fontWeight: 800,
    background: "linear-gradient(145deg, var(--aa-gold), var(--aa-cyan))",
    boxShadow: "0 12px 32px rgba(0, 0, 0, 0.22)",
    ...shorthands.borderRadius("18px"),
  },
  workspaceName: { color: "var(--aa-text-strong)", fontSize: "15px", fontWeight: 700 },
  workspacePath: {
    marginTop: "4px",
    color: "var(--aa-soft)",
    fontSize: "12px",
    lineHeight: 1.6,
    overflowWrap: "anywhere",
  },
  workspaceMeta: {
    color: "var(--aa-soft)",
    fontSize: "12px",
    whiteSpace: "nowrap",
  },
  badge: {
    display: "inline-flex",
    alignItems: "center",
    fontSize: "10px",
    fontWeight: 700,
    letterSpacing: "0.08em",
    textTransform: "uppercase",
    color: "var(--aa-text)",
    backgroundColor: "rgba(91, 141, 239, 0.1)",
    border: "1px solid rgba(91, 141, 239, 0.22)",
    ...shorthands.borderRadius("999px"),
    ...shorthands.padding("4px", "10px"),
  },
  form: { display: "grid", gap: "18px" },
  fieldLabel: {
    color: "var(--aa-soft)",
    fontSize: "11px",
    fontWeight: 700,
    letterSpacing: "0.12em",
    textTransform: "uppercase",
    marginBottom: "6px",
  },
  fieldInput: { width: "100%" },
  inlineField: {
    display: "grid",
    gridTemplateColumns: "minmax(0, 1fr) auto",
    gap: "10px",
    "@media (max-width: 640px)": {
      gridTemplateColumns: "1fr",
    },
  },
  actionRow: {
    display: "flex",
    justifyContent: "flex-end",
    gap: "10px",
    flexWrap: "wrap",
  },
  scanResults: {
    display: "grid",
    gap: "12px",
    border: "1px solid var(--aa-hairline)",
    background: "linear-gradient(180deg, rgba(91, 141, 239, 0.06), rgba(255, 255, 255, 0.03))",
    boxShadow: "0 18px 40px rgba(0, 0, 0, 0.16)",
    ...shorthands.borderRadius("22px"),
    ...shorthands.padding("18px"),
  },
  scanRow: {
    display: "flex",
    alignItems: "center",
    gap: "10px",
    flexWrap: "wrap",
    fontSize: "13px",
    color: "var(--aa-text)",
  },
  scanLabel: {
    color: "var(--aa-soft)",
    minWidth: "88px",
    fontSize: "11px",
    fontWeight: 700,
    letterSpacing: "0.1em",
    textTransform: "uppercase",
  },
  errorText: { color: "var(--aa-copper)", fontSize: "13px", lineHeight: 1.6 },
  browserWrap: {
    border: "1px solid var(--aa-hairline)",
    background: "linear-gradient(180deg, rgba(255, 255, 255, 0.03), rgba(255, 255, 255, 0.02))",
    ...shorthands.borderRadius("22px"),
    ...shorthands.padding("14px"),
  },
  browserHeader: {
    display: "flex",
    alignItems: "center",
    gap: "10px",
    marginBottom: "10px",
    fontSize: "12px",
    color: "var(--aa-muted)",
    overflowWrap: "anywhere",
  },
  browserList: { display: "grid", gap: "6px", maxHeight: "280px", overflowY: "auto" },
  browserEntry: {
    display: "flex",
    alignItems: "center",
    gap: "10px",
    width: "100%",
    background: "transparent",
    border: "1px solid transparent",
    color: "var(--aa-text-strong)",
    cursor: "pointer",
    textAlign: "left",
    fontSize: "13px",
    transitionDuration: "140ms",
    transitionProperty: "background-color, border-color",
    ...shorthands.borderRadius("14px"),
    ...shorthands.padding("10px", "12px"),
    ":hover": {
      backgroundColor: "rgba(255, 255, 255, 0.04)",
      border: "1px solid rgba(255, 255, 255, 0.08)",
    },
  },
  browserActions: { display: "flex", justifyContent: "flex-end", gap: "8px", marginTop: "12px" },
  dialogSurface: {
    backgroundColor: "var(--aa-bg)",
    border: "1px solid var(--aa-border)",
    color: "var(--aa-text-strong)",
    maxWidth: "520px",
  },
  dialogTitle: { color: "var(--aa-text-strong)", fontFamily: "var(--heading)" },
  dialogSpecNote: {
    display: "flex",
    gap: "10px",
    fontSize: "13px",
    lineHeight: "1.7",
    color: "var(--aa-text)",
    backgroundColor: "rgba(91, 141, 239, 0.06)",
    border: "1px solid rgba(91, 141, 239, 0.18)",
    ...shorthands.borderRadius("14px"),
    ...shorthands.padding("14px"),
  },
  dialogSpecIcon: { fontSize: "16px", flexShrink: 0, lineHeight: "1.6" },
  dialogError: { color: "var(--aa-copper)", fontSize: "13px", marginTop: "8px" },
});

function LoadExistingSection({ onProjectSelected }: { onProjectSelected: (path: string) => void }) {
  const classes = useStyles();
  const [workspaces, setWorkspaces] = useState<WorkspaceMeta[]>([]);
  const [loading, setLoading] = useState(true);
  const [fetchError, setFetchError] = useState(false);
  const [fetchKey, setFetchKey] = useState(0);

  useEffect(() => {
    let cancelled = false;
    setFetchError(false);
    setLoading(true);
    listWorkspaces()
      .then((ws) => {
        if (!cancelled) setWorkspaces(ws);
      })
      .catch(() => {
        if (!cancelled) { setWorkspaces([]); setFetchError(true); }
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, [fetchKey]);

  if (loading) {
    return (
      <div className={classes.loadingWrap}>
        <Spinner size="small" label="Loading workspaces…" />
      </div>
    );
  }

  if (fetchError) {
    return (
      <div className={classes.placeholder}>
        <Body1>Failed to load workspaces. Check your connection and try again.</Body1>
        <Button appearance="subtle" onClick={() => setFetchKey((k) => k + 1)} style={{ marginTop: "12px" }}>
          Retry
        </Button>
      </div>
    );
  }

  if (workspaces.length === 0) {
    return (
      <div className={classes.placeholder}>
        <Body1>No existing projects found yet. Onboard a repository or create a new workspace below.</Body1>
      </div>
    );
  }

  return (
    <div className={classes.workspaceList}>
      {workspaces.map((ws) => (
        <button
          key={ws.path}
          className={classes.workspaceCard}
          onClick={() => onProjectSelected(ws.path)}
          title={`Open ${ws.projectName ?? ws.path}`}
          type="button"
        >
          <div className={classes.workspaceIcon}>{(ws.projectName ?? ws.path).charAt(0).toUpperCase()}</div>
          <div>
            <div className={classes.workspaceName}>{ws.projectName ?? ws.path.split("/").pop()}</div>
            <div className={classes.workspacePath}>{ws.path}</div>
          </div>
          <Caption1 className={classes.workspaceMeta}>
            {ws.lastAccessedAt ? `Active ${relativeTime(ws.lastAccessedAt)}` : "New workspace"}
          </Caption1>
        </button>
      ))}
    </div>
  );
}

function OnboardSection({ onProjectOnboarded }: { onProjectOnboarded: (r: OnboardResult) => void }) {
  const classes = useStyles();
  const [dirPath, setDirPath] = useState("");
  const [scanning, setScanning] = useState(false);
  const [scanResult, setScanResult] = useState<ProjectScanResult | null>(null);
  const [scannedPath, setScannedPath] = useState("");
  const [scanError, setScanError] = useState<string | null>(null);
  const [browsing, setBrowsing] = useState(false);
  const [browseResult, setBrowseResult] = useState<BrowseResult | null>(null);
  const [browseLoading, setBrowseLoading] = useState(false);
  const [browseError, setBrowseError] = useState<string | null>(null);
  const [dialogOpen, setDialogOpen] = useState(false);
  const [onboarding, setOnboarding] = useState(false);
  const [onboardError, setOnboardError] = useState<string | null>(null);
  const scanNonceRef = useRef(0);

  const doScan = useCallback(async (pathToScan: string) => {
    const trimmed = pathToScan.trim();
    if (!trimmed) return;
    const nonce = ++scanNonceRef.current;
    setScanning(true);
    setScanResult(null);
    setScannedPath("");
    setScanError(null);
    try {
      const result = await scanProject(trimmed);
      if (nonce !== scanNonceRef.current) return;
      setScanResult(result);
      setScannedPath(trimmed);
    } catch (err) {
      if (nonce !== scanNonceRef.current) return;
      setScanError(err instanceof Error ? err.message : String(err));
    } finally {
      if (nonce === scanNonceRef.current) setScanning(false);
    }
  }, []);

  const handleBrowse = useCallback(async (targetPath?: string) => {
    setBrowseLoading(true);
    setBrowseError(null);
    try {
      const result = await browseDirectory(targetPath);
      setBrowseResult(result);
      setBrowsing(true);
    } catch (err) {
      setBrowseError(err instanceof Error ? err.message : String(err));
    } finally {
      setBrowseLoading(false);
    }
  }, []);

  const handleSelectBrowsedDir = useCallback(() => {
    if (browseResult) {
      const selectedPath = browseResult.current;
      setDirPath(selectedPath);
      setBrowsing(false);
      setBrowseResult(null);
      setScanResult(null);
      setScannedPath("");
      void doScan(selectedPath);
    }
  }, [browseResult, doScan]);

  const handleConfirmOnboard = useCallback(async () => {
    if (!scannedPath) return;
    setOnboarding(true);
    setOnboardError(null);
    try {
      const result = await onboardProject(scannedPath);
      setDialogOpen(false);
      onProjectOnboarded(result);
    } catch (err) {
      setOnboardError(err instanceof Error ? err.message : String(err));
    } finally {
      setOnboarding(false);
    }
  }, [scannedPath, onProjectOnboarded]);

  return (
    <div className={classes.form}>
      <div>
        <div className={classes.fieldLabel}>Directory path</div>
        <div className={classes.inlineField}>
          <Input
            className={classes.fieldInput}
            placeholder="/home/user/projects/my-project"
            aria-label="Directory path"
            value={dirPath}
            onChange={(_, data) => {
              setDirPath(data.value);
              if (scanResult && data.value.trim() !== scannedPath) {
                setScanResult(null);
                setScannedPath("");
              }
            }}
            onKeyDown={(e) => {
              if (e.key === "Enter") void doScan(dirPath);
            }}
            contentAfter={
              <Button
                appearance="transparent"
                size="small"
                onClick={() => void doScan(dirPath)}
                disabled={scanning || !dirPath.trim()}
              >
                Scan
              </Button>
            }
          />
          <Button
            appearance="subtle"
            size="medium"
            onClick={() => (browsing ? setBrowsing(false) : void handleBrowse())}
            disabled={browseLoading}
          >
            {browsing ? "Close browser" : "Browse directories"}
          </Button>
        </div>
      </div>

      {browseLoading && !browsing && (
        <div className={classes.loadingWrap} style={{ minHeight: "72px" }}>
          <Spinner size="small" label="Loading directories…" />
        </div>
      )}
      {browseError && <div className={classes.errorText}>{browseError}</div>}

      {browsing && browseResult && (
        <div className={classes.browserWrap}>
          <div className={classes.browserHeader}>
            <span style={{ color: "var(--aa-cyan)", fontWeight: 700 }}>Path</span>
            <span style={{ flex: 1 }}>{browseResult.current}</span>
          </div>
          <div className={classes.browserList}>
            {browseResult.parent && (
              <button
                className={classes.browserEntry}
                onClick={() => void handleBrowse(browseResult.parent ?? undefined)}
                disabled={browseLoading}
                type="button"
              >
                <span>↖</span>
                <span>Parent directory</span>
              </button>
            )}
            {browseResult.entries
              .filter((entry) => entry.isDirectory)
              .map((entry) => (
                <button
                  key={entry.path}
                  className={classes.browserEntry}
                  onClick={() => void handleBrowse(entry.path)}
                  disabled={browseLoading}
                  type="button"
                >
                  <span>▣</span>
                  <span>{entry.name}</span>
                </button>
              ))}
          </div>
          <div className={classes.browserActions}>
            <Button appearance="primary" size="small" onClick={handleSelectBrowsedDir}>
              Select this directory
            </Button>
          </div>
        </div>
      )}

      {scanning && (
        <div className={classes.loadingWrap} style={{ minHeight: "88px" }}>
          <Spinner size="small" label="Scanning project…" />
        </div>
      )}
      {scanError && <div className={classes.errorText}>{scanError}</div>}

      {scanResult && (
        <>
          <div className={classes.scanResults}>
            <div className={classes.scanRow}>
              <span className={classes.scanLabel}>Project</span>
              <Body1Strong>{scanResult.projectName ?? scanResult.path}</Body1Strong>
            </div>
            {scanResult.techStack.length > 0 && (
              <div className={classes.scanRow}>
                <span className={classes.scanLabel}>Stack</span>
                <div style={{ display: "flex", gap: "6px", flexWrap: "wrap" }}>
                  {scanResult.techStack.map((tech) => (
                    <span key={tech} className={classes.badge}>{tech}</span>
                  ))}
                </div>
              </div>
            )}
            <div className={classes.scanRow}>
              <span className={classes.scanLabel}>Git</span>
              <span>
                {scanResult.isGitRepo
                  ? `✓ Repository detected (${scanResult.gitBranch ?? "unknown branch"})`
                  : "No git repository detected"}
              </span>
            </div>
            <div className={classes.scanRow}>
              <span className={classes.scanLabel}>Specs</span>
              <span>{scanResult.hasSpecs ? "Existing spec set found" : "No specs found — onboarding will generate one"}</span>
            </div>
          </div>

          <div className={classes.actionRow}>
            <Button
              appearance="primary"
              onClick={() => {
                setOnboardError(null);
                setDialogOpen(true);
              }}
            >
              Onboard project
            </Button>
          </div>

          <Dialog open={dialogOpen} onOpenChange={(_, data) => { if (!onboarding) setDialogOpen(data.open); }}>
            <DialogSurface className={classes.dialogSurface}>
              <DialogBody>
                <DialogTitle className={classes.dialogTitle}>Onboard project</DialogTitle>
                <DialogContent>
                  <div style={{ display: "grid", gap: "12px" }}>
                    <div>
                      <Body1Strong>{scanResult.projectName ?? scannedPath.split("/").pop()}</Body1Strong>
                      <Caption1 style={{ display: "block", color: "var(--aa-muted)", marginTop: "4px" }}>
                        {scannedPath}
                      </Caption1>
                    </div>
                    {scanResult.techStack.length > 0 && (
                      <div style={{ display: "flex", gap: "6px", flexWrap: "wrap" }}>
                        {scanResult.techStack.map((tech) => (
                          <span key={tech} className={classes.badge}>{tech}</span>
                        ))}
                      </div>
                    )}
                    <div className={classes.dialogSpecNote}>
                      <span className={classes.dialogSpecIcon}>{scanResult.hasSpecs ? "✓" : "✦"}</span>
                      <div>
                        {scanResult.hasSpecs ? (
                          <>
                            <Body1Strong>Existing specification found.</Body1Strong>
                            <Body1 style={{ display: "block", color: "var(--aa-muted)", marginTop: "4px" }}>
                              The agent team will anchor on the current specs/ directory during onboarding.
                            </Body1>
                          </>
                        ) : (
                          <>
                            <Body1Strong>No specification found — one will be generated automatically.</Body1Strong>
                            <Body1 style={{ display: "block", color: "var(--aa-muted)", marginTop: "4px" }}>
                              Agent Academy will inspect the codebase and create an initial spec set for review.
                            </Body1>
                          </>
                        )}
                      </div>
                    </div>
                    {onboardError && <div className={classes.dialogError}>{onboardError}</div>}
                  </div>
                </DialogContent>
                <DialogActions>
                  <Button appearance="secondary" onClick={() => setDialogOpen(false)} disabled={onboarding}>
                    Cancel
                  </Button>
                  <Button
                    appearance="primary"
                    onClick={() => void handleConfirmOnboard()}
                    disabled={onboarding}
                    icon={onboarding ? <Spinner size="tiny" /> : undefined}
                  >
                    {onboarding ? "Onboarding…" : "Onboard"}
                  </Button>
                </DialogActions>
              </DialogBody>
            </DialogSurface>
          </Dialog>
        </>
      )}
    </div>
  );
}

function CreateSection({ onProjectOnboarded }: { onProjectOnboarded: (r: OnboardResult) => void }) {
  const classes = useStyles();
  const [dirPath, setDirPath] = useState("");
  const [creating, setCreating] = useState(false);
  const [createError, setCreateError] = useState<string | null>(null);

  const handleCreate = useCallback(async () => {
    const trimmed = dirPath.trim();
    if (!trimmed) return;
    setCreating(true);
    setCreateError(null);
    try {
      const result = await onboardProject(trimmed);
      onProjectOnboarded(result);
    } catch (err) {
      setCreateError(err instanceof Error ? err.message : String(err));
    } finally {
      setCreating(false);
    }
  }, [dirPath, onProjectOnboarded]);

  return (
    <div className={classes.form}>
      <div>
        <div className={classes.fieldLabel}>Directory path</div>
        <Input
          className={classes.fieldInput}
          placeholder="/home/user/projects/my-awesome-project"
          aria-label="Directory path"
          value={dirPath}
          onChange={(_, data) => setDirPath(data.value)}
          onKeyDown={(e) => {
            if (e.key === "Enter") void handleCreate();
          }}
        />
      </div>
      <div className={classes.actionRow}>
        <Button
          appearance="primary"
          icon={creating ? <Spinner size="tiny" /> : undefined}
          onClick={() => void handleCreate()}
          disabled={!dirPath.trim() || creating}
        >
          {creating ? "Creating…" : "Create & open"}
        </Button>
      </div>
      {createError && <div className={classes.errorText}>{createError}</div>}
    </div>
  );
}

export default function ProjectSelectorPage({ onProjectSelected, onProjectOnboarded, user, onLogout }: ProjectSelectorPageProps) {
  const classes = useStyles();
  const [tab, setTab] = useState<SelectorTab>("onboard");
  const tabCopy = TAB_COPY[tab];
  const userName = user?.name ?? user?.login;

  const handleOnboarded = useCallback((result: OnboardResult) => {
    if (onProjectOnboarded) {
      onProjectOnboarded(result);
    } else {
      onProjectSelected(result.workspace.path);
    }
  }, [onProjectOnboarded, onProjectSelected]);

  return (
    <div className={classes.root}>
      <div className={classes.backdrop} />
      <div className={classes.container}>
        <section className={classes.rail}>
          <div className={classes.railHeader}>
            <div className={classes.railKicker}>Workspace staging</div>
            <h1 className={classes.railTitle}>Choose the project with intent.</h1>
            <p className={classes.railBody}>
              {userName
                ? `Welcome back, ${userName}. Bring an existing repository forward or onboard a new one without leaving the client.`
                : "Move from directory discovery into collaboration without a clunky handoff between tools."}
            </p>
          </div>

          <div className={classes.railGrid}>
            {RAIL_POINTS.map((point) => (
              <div key={point.label} className={classes.railCard}>
                <div className={classes.railCardLabel}>{point.label}</div>
                <div className={classes.railCardValue}>{point.value}</div>
                <div className={classes.railCardBody}>{point.body}</div>
              </div>
            ))}
          </div>

          <div className={classes.railFootnote}>
            The frontend now treats Copilot availability as a first-class state, so onboarding and workspace entry feel
            consistent with the rest of the application instead of bolted on.
          </div>
        </section>

        <section className={classes.deck}>
          <div className={classes.deckTop}>
            <div className={classes.deckHeader}>
              <div className={classes.deckKicker}>{tabCopy.kicker}</div>
              <div className={classes.deckTitle}>{tabCopy.title}</div>
              <div className={classes.deckDescription}>{tabCopy.description}</div>
            </div>
            {user && onLogout && (
              <div className={classes.userWrap}>
                <UserBadge user={user} onLogout={onLogout} />
              </div>
            )}
          </div>

          <TabList
            className={classes.tabList}
            selectedValue={tab}
            onTabSelect={(_, data) => setTab(data.value as SelectorTab)}
          >
            <Tab value="existing">Existing</Tab>
            <Tab value="onboard">Onboard</Tab>
            <Tab value="create">Create</Tab>
          </TabList>

          <div className={classes.panel}>
            {tab === "existing" && <LoadExistingSection onProjectSelected={onProjectSelected} />}
            {tab === "onboard" && <OnboardSection onProjectOnboarded={handleOnboarded} />}
            {tab === "create" && <CreateSection onProjectOnboarded={handleOnboarded} />}
          </div>
        </section>
      </div>
    </div>
  );
}
