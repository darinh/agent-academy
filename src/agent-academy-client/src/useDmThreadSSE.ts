import { useEffect, useRef, useState } from "react";
import { apiBaseUrl } from "./api";

export type ConnectionStatus =
  | "connecting"
  | "connected"
  | "reconnecting"
  | "disconnected";

const RETRY_DELAYS = [0, 2_000, 5_000, 10_000, 30_000];

/**
 * SSE hook for DM thread list invalidation.
 *
 * Connects to `GET /api/dm/threads/stream` and calls `onInvalidate`
 * whenever a DM thread is updated. The caller should debounce and
 * refetch the thread list via REST.
 *
 * On `resync` events, calls `onInvalidate` so the client refetches.
 */
export function useDmThreadSSE(
  onInvalidate: () => void,
  enabled = true,
): ConnectionStatus {
  const [status, setStatus] = useState<ConnectionStatus>(
    enabled ? "connecting" : "disconnected",
  );

  const onInvalidateRef = useRef(onInvalidate);
  onInvalidateRef.current = onInvalidate;

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

      const url = `${apiBaseUrl}/api/dm/threads/stream`;
      source = new EventSource(url, { withCredentials: true });

      source.addEventListener("connected", () => {
        if (!cancelled) {
          setStatus("connected");
          attempt = 0;
          // Refetch thread list on (re)connect to catch updates missed while disconnected.
          onInvalidateRef.current();
        }
      });

      source.addEventListener("thread-updated", () => {
        onInvalidateRef.current();
      });

      source.addEventListener("resync", () => {
        onInvalidateRef.current();
      });

      source.onerror = () => {
        if (cancelled) return;

        source?.close();
        source = null;
        const delay = RETRY_DELAYS[Math.min(attempt, RETRY_DELAYS.length - 1)];
        setStatus(attempt === 0 ? "connecting" : "reconnecting");
        attempt++;
        retryTimer = setTimeout(connect, delay);
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
