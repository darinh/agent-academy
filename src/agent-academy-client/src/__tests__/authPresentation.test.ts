import { describe, expect, it } from "vitest";
import { getCopilotStatusCopy, getCopilotStatusFacts, shouldRenderWorkspace } from "../authPresentation";
import type { AuthStatus } from "../api";

function authStatus(overrides: Partial<AuthStatus>): AuthStatus {
  return {
    authEnabled: true,
    authenticated: false,
    copilotStatus: "unavailable",
    user: null,
    ...overrides,
  };
}

describe("auth presentation", () => {
  it("renders the workspace only when auth is disabled or Copilot is operational", () => {
    expect(shouldRenderWorkspace(authStatus({ authEnabled: false }))).toBe(true);
    expect(shouldRenderWorkspace(authStatus({ copilotStatus: "operational" }))).toBe(true);
    expect(shouldRenderWorkspace(authStatus({ copilotStatus: "degraded" }))).toBe(false);
    expect(shouldRenderWorkspace(authStatus({ copilotStatus: "unavailable" }))).toBe(false);
  });

  it("uses the required degraded-state headline", () => {
    const copy = getCopilotStatusCopy("degraded", { login: "athena", name: "Athena" });

    expect(copy.title).toBe("Copilot SDK unavailable - agents cannot work");
    expect(copy.actionLabel).toBe("Reconnect GitHub");
    expect(copy.description).toContain("Athena GitHub session is still present");
  });

  it("keeps the unavailable state on the standard sign-in flow", () => {
    const copy = getCopilotStatusCopy("unavailable");

    expect(copy.actionLabel).toBe("Login with GitHub");
    expect(copy.eyebrow).toBe("Authentication required");
  });

  it("surfaces state facts that distinguish degraded from unavailable sessions", () => {
    expect(getCopilotStatusFacts("degraded")).toEqual([
      { label: "Browser identity", value: "Still connected", tone: "good" },
      { label: "Copilot runtime", value: "Paused until re-auth", tone: "warning" },
      { label: "Workspace access", value: "Fail-closed", tone: "critical" },
    ]);

    expect(getCopilotStatusFacts("unavailable")).toEqual([
      { label: "Browser identity", value: "Not connected", tone: "critical" },
      { label: "Copilot runtime", value: "Unavailable", tone: "warning" },
      { label: "Workspace access", value: "Sign-in required", tone: "critical" },
    ]);
  });
});
