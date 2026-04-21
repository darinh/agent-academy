import { useState, useCallback, useEffect, useRef } from "react";
import {
  Button,
  Spinner,
} from "@fluentui/react-components";
import { getSystemSettings, updateSystemSettings, getSprintSchedule, upsertSprintSchedule, deleteSprintSchedule } from "../api";
import type { SprintScheduleResponse } from "../api";
import type { DesktopNotificationControls } from "../useDesktopNotifications";
import { useSettingsStyles } from "./settingsStyles";

const COMMON_TIMEZONES = [
  "UTC",
  "America/New_York",
  "America/Chicago",
  "America/Denver",
  "America/Los_Angeles",
  "America/Anchorage",
  "Pacific/Honolulu",
  "America/Toronto",
  "America/Vancouver",
  "America/Sao_Paulo",
  "America/Argentina/Buenos_Aires",
  "Europe/London",
  "Europe/Paris",
  "Europe/Berlin",
  "Europe/Amsterdam",
  "Europe/Rome",
  "Europe/Madrid",
  "Europe/Moscow",
  "Asia/Istanbul",
  "Asia/Dubai",
  "Asia/Kolkata",
  "Asia/Shanghai",
  "Asia/Tokyo",
  "Asia/Seoul",
  "Asia/Singapore",
  "Australia/Sydney",
  "Australia/Melbourne",
  "Pacific/Auckland",
];

function detectBrowserTimezone(): string {
  try {
    return Intl.DateTimeFormat().resolvedOptions().timeZone;
  } catch {
    return "UTC";
  }
}

function buildTimezoneOptions(savedTz?: string): string[] {
  const set = new Set(COMMON_TIMEZONES);
  const browser = detectBrowserTimezone();
  set.add(browser);
  if (savedTz) set.add(savedTz);
  return Array.from(set).sort();
}

