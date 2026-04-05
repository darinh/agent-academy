import { useCallback, useEffect, useRef, useState } from "react";
import { getInstanceHealth } from "./api";

export type CircuitBreakerState = "Closed" | "Open" | "HalfOpen" | null;

const POLL_INTERVAL_NORMAL_MS = 60_000;
const POLL_INTERVAL_DEGRADED_MS = 10_000;

export function isDegraded(state: CircuitBreakerState): boolean {
  return state === "Open" || state === "HalfOpen";
}

export function parseCircuitBreakerState(raw: string | undefined): CircuitBreakerState {
  if (raw === "Closed" || raw === "Open" || raw === "HalfOpen") return raw;
  return null;
}

/**
 * Polls /api/health/instance for circuit breaker state.
 * Polls faster when the circuit is Open/HalfOpen so the UI
 * reflects recovery quickly. Pauses polling when the tab is hidden.
 */
export function useCircuitBreakerPolling(): {
  circuitBreakerState: CircuitBreakerState;
  refreshCircuitBreaker: () => void;
} {
  const [state, setState] = useState<CircuitBreakerState>(null);
  const timerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const mountedRef = useRef(true);
  const requestIdRef = useRef(0);

  const clearTimer = useCallback(() => {
    if (timerRef.current) {
      clearTimeout(timerRef.current);
      timerRef.current = null;
    }
  }, []);

  const poll = useCallback(async () => {
    const myId = ++requestIdRef.current;
    try {
      const health = await getInstanceHealth();
      if (!mountedRef.current || myId !== requestIdRef.current) return;
      setState(parseCircuitBreakerState(health.circuitBreakerState));
    } catch {
      // Health endpoint unreachable — keep the last known value visible.
    }
  }, []);

  const scheduleNext = useCallback(
    (currentState: CircuitBreakerState) => {
      clearTimer();
      if (typeof document !== "undefined" && document.visibilityState === "hidden") return;
      const ms = isDegraded(currentState)
        ? POLL_INTERVAL_DEGRADED_MS
        : POLL_INTERVAL_NORMAL_MS;
      timerRef.current = setTimeout(() => {
        void poll().then(() => {
          if (mountedRef.current) {
            setState((s) => {
              scheduleNext(s);
              return s;
            });
          }
        });
      }, ms);
    },
    [poll, clearTimer],
  );

  // Pause/resume polling on visibility change
  useEffect(() => {
    function handleVisibility() {
      if (document.visibilityState === "visible") {
        // Tab became visible — poll immediately, then schedule next
        void poll().then(() => {
          if (mountedRef.current) {
            setState((s) => {
              scheduleNext(s);
              return s;
            });
          }
        });
      } else {
        clearTimer();
      }
    }
    document.addEventListener("visibilitychange", handleVisibility);
    return () => document.removeEventListener("visibilitychange", handleVisibility);
  }, [poll, scheduleNext, clearTimer]);

  // Initial poll + start scheduling
  useEffect(() => {
    mountedRef.current = true;
    void poll().then(() => {
      if (mountedRef.current) {
        setState((s) => {
          scheduleNext(s);
          return s;
        });
      }
    });
    return () => {
      mountedRef.current = false;
      clearTimer();
    };
  }, [poll, scheduleNext, clearTimer]);

  const refreshCircuitBreaker = useCallback(() => {
    void poll();
  }, [poll]);

  return { circuitBreakerState: state, refreshCircuitBreaker };
}
