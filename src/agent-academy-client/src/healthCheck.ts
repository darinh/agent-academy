import { getInstanceHealth } from "./api";
import type { InstanceHealthResult } from "./api";
import { hasInstanceChanged } from "./recovery";
import type { RecoveryBannerState } from "./RecoveryBanner";

/**
 * Client connection states that map to the spec 011 UX state table.
 *
 * - `connected`         — healthy, normal operation
 * - `reconnecting`      — SignalR dropped, attempting reconnect
 * - `instance-mismatch` — reconnected but server instanceId changed (restart)
 * - `crash-recovered`   — reconnected and server reports crashDetected
 * - `resume-success`    — reconnected, same instance, no crash
 * - `refresh-failed`    — reconnected but workspace refresh failed
 */
export type ConnectionState =
  | "connected"
  | "reconnecting"
  | "instance-mismatch"
  | "crash-recovered"
  | "resume-success"
  | "refresh-failed";

export interface ReconnectResult {
  state: ConnectionState;
  health: InstanceHealthResult | null;
  banner: RecoveryBannerState | null;
}

const RECONNECTING_BANNER: RecoveryBannerState = {
  tone: "reconnecting",
  message: "Connection lost — reconnecting",
  detail: "The workspace is read-only until the connection is restored.",
};

/**
 * Evaluate the reconnect outcome by comparing the previous instance ID
 * against a fresh health check from the server.
 */
export async function evaluateReconnect(
  previousInstanceId: string | null,
): Promise<ReconnectResult> {
  let health: InstanceHealthResult;
  try {
    health = await getInstanceHealth();
  } catch {
    return {
      state: "refresh-failed",
      health: null,
      banner: {
        tone: "error",
        message: "Live connection returned, but health check failed.",
        detail: "Workspace state may be stale. Use manual refresh to retry.",
      },
    };
  }

  if (health.crashDetected) {
    return {
      state: "crash-recovered",
      health,
      banner: {
        tone: "crash",
        message: "Server recovered from an unexpected shutdown",
        detail: "The previous instance ended unexpectedly. Verify any in-flight work.",
      },
    };
  }

  if (hasInstanceChanged(previousInstanceId, health.instanceId)) {
    return {
      state: "instance-mismatch",
      health,
      banner: {
        tone: "syncing",
        message: "Server restarted — refreshing workspace",
        detail: "Re-syncing rooms, activity, and current task state.",
      },
    };
  }

  return {
    state: "resume-success",
    health,
    banner: null,
  };
}

export { RECONNECTING_BANNER };