function formatScheduleTime(utcIso: string | null, tz: string): string {
  if (!utcIso) return "—";
  try {
    const d = new Date(utcIso);
    const local = d.toLocaleString(undefined, { timeZone: tz, dateStyle: "medium", timeStyle: "short" });
    const utcStr = d.toLocaleString(undefined, { timeZone: "UTC", dateStyle: "medium", timeStyle: "short" });
    return `${local} (${tz}) · ${utcStr} UTC`;
  } catch {
    return new Date(utcIso).toLocaleString();
  }
}

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

  // Sprint schedule state
  const [schedule, setSchedule] = useState<SprintScheduleResponse | null>(null);
  const [scheduleLoading, setScheduleLoading] = useState(true);
  const [scheduleError, setScheduleError] = useState<string | null>(null);
  const [scheduleSaving, setScheduleSaving] = useState(false);
  const [scheduleSaved, setScheduleSaved] = useState(false);
  const [scheduleDeleting, setScheduleDeleting] = useState(false);
  const [deleteConfirm, setDeleteConfirm] = useState(false);
  const [cronExpr, setCronExpr] = useState("");
  const [schedTz, setSchedTz] = useState(detectBrowserTimezone);
  const [schedEnabled, setSchedEnabled] = useState(true);

  const timersRef = useRef<ReturnType<typeof setTimeout>[]>([]);
  useEffect(() => () => timersRef.current.forEach(clearTimeout), []);
  const safeTimeout = useCallback((fn: () => void, ms: number) => {
    const id = setTimeout(fn, ms);
    timersRef.current.push(id);
  }, []);

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
      safeTimeout(() => setSettingsSaved(false), 2000);
    } catch {
      // Error handling in API layer
    } finally {
      setSettingsSaving(false);
    }
  }, [mainRoomEpochSize, breakoutEpochSize, sprintAutoStart]);

  // Load sprint schedule
  useEffect(() => {
    setScheduleLoading(true);
    getSprintSchedule()
      .then((s) => {
        setSchedule(s);
        if (s) {
          setCronExpr(s.cronExpression);
          setSchedTz(s.timeZoneId);
          setSchedEnabled(s.enabled);
        }
      })
      .catch(() => setScheduleError("Failed to load schedule"))
      .finally(() => setScheduleLoading(false));
  }, []);

  const handleSaveSchedule = useCallback(async () => {
    if (scheduleDeleting) return;
    setScheduleSaving(true);
    setScheduleError(null);
    try {
      const updated = await upsertSprintSchedule({
        cronExpression: cronExpr.trim(),
        timeZoneId: schedTz,
        enabled: schedEnabled,
      });
      setSchedule(updated);
      setScheduleSaved(true);
      safeTimeout(() => setScheduleSaved(false), 2000);
    } catch (err) {
      setScheduleError(err instanceof Error ? err.message : "Failed to save schedule");
    } finally {
      setScheduleSaving(false);
    }
  }, [cronExpr, schedTz, schedEnabled, scheduleDeleting]);

  const handleDeleteSchedule = useCallback(async () => {
    if (scheduleSaving) return;
    if (!deleteConfirm) {
      setDeleteConfirm(true);
      safeTimeout(() => setDeleteConfirm(false), 3000);
      return;
    }
    setDeleteConfirm(false);
    setScheduleDeleting(true);
    setScheduleError(null);
    try {
      await deleteSprintSchedule();
      setSchedule(null);
      setCronExpr("");
      setSchedTz(detectBrowserTimezone());
      setSchedEnabled(true);
    } catch (err) {
      setScheduleError(err instanceof Error ? err.message : "Failed to delete schedule");
    } finally {
      setScheduleDeleting(false);
    }
  }, [deleteConfirm, scheduleSaving]);

  const tzOptions = buildTimezoneOptions(schedule?.timeZoneId);
  const cronFieldCount = cronExpr.trim().split(/\s+/).length;
  const cronHint = cronExpr.trim() && cronFieldCount !== 5
    ? "Cron requires 5 fields: minute hour day month weekday"
    : null;
  const scheduleBusy = scheduleSaving || scheduleDeleting;

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

      {/* Sprint Schedule */}
      <div style={{ fontWeight: 600, marginTop: 28, marginBottom: 10, color: "#e2e8f0", fontSize: 14 }}>
        Sprint Schedule
      </div>
      <div style={{ marginBottom: 14, color: "rgba(148,163,184,0.6)", fontSize: 13, lineHeight: 1.5 }}>
        Configure a cron schedule to automatically start new sprints. Uses standard 5-field cron format:
        <span style={{ fontFamily: "'JetBrains Mono', 'Fira Code', monospace", color: "rgba(148,163,184,0.8)" }}>
          {" "}minute hour day month weekday
        </span>
      </div>

      {scheduleLoading ? (
        <div style={{ display: "flex", alignItems: "center", gap: 8, padding: "8px 0" }}>
          <Spinner size="tiny" /> <span style={{ color: "rgba(148,163,184,0.6)", fontSize: 13 }}>Loading schedule…</span>
        </div>
      ) : (
        <>
          <div style={{ display: "flex", gap: 24, flexWrap: "wrap", marginBottom: 12 }}>
            <label style={{ display: "flex", flexDirection: "column", gap: 6, flex: "1 1 200px", maxWidth: 280 }}>
              <span className={shared.fieldLabel}>Cron Expression</span>
              <input
                type="text"
                value={cronExpr}
                onChange={(e) => { setCronExpr(e.target.value); setScheduleError(null); }}
                placeholder="0 9 * * MON-FRI"
                className={shared.inputField}
                style={{ fontFamily: "'JetBrains Mono', 'Fira Code', monospace" }}
              />
              {cronHint && (
                <span style={{ color: "#fbbf24", fontSize: 11 }}>{cronHint}</span>
              )}
            </label>
            <label style={{ display: "flex", flexDirection: "column", gap: 6, flex: "1 1 200px", maxWidth: 280 }}>
              <span className={shared.fieldLabel}>Timezone</span>
              <select
                value={schedTz}
                onChange={(e) => setSchedTz(e.target.value)}
                className={shared.inputField}
                style={{ cursor: "pointer" }}
              >
                {tzOptions.map((tz) => (
                  <option key={tz} value={tz}>{tz.replace(/_/g, " ")}</option>
                ))}
              </select>
            </label>
          </div>

          <div style={{ display: "flex", alignItems: "center", gap: 12, marginBottom: 12 }}>
            <label style={{ display: "flex", alignItems: "center", gap: 8, cursor: "pointer" }}>
              <input
                type="checkbox"
                checked={schedEnabled}
                onChange={(e) => setSchedEnabled(e.target.checked)}
                style={{ accentColor: "#818cf8", width: 16, height: 16 }}
              />
              <span style={{ color: "#e2e8f0", fontSize: 13 }}>Schedule enabled</span>
            </label>
          </div>

          {schedule && (
            <div style={{ marginBottom: 12, fontSize: 12, color: "rgba(148,163,184,0.6)", lineHeight: 1.8 }}>
              {schedule.nextRunAtUtc && (
                <div><strong style={{ color: "rgba(148,163,184,0.8)" }}>Next run:</strong> {formatScheduleTime(schedule.nextRunAtUtc, schedule.timeZoneId)}</div>
              )}
              {schedule.lastTriggeredAt && (
                <div>
                  <strong style={{ color: "rgba(148,163,184,0.8)" }}>Last triggered:</strong> {formatScheduleTime(schedule.lastTriggeredAt, schedule.timeZoneId)}
                  {schedule.lastOutcome && <span> · {schedule.lastOutcome}</span>}
                </div>
              )}
            </div>
          )}

          {scheduleError && (
            <div style={{ marginBottom: 12, color: "#f87171", fontSize: 13, padding: "6px 10px", borderRadius: 6, background: "rgba(248,113,113,0.08)", border: "1px solid rgba(248,113,113,0.15)" }}>
              {scheduleError}
            </div>
          )}

          <div style={{ display: "flex", alignItems: "center", gap: 12 }}>
            <Button
              appearance="primary"
              size="small"
              disabled={scheduleBusy || !cronExpr.trim()}
              onClick={handleSaveSchedule}
            >
              {scheduleSaving ? <Spinner size="tiny" /> : schedule ? "Update Schedule" : "Create Schedule"}
            </Button>
            {schedule && (
              <Button
                appearance="subtle"
                size="small"
                disabled={scheduleBusy}
                onClick={handleDeleteSchedule}
                style={{ color: deleteConfirm ? "#fbbf24" : "#f87171" }}
              >
                {scheduleDeleting ? <Spinner size="tiny" /> : deleteConfirm ? "Confirm Delete?" : "Delete Schedule"}
              </Button>
            )}
            {scheduleSaved && <span style={{ color: "#4ade80", fontSize: 13 }}>✓ Saved</span>}
          </div>
        </>
      )}

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
