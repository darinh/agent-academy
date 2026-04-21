import { useState, useCallback, useEffect } from "react";
import { mergeClasses, Button, Spinner } from "@fluentui/react-components";
import { EditRegular, CheckmarkRegular, DismissRegular } from "@fluentui/react-icons";
import type { CircuitBreakerState } from "./useCircuitBreakerPolling";

export interface HeaderModel {
  title: string;
  meta: string | null;
  showPhasePill: boolean;
  workspaceLimited: boolean;
  degradedEyebrow: string | null;
  circuitBreakerState: CircuitBreakerState;
  canRename?: boolean;
  onRename?: (newName: string) => Promise<void>;
}

export interface WorkspaceHeaderProps {
  model: HeaderModel;
  styles: Record<string, string>;
}

export default function WorkspaceHeader({ model, styles: s }: WorkspaceHeaderProps) {
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState(model.title);
  const [saving, setSaving] = useState(false);

  // Reset edit state when room/view changes
  useEffect(() => {
    setEditing(false);
    setDraft(model.title);
  }, [model.title]);

  const startEdit = useCallback(() => {
    setDraft(model.title);
    setEditing(true);
  }, [model.title]);

  const cancelEdit = useCallback(() => {
    setEditing(false);
    setDraft(model.title);
  }, [model.title]);

  const saveEdit = useCallback(async () => {
    const trimmed = draft.trim();
    if (!trimmed || trimmed === model.title) { cancelEdit(); return; }
    setSaving(true);
    try {
      await model.onRename?.(trimmed);
      setEditing(false);
    } catch {
      // stay in edit mode on error
    } finally {
      setSaving(false);
    }
  }, [draft, model.title, model.onRename, cancelEdit]);

  return (
    <div className={s.workspaceHeader}>
      <div className={s.workspaceHeaderBody}>
        <div className={s.workspaceHeaderTopRow}>
          {editing ? (
            <div style={{ display: "flex", alignItems: "center", gap: 6 }}>
              <input
                autoFocus
                value={draft}
                onChange={(e) => setDraft(e.target.value)}
                onKeyDown={(e) => {
                  if (e.key === "Enter") saveEdit();
                  if (e.key === "Escape") cancelEdit();
                }}
                style={{
                  fontSize: "inherit",
                  fontWeight: "inherit",
                  fontFamily: "inherit",
                  background: "var(--aa-surface, #1a1a2e)",
                  border: "1px solid var(--aa-border)",
                  borderRadius: 4,
                  color: "var(--aa-text-strong, #fff)",
                  padding: "2px 8px",
                  minWidth: 200,
                }}
                disabled={saving}
              />
              <Button
                size="small"
                appearance="subtle"
                icon={saving ? <Spinner size="tiny" /> : <CheckmarkRegular />}
                onClick={saveEdit}
                disabled={saving}
                title="Save"
              />
              <Button size="small" appearance="subtle" icon={<DismissRegular />} onClick={cancelEdit} disabled={saving} title="Cancel" />
            </div>
          ) : (
            <div style={{ display: "flex", alignItems: "center", gap: 6 }}>
              <div className={s.workspaceTitle}>{model.title}</div>
              {model.canRename && (
                <Button
                  size="small"
                  appearance="transparent"
                  icon={<EditRegular fontSize={14} />}
                  onClick={startEdit}
                  title="Rename room"
                  style={{ minWidth: "auto", padding: "2px 4px" }}
                />
              )}
            </div>
          )}
          {!editing && model.meta && (<>
            <span className={s.headerDivider} />
            <span className={s.workspaceMetaText}>{model.meta}</span>
          </>)}
          <div style={{ flex: 1 }} />
          <div className={s.workspaceHeaderSignals}>
            {model.workspaceLimited && (
              <div className={mergeClasses(s.workspaceSignal, s.workspaceSignalWarning)}>
                {model.degradedEyebrow ?? "Limited mode"}
              </div>
            )}
            {model.circuitBreakerState && model.circuitBreakerState !== "Closed" && (
              <div className={mergeClasses(s.workspaceSignal, s.workspaceSignalWarning)}>
                Circuit {model.circuitBreakerState === "Open" ? "open" : "probing"}
              </div>
            )}
            {model.showPhasePill && (
              <div className={s.phasePill}>
                <span className={s.phasePillDot} />
                Connected
              </div>
            )}
          </div>
        </div>
      </div>
    </div>
  );
}
