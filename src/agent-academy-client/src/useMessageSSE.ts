import { useEffect, useRef, useState } from "react";
import type { DmMessage } from "./api";
import { apiBaseUrl } from "./api";

export type ConnectionStatus =
  | "connecting"
  | "connected"
  | "reconnecting"
  | "disconnected";

const RETRY_DELAYS = [0, 2_000, 5_000, 10_000, 30_000];

/**
 * SSE hook for real-time DM message streaming.
 *
 * Connects to `GET /api/dm/threads/{agentId}/stream` and:
 * - Replays messages from the server-side DB cursor on connect/reconnect
 * - Streams live messages as `message` events
 * - Handles `resync` events (channel overflow) by calling `onResync`
 * - Auto-reconnects with exponential backoff, using `?after={lastId}` cursor
 * - Resets cursor when agentId changes (new agent = fresh replay)
 */
export function useMessageSSE(
  agentId: string | null,
  onMessage: (msg: DmMessage) => void,
  onResync?: () => void,
  enabled = true,
): ConnectionStatus {
  const [status, setStatus] = useState<ConnectionStatus>(
    agentId && enabled ? "connecting" : "disconnected",
  );

  const onMessageRef = useRef(onMessage);
  onMessageRef.current = onMessage;

  const onResyncRef = useRef(onResync);
  onResyncRef.current = onResync;

  const lastIdRef = useRef<string | null>(null);

  useEffect(() => {
    if (!agentId || !enabled) {
      setStatus("disconnected");
      return;
    }

    // Reset cursor when switching agents — new thread needs a full replay.
    lastIdRef.current = null;

    setStatus("connecting");
    let cancelled = false;
    let source: EventSource | null = null;
    let retryTimer: ReturnType<typeof setTimeout> | undefined;
    let attempt = 0;

    function buildUrl() {
      const base = `${apiBaseUrl}/api/dm/threads/${encodeURIComponent(agentId!)}` +
        "/stream";
      const cursor = lastIdRef.current;
      return cursor ? `${base}?after=${encodeURIComponent(cursor)}` : base;
    }

    function connect() {
      if (cancelled) return;

      source = new EventSource(buildUrl(), { withCredentials: true });

      source.addEventListener("message", (e: MessageEvent) => {
        try {
          const msg: DmMessage = JSON.parse(e.data);
          lastIdRef.current = msg.id;
          onMessageRef.current(msg);
        } catch {
          // Malformed event — skip.
        }
      });

      source.addEventListener("resync", () => {
        onResyncRef.current?.();
      });

      source.onopen = () => {
        if (!cancelled) {
          setStatus("connected");
          attempt = 0;
        }
      };

      source.onerror = () => {
        if (cancelled) return;

        // Always close + manual reconnect so the URL uses the latest cursor.
        // Relying on EventSource's built-in reconnect would reuse the stale URL.
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
  }, [agentId, enabled]);

  return status;
}
