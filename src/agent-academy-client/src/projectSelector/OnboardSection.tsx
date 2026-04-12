import { useCallback, useRef, useState } from "react";
import {
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Input,
  Spinner,
} from "@fluentui/react-components";
import { browseDirectory, onboardProject, scanProject } from "../api";
import type { BrowseResult, OnboardResult, ProjectScanResult } from "../api";
import { useProjectSelectorStyles } from "./projectSelectorStyles";

interface OnboardSectionProps {
  onProjectOnboarded: (result: OnboardResult) => void;
}

export default function OnboardSection({ onProjectOnboarded }: OnboardSectionProps) {
  const classes = useProjectSelectorStyles();
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
              <span className={classes.body1Strong}>{scanResult.projectName ?? scanResult.path}</span>
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
                      <span className={classes.body1Strong}>{scanResult.projectName ?? scannedPath.split("/").pop()}</span>
                      <span className={classes.caption1} style={{ display: "block", color: "var(--aa-muted)", marginTop: "4px" }}>
                        {scannedPath}
                      </span>
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
                            <span className={classes.body1Strong}>Existing specification found.</span>
                            <span className={classes.body1} style={{ display: "block", color: "var(--aa-muted)", marginTop: "4px" }}>
                              The agent team will anchor on the current specs/ directory during onboarding.
                            </span>
                          </>
                        ) : (
                          <>
                            <span className={classes.body1Strong}>No specification found — one will be generated automatically.</span>
                            <span className={classes.body1} style={{ display: "block", color: "var(--aa-muted)", marginTop: "4px" }}>
                              Agent Academy will inspect the codebase and create an initial spec set for review.
                            </span>
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
