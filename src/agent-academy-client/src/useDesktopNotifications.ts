import { useCallback, useEffect, useRef, useState } from "react";
import type { ActivityEvent, ActivityEventType } from "./api";

const STORAGE_KEY = "aa-desktop-notifications";

/** Event types that warrant a desktop notification when the tab is backgrounded. */
const NOTIFY_EVENT_TYPES: ReadonlySet<ActivityEventType> = new Set([
  "DirectMessageSent",
  "AgentErrorOccurred",
  "SubagentFailed",
  "SprintCompleted",
  "SprintCancelled",
  "TaskCreated",
  "TaskUnblocked",
]);

function loadEnabled(): boolean {
  try {
    return localStorage.getItem(STORAGE_KEY) === "true";
  } catch {
    return false;
  }
}

function saveEnabled(v: boolean): void {
  try {
    localStorage.setItem(STORAGE_KEY, String(v));
  } catch {
    /* quota */
  }
}

function notificationSupported(): boolean {
  return typeof window !== "undefined" && "Notification" in window && typeof Notification !== "undefined";
}

function permissionState(): NotificationPermission | "unsupported" {
  if (!notificationSupported()) return "unsupported";
  return Notification.permission;
}

function eventTitle(evt: ActivityEvent): string {
  switch (evt.type) {
    case "DirectMessageSent":
      return "New message";
    case "AgentErrorOccurred":
      return "Agent error";
    case "SubagentFailed":
      return "Subagent failed";
    case "SprintCompleted":
      return "Sprint completed";
    case "SprintCancelled":
      return "Sprint cancelled";
    case "TaskCreated":
      return "Task created";
    case "TaskUnblocked":
      return "Task unblocked";
    default:
      return evt.type.replace(/([A-Z])/g, " $1").trim();
  }
}

export interface DesktopNotificationControls {
  /** Whether the user has opted in to desktop notifications. */
  enabled: boolean;
  /** Toggle desktop notifications on/off. Requests permission if needed. */
  setEnabled: (on: boolean) => void;
  /** Current Notification API permission state. */
  permission: NotificationPermission | "unsupported";
  /** Whether the browser supports the Notification API. */
  supported: boolean;
  /** Show a desktop notification for an activity event (only when tab is hidden). */
  notify: (evt: ActivityEvent) => void;
}

export function useDesktopNotifications(): DesktopNotificationControls {
  const [enabled, setEnabledState] = useState(loadEnabled);
  const [permission, setPermission] = useState<NotificationPermission | "unsupported">(permissionState);
  const supported = notificationSupported();

  // Track recent notification IDs to deduplicate (SSE reconnect can replay events)
  const recentIds = useRef(new Set<string>());

  // Request permission when user enables notifications
  const setEnabled = useCallback(
    async (on: boolean) => {
      // Can't enable if browser doesn't support Notification API
      if (on && !supported) return;

      if (on && supported && Notification.permission === "default") {
        try {
          const result = await Notification.requestPermission();
          setPermission(result);
          if (result !== "granted") {
            saveEnabled(false);
            setEnabledState(false);
            return;
          }
        } catch {
          saveEnabled(false);
          setEnabledState(false);
          return;
        }
      }
      saveEnabled(on);
      setEnabledState(on);
    },
    [supported],
  );

  // Keep permission state in sync (e.g., user revokes in browser settings)
  useEffect(() => {
    if (!supported) return;
    const id = setInterval(() => {
      setPermission(Notification.permission);
    }, 10_000);
    return () => clearInterval(id);
  }, [supported]);

  const notify = useCallback(
    (evt: ActivityEvent) => {
      if (!enabled || !supported || Notification.permission !== "granted") return;
      if (!NOTIFY_EVENT_TYPES.has(evt.type)) return;
      if (!document.hidden) return;

      // Deduplicate
      if (recentIds.current.has(evt.id)) return;
      recentIds.current.add(evt.id);
      if (recentIds.current.size > 100) {
        const entries = [...recentIds.current];
        recentIds.current = new Set(entries.slice(-50));
      }

      const title = eventTitle(evt);
      const body = evt.message || evt.type;
      const n = new Notification(title, {
        body,
        tag: evt.id,
        icon: "/agent-academy-icon.png",
        silent: false,
      });
      n.onclick = () => {
        window.focus();
        n.close();
      };
      // Auto-close after 8 seconds
      setTimeout(() => n.close(), 8_000);
    },
    [enabled, supported],
  );

  return { enabled, setEnabled, permission, supported, notify };
}
