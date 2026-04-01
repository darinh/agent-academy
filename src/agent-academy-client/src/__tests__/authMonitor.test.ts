import { describe, expect, it } from "vitest";
import type { AuthStatus } from "../api";
import {
  clearAutoReauthAttempt,
  clearManualLogout,
  getAuthTransitionEffect,
  hasAutoReauthAttempt,
  hasManualLogoutSuppression,
  markAutoReauthAttempt,
  markManualLogout,
  shouldAttemptAutoReauth,
  shouldAutoReauthenticate,
} from "../authMonitor";

class MemoryStorage {
  private readonly values = new Map<string, string>();

  getItem(key: string): string | null {
    return this.values.get(key) ?? null;
  }

  setItem(key: string, value: string): void {
    this.values.set(key, value);
  }

  removeItem(key: string): void {
    this.values.delete(key);
  }
}

function authStatus(overrides: Partial<AuthStatus>): AuthStatus {
  return {
    authEnabled: true,
    authenticated: false,
    copilotStatus: "unavailable",
    user: null,
    ...overrides,
  };
}

describe("auth monitor", () => {
  it("detects operational-to-degraded transitions for connected users", () => {
    const previous = authStatus({
      authenticated: true,
      copilotStatus: "operational",
      user: { login: "athena" },
    });
    const next = authStatus({
      authenticated: false,
      copilotStatus: "degraded",
      user: { login: "athena" },
    });

    expect(shouldAttemptAutoReauth(previous, next)).toBe(true);
    expect(shouldAutoReauthenticate(previous, next, new MemoryStorage())).toBe(true);
  });

  it("does not auto-reauth when the session was explicitly logged out", () => {
    const storage = new MemoryStorage();
    const previous = authStatus({
      authenticated: true,
      copilotStatus: "operational",
      user: { login: "athena" },
    });
    const next = authStatus({
      authenticated: false,
      copilotStatus: "degraded",
      user: { login: "athena" },
    });

    markManualLogout(storage);

    expect(hasManualLogoutSuppression(storage)).toBe(true);
    expect(shouldAutoReauthenticate(previous, next, storage)).toBe(false);

    clearManualLogout(storage);
    expect(hasManualLogoutSuppression(storage)).toBe(false);
  });

  it("debounces repeated degraded probes within the same tab", () => {
    const storage = new MemoryStorage();
    const previous = authStatus({
      authenticated: true,
      copilotStatus: "operational",
      user: { login: "athena" },
    });
    const next = authStatus({
      authenticated: false,
      copilotStatus: "degraded",
      user: { login: "athena" },
    });

    expect(shouldAutoReauthenticate(previous, next, storage)).toBe(true);

    markAutoReauthAttempt(storage);

    expect(hasAutoReauthAttempt(storage)).toBe(true);
    expect(shouldAutoReauthenticate(previous, next, storage)).toBe(false);

    clearAutoReauthAttempt(storage);
    expect(hasAutoReauthAttempt(storage)).toBe(false);
  });

  it("ignores degraded states without a retained browser identity", () => {
    const previous = authStatus({
      authenticated: true,
      copilotStatus: "operational",
      user: { login: "athena" },
    });
    const missingUser = authStatus({
      authenticated: false,
      copilotStatus: "degraded",
      user: null,
    });

    expect(shouldAttemptAutoReauth(previous, missingUser)).toBe(false);
  });

  it("describes the redirect and storage-clearing side effects for degraded transitions", () => {
    const storage = new MemoryStorage();
    const previous = authStatus({
      authenticated: true,
      copilotStatus: "operational",
      user: { login: "athena" },
    });
    const next = authStatus({
      authenticated: false,
      copilotStatus: "degraded",
      user: { login: "athena" },
    });

    const effect = getAuthTransitionEffect(previous, next, storage);

    expect(effect).toEqual({
      clearAutoReauthAttempt: false,
      clearManualLogoutSuppression: false,
      redirectToLogin: true,
    });
  });

  it("clears session debounce state when auth returns to operational", () => {
    const storage = new MemoryStorage();
    markAutoReauthAttempt(storage);
    markManualLogout(storage);

    const effect = getAuthTransitionEffect(
      authStatus({
        authenticated: false,
        copilotStatus: "degraded",
        user: { login: "athena" },
      }),
      authStatus({
        authenticated: true,
        copilotStatus: "operational",
        user: { login: "athena" },
      }),
      storage,
    );

    expect(effect).toEqual({
      clearAutoReauthAttempt: true,
      clearManualLogoutSuppression: true,
      redirectToLogin: false,
    });
  });

  it("keeps logout suppression in place while unavailable after explicit logout", () => {
    const storage = new MemoryStorage();
    markManualLogout(storage);
    markAutoReauthAttempt(storage);

    const effect = getAuthTransitionEffect(
      authStatus({
        authenticated: true,
        copilotStatus: "operational",
        user: { login: "athena" },
      }),
      authStatus({
        authenticated: false,
        copilotStatus: "unavailable",
        user: null,
      }),
      storage,
    );

    expect(effect).toEqual({
      clearAutoReauthAttempt: true,
      clearManualLogoutSuppression: false,
      redirectToLogin: false,
    });
  });
});
