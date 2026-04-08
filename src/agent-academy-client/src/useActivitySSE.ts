import { useEffect, useRef, useState } from "react";
import type { ActivityEvent } from "./api";

export type ConnectionStatus =
  | "connecting"
  | "connected"
  | "reconnecting"
  | "disconnected";

const INITIAL_RETRY_DELAYS = [0, 2_000, 5_000, 10_000, 30_000];

/**
 * Manages an SSE (EventSource) connection to /api/activity/stream.
 * Drop-in alternative to useActivityHub for environments without WebSocket support.
 * Calls `onEvent` for every `activityEvent` received from the server.
 * Handles auto-reconnect with exponential backoff.
 */
export function useActivitySSE(
  onEvent: (evt: ActivityEvent) => void,
  enabled = true,
): ConnectionStatus {
  const [status, setStatus] = useState<ConnectionStatus>(
    enabled ? "connecting" : "disconnected",
  );
  const onEventRef = useRef(onEvent);
  onEventRef.current = onEvent;

  useEffect(() => {
    if (!enabled) {
      setStatus("disconnected");
      return;
    }

    setStatus("connecting");
    let cancelled = false;
    let source: EventSource | null = null;
    let retryTimer: ReturnType<typeof setTimeout> | undefined;
    let attempt = 0;

    function connect() {
      if (cancelled) return;

      source = new EventSource("/api/activity/stream");

      source.addEventListener("activityEvent", (e: MessageEvent) => {
        try {
          const evt: ActivityEvent = JSON.parse(e.data);
          onEventRef.current(evt);
        } catch {
          // Malformed event — skip.
        }
      });

      source.onopen = () => {
        if (!cancelled) {
          setStatus("connected");
          attempt = 0;
        }
      };

      source.onerror = () => {
        if (cancelled) return;

        // EventSource auto-reconnects, but if it fails repeatedly
        // (e.g., server down), readyState goes to CLOSED.
        if (source?.readyState === EventSource.CLOSED) {
          source.close();
          source = null;
          const delay = INITIAL_RETRY_DELAYS[Math.min(attempt, INITIAL_RETRY_DELAYS.length - 1)];
          setStatus(attempt === 0 ? "connecting" : "reconnecting");
          attempt++;
          retryTimer = setTimeout(connect, delay);
        } else {
          setStatus("reconnecting");
        }
      };
    }

    connect();

    return () => {
      cancelled = true;
      if (retryTimer !== undefined) clearTimeout(retryTimer);
      source?.close();
      source = null;
    };
  }, [enabled]);

  return status;
}
