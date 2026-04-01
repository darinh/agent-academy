import { describe, expect, it } from "vitest";
import { getCopilotStatusCopy, shouldRenderWorkspace } from "../authPresentation";
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
});
