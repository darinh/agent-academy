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
  BrowseResult,
  OnboardResult,
  ProjectScanResult,
  WorkspaceMeta,
} from "./api";

interface ProjectSelectorPageProps {
  onProjectSelected: (workspacePath: string) => void;
  onProjectOnboarded?: (result: OnboardResult) => void;
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

const useStyles = makeStyles({
  root: {
    minHeight: "100vh",
    display: "flex",
    flexDirection: "column",
    alignItems: "center",
    justifyContent: "center",
    color: "#eff5ff",
    background:
      "radial-gradient(circle at top left, rgba(65, 135, 255, 0.18), transparent 26%), radial-gradient(circle at top right, rgba(183, 148, 255, 0.14), transparent 24%), linear-gradient(180deg, #09111f 0%, #0b1425 100%)",
    ...shorthands.padding("40px", "24px"),
  },
  container: {
    width: "100%",
    maxWidth: "720px",
    display: "flex",
    flexDirection: "column",
    gap: "32px",
  },
  header: {
    textAlign: "center",
    display: "flex",
    flexDirection: "column",
    alignItems: "center",
    gap: "8px",
  },
  title: {
    fontSize: "32px",
    fontWeight: 760,
    letterSpacing: "-0.03em",
    background: "linear-gradient(135deg, #6cb6ff, #b794ff)",
    WebkitBackgroundClip: "text",
    WebkitTextFillColor: "transparent",
  },
  subtitle: { color: "#a1b3d2", fontSize: "14px", lineHeight: "1.6" },
  tabList: { justifyContent: "center" },
  panel: {
    border: "1px solid rgba(155, 176, 210, 0.16)",
    background: "linear-gradient(180deg, rgba(15, 23, 40, 0.98), rgba(10, 17, 31, 0.98))",
    boxShadow: "0 24px 60px rgba(0, 0, 0, 0.35)",
    ...shorthands.borderRadius("22px"),
    ...shorthands.padding("24px"),
  },
  placeholder: { display: "grid", placeItems: "center", minHeight: "160px", color: "#7c90b2", fontSize: "14px" },
  loadingWrap: { display: "grid", placeItems: "center", minHeight: "160px", gap: "12px" },
  workspaceList: { display: "grid", gap: "10px" },
  workspaceCard: {
    width: "100%",
    display: "grid",
    gridTemplateColumns: "40px 1fr auto",
    gap: "14px",
    alignItems: "center",
    border: "1px solid transparent",
    background: "transparent",
    color: "inherit",
    cursor: "pointer",
    textAlign: "left",
    ...shorthands.borderRadius("18px"),
    ...shorthands.padding("14px"),
    ":hover": {
      backgroundColor: "rgba(255, 255, 255, 0.03)",
      border: "1px solid rgba(255, 255, 255, 0.06)",
    },
  },
  workspaceIcon: {
    width: "40px",
    height: "40px",
    display: "grid",
    placeItems: "center",
    color: "#ffffff",
    fontSize: "14px",
    fontWeight: 760,
    background: "linear-gradient(135deg, #4f8cff, #7c5cff)",
    boxShadow: "0 8px 20px rgba(79, 140, 255, 0.25)",
    ...shorthands.borderRadius("14px"),
  },
  workspaceName: { fontSize: "14px", fontWeight: 650 },
  workspacePath: { marginTop: "2px", color: "#a1b3d2", fontSize: "12px", display: "flex", alignItems: "center", gap: "4px" },
  badge: {
    display: "inline-flex",
    alignItems: "center",
    fontSize: "10px",
    letterSpacing: "0.04em",
    color: "#b794ff",
    backgroundColor: "rgba(183, 148, 255, 0.12)",
    border: "1px solid rgba(183, 148, 255, 0.22)",
    ...shorthands.borderRadius("999px"),
    ...shorthands.padding("2px", "8px"),
  },
  form: { display: "grid", gap: "14px" },
  fieldLabel: { color: "#7c90b2", fontSize: "11px", letterSpacing: "0.08em", textTransform: "uppercase", marginBottom: "4px" },
  fieldInput: { width: "100%" },
  actionRow: { display: "flex", justifyContent: "flex-end", gap: "10px", marginTop: "4px" },
  scanResults: {
    display: "grid",
    gap: "10px",
    border: "1px solid rgba(108, 182, 255, 0.18)",
    backgroundColor: "rgba(108, 182, 255, 0.04)",
    ...shorthands.borderRadius("14px"),
    ...shorthands.padding("16px"),
  },
  scanRow: { display: "flex", alignItems: "center", gap: "8px", fontSize: "13px" },
  scanLabel: { color: "#7c90b2", minWidth: "90px", fontSize: "12px" },
  errorText: { color: "#ef4444", fontSize: "13px" },
  browserWrap: {
    border: "1px solid rgba(155, 176, 210, 0.16)",
    backgroundColor: "rgba(15, 23, 40, 0.6)",
    ...shorthands.borderRadius("14px"),
    ...shorthands.padding("12px"),
  },
  browserHeader: { display: "flex", alignItems: "center", gap: "8px", marginBottom: "10px", fontSize: "12px", color: "#a1b3d2", overflowWrap: "anywhere" },
  browserList: { display: "grid", gap: "2px", maxHeight: "260px", overflowY: "auto" },
  browserEntry: {
    display: "flex",
    alignItems: "center",
    gap: "8px",
    width: "100%",
    background: "transparent",
    border: "1px solid transparent",
    color: "#eff5ff",
    cursor: "pointer",
    textAlign: "left",
    fontSize: "13px",
    ...shorthands.borderRadius("8px"),
    ...shorthands.padding("6px", "10px"),
    ":hover": { backgroundColor: "rgba(255, 255, 255, 0.04)", border: "1px solid rgba(255, 255, 255, 0.08)" },
  },
  browserActions: { display: "flex", justifyContent: "flex-end", gap: "8px", marginTop: "10px" },
  dialogSurface: { backgroundColor: "#0f1728", border: "1px solid rgba(155, 176, 210, 0.16)", color: "#eff5ff", maxWidth: "480px" },
  dialogTitle: { color: "#eff5ff" },
  dialogSpecNote: {
    display: "flex",
    gap: "8px",
    fontSize: "13px",
    lineHeight: "1.6",
    backgroundColor: "rgba(108, 182, 255, 0.06)",
    border: "1px solid rgba(108, 182, 255, 0.18)",
    ...shorthands.borderRadius("10px"),
    ...shorthands.padding("12px"),
  },
  dialogSpecIcon: { fontSize: "16px", flexShrink: 0, lineHeight: "1.6" },
  dialogError: { color: "#ef4444", fontSize: "13px", marginTop: "8px" },
});

function LoadExistingSection({ onProjectSelected }: { onProjectSelected: (path: string) => void }) {
  const classes = useStyles();
  const [workspaces, setWorkspaces] = useState<WorkspaceMeta[]>([]);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    let cancelled = false;
    listWorkspaces()
      .then((ws) => { if (!cancelled) setWorkspaces(ws); })
      .catch(() => { if (!cancelled) setWorkspaces([]); })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, []);

