import { useState, useCallback, useEffect } from "react";
import {
  Button,
  Spinner,
} from "@fluentui/react-components";
import { getSystemSettings, updateSystemSettings } from "../api";
import type { DesktopNotificationControls } from "../useDesktopNotifications";
import { useSettingsStyles } from "./settingsStyles";

interface AdvancedTabProps {
  desktopNotifications?: DesktopNotificationControls;
}

export default function AdvancedTab({ desktopNotifications }: AdvancedTabProps) {
  const shared = useSettingsStyles();

  const [mainRoomEpochSize, setMainRoomEpochSize] = useState("50");
  const [breakoutEpochSize, setBreakoutEpochSize] = useState("30");
  const [sprintAutoStart, setSprintAutoStart] = useState(false);
  const [settingsSaving, setSettingsSaving] = useState(false);
  const [settingsSaved, setSettingsSaved] = useState(false);

  useEffect(() => {
    getSystemSettings()
      .then((s) => {
        if (s["conversation.mainRoomEpochSize"])
          setMainRoomEpochSize(s["conversation.mainRoomEpochSize"]);
        if (s["conversation.breakoutEpochSize"])
          setBreakoutEpochSize(s["conversation.breakoutEpochSize"]);
        if (s["sprint.autoStartOnCompletion"])
          setSprintAutoStart(s["sprint.autoStartOnCompletion"] === "True" || s["sprint.autoStartOnCompletion"] === "true");
      })
      .catch(() => {});
  }, []);

  const handleSaveSettings = useCallback(async () => {
    setSettingsSaving(true);
    try {
      await updateSystemSettings({
        "conversation.mainRoomEpochSize": mainRoomEpochSize,
        "conversation.breakoutEpochSize": breakoutEpochSize,
        "sprint.autoStartOnCompletion": sprintAutoStart.toString(),
      });
      setSettingsSaved(true);
      setTimeout(() => setSettingsSaved(false), 2000);
    } catch {
      // Error handling in API layer
    } finally {
      setSettingsSaving(false);
    }
  }, [mainRoomEpochSize, breakoutEpochSize, sprintAutoStart]);

  return (
    <>
      <div className={shared.sectionTitle}>Advanced Settings</div>
      <div style={{ fontWeight: 600, marginBottom: 10, color: "#e2e8f0", fontSize: 14 }}>
        Conversation Epoch Management
      </div>
      <div style={{ marginBottom: 14, color: "rgba(148,163,184,0.6)", fontSize: 13, lineHeight: 1.5 }}>
        When a room's message count exceeds the epoch size, the conversation is
        summarized and a new session begins with clean context.
      </div>
      <div style={{ display: "flex", gap: 24, flexWrap: "wrap" }}>
        <label style={{ display: "flex", flexDirection: "column", gap: 6 }}>
          <span className={shared.fieldLabel}>Main room</span>
          <input type="number" min="10" max="500" value={mainRoomEpochSize} onChange={(e) => setMainRoomEpochSize(e.target.value)} className={shared.inputField} style={{ width: 90 }} />
        </label>
        <label style={{ display: "flex", flexDirection: "column", gap: 6 }}>
          <span className={shared.fieldLabel}>Breakout room</span>
          <input type="number" min="10" max="500" value={breakoutEpochSize} onChange={(e) => setBreakoutEpochSize(e.target.value)} className={shared.inputField} style={{ width: 90 }} />
        </label>
      </div>
      <div style={{ marginTop: 16, display: "flex", alignItems: "center", gap: 12 }}>
        <Button appearance="primary" size="small" disabled={settingsSaving} onClick={handleSaveSettings}>
          {settingsSaving ? <Spinner size="tiny" /> : "Save"}
        </Button>
        {settingsSaved && <span style={{ color: "#4ade80", fontSize: 13 }}>✓ Saved</span>}
      </div>

      {/* Sprint Automation */}
      <div style={{ fontWeight: 600, marginTop: 28, marginBottom: 10, color: "#e2e8f0", fontSize: 14 }}>
        Sprint Automation
      </div>
      <div style={{ marginBottom: 14, color: "rgba(148,163,184,0.6)", fontSize: 13, lineHeight: 1.5 }}>
        When enabled, a new sprint is automatically created after the current sprint completes.
        Overflow requirements from the completed sprint are carried into the new sprint.
      </div>
      <div style={{ display: "flex", alignItems: "center", gap: 12 }}>
        <label style={{ display: "flex", alignItems: "center", gap: 8, cursor: "pointer" }}>
          <input
            type="checkbox"
            checked={sprintAutoStart}
            onChange={(e) => setSprintAutoStart(e.target.checked)}
            style={{ accentColor: "#818cf8", width: 16, height: 16 }}
          />
          <span style={{ color: "#e2e8f0", fontSize: 13 }}>Auto-start next sprint on completion</span>
        </label>
      </div>

      {/* Desktop Notifications */}
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
  );
}
