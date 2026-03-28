import { useEffect, useRef, useState } from "react";
import {
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
  type HubConnection,
} from "@microsoft/signalr";
import type { ActivityEvent } from "./api";

export type ConnectionStatus =
  | "connecting"
  | "connected"
  | "reconnecting"
  | "disconnected";

const INITIAL_RETRY_DELAYS = [0, 2_000, 5_000, 10_000, 30_000];

/**
 * Manages a SignalR connection to /hubs/activity.
 * Calls `onEvent` for every `activityEvent` received from the server.
 * Handles auto-reconnect with exponential backoff.
 * Retries initial connection failures (withAutomaticReconnect only covers post-connect drops).
 */
export function useActivityHub(onEvent: (evt: ActivityEvent) => void): ConnectionStatus {
  const [status, setStatus] = useState<ConnectionStatus>("connecting");
  const onEventRef = useRef(onEvent);
  onEventRef.current = onEvent;

  useEffect(() => {
    const connection: HubConnection = new HubConnectionBuilder()
      .withUrl("/hubs/activity")
      .withAutomaticReconnect(INITIAL_RETRY_DELAYS)
      .configureLogging(LogLevel.Warning)
      .build();

    connection.on("activityEvent", (evt: ActivityEvent) => {
      onEventRef.current(evt);
    });

    connection.onreconnecting(() => setStatus("reconnecting"));
    connection.onreconnected(() => setStatus("connected"));
    connection.onclose(() => setStatus("disconnected"));

    let cancelled = false;
    let retryTimer: ReturnType<typeof setTimeout> | undefined;

    async function start(attempt: number) {
      if (cancelled) return;
      try {
        await connection.start();
        if (!cancelled) setStatus("connected");
      } catch {
        if (cancelled) return;
        const delay = INITIAL_RETRY_DELAYS[Math.min(attempt, INITIAL_RETRY_DELAYS.length - 1)];
        setStatus(attempt === 0 ? "connecting" : "reconnecting");
        retryTimer = setTimeout(() => void start(attempt + 1), delay);
      }
    }

    void start(0);

    return () => {
      cancelled = true;
      if (retryTimer !== undefined) clearTimeout(retryTimer);
      if (connection.state !== HubConnectionState.Disconnected) {
        void connection.stop();
      }
    };
  }, []);

  return status;
}