  if (loading) {
    return (
      <div className={classes.loadingWrap}>
        <Spinner size="small" label="Loading projects\u2026" />
      </div>
    );
  }

  if (workspaces.length === 0) {
    return (
      <div className={classes.placeholder}>
        <Body1>No existing projects found. Onboard or create one below.</Body1>
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
        >
          <div className={classes.workspaceIcon}>
            {(ws.projectName ?? ws.path).charAt(0).toUpperCase()}
          </div>
          <div>
            <div className={classes.workspaceName}>{ws.projectName ?? ws.path.split("/").pop()}</div>
            <div className={classes.workspacePath}>{ws.path}</div>
          </div>
          <Caption1 style={{ color: "#7c90b2" }}>
            {ws.lastAccessedAt ? relativeTime(ws.lastAccessedAt) : ""}
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
      const selectedPath = browseResult.path;
      setDirPath(selectedPath);
      setBrowsing(false);
      setBrowseResult(null);
      setScanResult(null);
      setScannedPath("");
      doScan(selectedPath);
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
        <div className={classes.fieldLabel}>Directory Path</div>
        <div style={{ display: "flex", gap: "8px" }}>
          <Input
            className={classes.fieldInput}
            placeholder="/home/user/projects/my-project"
            value={dirPath}
            onChange={(_, data) => {
              setDirPath(data.value);
              if (scanResult && data.value.trim() !== scannedPath) {
                setScanResult(null);
                setScannedPath("");
              }
            }}
            onKeyDown={(e) => { if (e.key === "Enter") doScan(dirPath); }}
            contentAfter={
              <Button
                appearance="transparent"
                size="small"
                icon={<span>\ud83d\udd0d</span>}
                onClick={() => doScan(dirPath)}
                disabled={scanning || !dirPath.trim()}
              />
            }
          />
          <Button
            appearance="subtle"
            size="medium"
            onClick={() => browsing ? setBrowsing(false) : handleBrowse()}
            disabled={browseLoading}
          >
            {browsing ? "Close" : "Browse\u2026"}
          </Button>
        </div>
      </div>

      {browseLoading && !browsing && (
        <div className={classes.loadingWrap} style={{ minHeight: "60px" }}>
          <Spinner size="small" label="Loading directories\u2026" />
        </div>
      )}
      {browseError && <div className={classes.errorText}>{browseError}</div>}

      {browsing && browseResult && (
        <div className={classes.browserWrap}>
          <div className={classes.browserHeader}>
            <span style={{ fontWeight: 600, color: "#6cb6ff" }}>\ud83d\udcc1</span>
            <span style={{ flex: 1 }}>{browseResult.path}</span>
          </div>
          <div className={classes.browserList}>
            {browseResult.entries
              .filter((e) => e.type === "directory")
              .map((entry) => (
                <button
                  key={entry.path}
                  className={classes.browserEntry}
                  onClick={() => handleBrowse(entry.path)}
                  disabled={browseLoading}
                >
                  <span>\ud83d\udcc1</span>
                  <span>{entry.name}</span>
                </button>
              ))}
          </div>
          <div className={classes.browserActions}>
            <Button appearance="primary" size="small" onClick={handleSelectBrowsedDir}>
              Select This Directory
            </Button>
          </div>
        </div>
      )}

      {scanning && (
        <div className={classes.loadingWrap} style={{ minHeight: "80px" }}>
          <Spinner size="small" label="Scanning project\u2026" />
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
                <span className={classes.scanLabel}>Tech Stack</span>
                <div style={{ display: "flex", gap: "6px", flexWrap: "wrap" }}>
                  {scanResult.techStack.map((t) => (
                    <span key={t} className={classes.badge}>{t}</span>
                  ))}
                </div>
              </div>
            )}
            <div className={classes.scanRow}>
              <span className={classes.scanLabel}>Git</span>
              <span>
                {scanResult.isGitRepo
                  ? `\u2713 Git repo (${scanResult.gitBranch ?? "unknown branch"})`
                  : "Not a git repository"}
              </span>
            </div>
          </div>

          <div className={classes.actionRow}>
            <Button
              appearance="primary"
              icon={<span>\ud83d\ude80</span>}
              onClick={() => { setOnboardError(null); setDialogOpen(true); }}
            >
              Onboard Project
            </Button>
          </div>

          <Dialog open={dialogOpen} onOpenChange={(_, data) => { if (!onboarding) setDialogOpen(data.open); }}>
            <DialogSurface className={classes.dialogSurface}>
              <DialogBody>
                <DialogTitle className={classes.dialogTitle}>Onboard Project</DialogTitle>
                <DialogContent>
                  <div style={{ display: "grid", gap: "12px" }}>
                    <div>
                      <Body1Strong>{scanResult.projectName ?? scannedPath.split("/").pop()}</Body1Strong>
                      <Caption1 style={{ display: "block", color: "#a1b3d2", marginTop: "2px" }}>
                        {scannedPath}
                      </Caption1>
                    </div>
                    {scanResult.techStack.length > 0 && (
                      <div style={{ display: "flex", gap: "6px", flexWrap: "wrap" }}>
                        {scanResult.techStack.map((t) => (
                          <span key={t} className={classes.badge}>{t}</span>
                        ))}
                      </div>
                    )}
                    <div className={classes.dialogSpecNote}>
                      <span className={classes.dialogSpecIcon}>\ud83d\udcdd</span>
                      <div>
                        <Body1Strong>The agent team will analyze this codebase.</Body1Strong>
                        <Body1 style={{ display: "block", color: "#a1b3d2", marginTop: "4px" }}>
                          A project specification will be generated for review.
                        </Body1>
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
                    onClick={handleConfirmOnboard}
                    disabled={onboarding}
                    icon={onboarding ? <Spinner size="tiny" /> : undefined}
                  >
                    {onboarding ? "Onboarding\u2026" : "Onboard"}
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
        <div className={classes.fieldLabel}>Directory Path</div>
        <Input
          className={classes.fieldInput}
          placeholder="/home/user/projects/my-awesome-project"
          value={dirPath}
          onChange={(_, d) => setDirPath(d.value)}
          onKeyDown={(e) => { if (e.key === "Enter") handleCreate(); }}
        />
      </div>
      <div className={classes.actionRow}>
        <Button
          appearance="primary"
          icon={creating ? <Spinner size="tiny" /> : <span>\u2795</span>}
          onClick={handleCreate}
          disabled={!dirPath.trim() || creating}
        >
          {creating ? "Creating\u2026" : "Create & Open"}
        </Button>
      </div>
      {createError && <div className={classes.errorText}>{createError}</div>}
    </div>
  );
}

type SelectorTab = "existing" | "onboard" | "create";

export default function ProjectSelectorPage({ onProjectSelected, onProjectOnboarded }: ProjectSelectorPageProps) {
  const classes = useStyles();
  const [tab, setTab] = useState<SelectorTab>("onboard");

  const handleOnboarded = useCallback((result: OnboardResult) => {
    if (onProjectOnboarded) {
      onProjectOnboarded(result);
    } else {
      onProjectSelected(result.workspace.path);
    }
  }, [onProjectOnboarded, onProjectSelected]);

  return (
    <div className={classes.root}>
      <div className={classes.container}>
        <div className={classes.header}>
          <h1 className={classes.title}>Agent Academy</h1>
          <div className={classes.subtitle}>Multi-Agent Collaboration Platform</div>
        </div>

        <TabList
          className={classes.tabList}
          selectedValue={tab}
          onTabSelect={(_, data) => setTab(data.value as SelectorTab)}
        >
          <Tab value="existing" icon={<span>\ud83d\udcc2</span>}>Load Existing</Tab>
          <Tab value="onboard" icon={<span>\ud83d\udd0d</span>}>Onboard Project</Tab>
          <Tab value="create" icon={<span>\u2795</span>}>Create New</Tab>
        </TabList>

        <div className={classes.panel}>
          {tab === "existing" && <LoadExistingSection onProjectSelected={onProjectSelected} />}
          {tab === "onboard" && <OnboardSection onProjectOnboarded={handleOnboarded} />}
          {tab === "create" && <CreateSection onProjectOnboarded={handleOnboarded} />}
        </div>
      </div>
    </div>
  );
}
