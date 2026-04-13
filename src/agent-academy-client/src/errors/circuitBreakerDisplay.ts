import type { CircuitBreakerState } from "../useCircuitBreakerPolling";

export function circuitBreakerDisplay(state: CircuitBreakerState): {
  color: string;
  label: string;
  detail: string;
} {
  switch (state) {
    case "Open":
      return {
        color: "var(--aa-copper)",
        label: "Circuit Open",
        detail: "Agent requests are blocked. Waiting for cooldown before probing.",
      };
    case "HalfOpen":
      return {
        color: "var(--aa-gold)",
        label: "Circuit Half-Open",
        detail: "Probing with a single request to test if the backend has recovered.",
      };
    case "Closed":
      return {
        color: "var(--aa-lime)",
        label: "Circuit Closed",
        detail: "All systems normal.",
      };
    default:
      return {
        color: "var(--aa-muted)",
        label: "Unknown",
        detail: "Circuit breaker state is unavailable.",
      };
  }
}
