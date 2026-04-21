import type { AuthStatus } from "./api";

export const AUTH_STATUS_POLL_MS = 30_000;

const AUTO_REAUTH_STORAGE_KEY = "aa-auto-reauth-attempted";
const MANUAL_LOGOUT_STORAGE_KEY = "aa-manual-logout";

interface StorageLike {
  getItem(key: string): string | null;
  setItem(key: string, value: string): void;
  removeItem(key: string): void;
}

export interface AuthTransitionEffect {
  clearAutoReauthAttempt: boolean;
  clearManualLogoutSuppression: boolean;
  redirectToLogin: boolean;
}

function getSessionStorage(storage?: StorageLike): StorageLike | null {
  if (storage) return storage;

  try {
    return window.sessionStorage;
  } catch {
    return null;
  }
}

export function shouldAttemptAutoReauth(previous: AuthStatus | null, next: AuthStatus): boolean {
  return previous?.copilotStatus === "operational"
    && next.authEnabled
    && next.copilotStatus === "degraded"
    && !!next.user;
}

export function hasAutoReauthAttempt(storage?: StorageLike): boolean {
  return getSessionStorage(storage)?.getItem(AUTO_REAUTH_STORAGE_KEY) === "1";
}

export function markAutoReauthAttempt(storage?: StorageLike): void {
  getSessionStorage(storage)?.setItem(AUTO_REAUTH_STORAGE_KEY, "1");
}

export function clearAutoReauthAttempt(storage?: StorageLike): void {
  getSessionStorage(storage)?.removeItem(AUTO_REAUTH_STORAGE_KEY);
}

export function hasManualLogoutSuppression(storage?: StorageLike): boolean {
  return getSessionStorage(storage)?.getItem(MANUAL_LOGOUT_STORAGE_KEY) === "1";
}

export function markManualLogout(storage?: StorageLike): void {
  getSessionStorage(storage)?.setItem(MANUAL_LOGOUT_STORAGE_KEY, "1");
}

export function clearManualLogout(storage?: StorageLike): void {
  getSessionStorage(storage)?.removeItem(MANUAL_LOGOUT_STORAGE_KEY);
}

export function shouldAutoReauthenticate(
  previous: AuthStatus | null,
  next: AuthStatus,
  storage?: StorageLike,
): boolean {
  return shouldAttemptAutoReauth(previous, next)
    && !hasManualLogoutSuppression(storage)
    && !hasAutoReauthAttempt(storage);
}

export function getAuthTransitionEffect(
  previous: AuthStatus | null,
  next: AuthStatus,
  storage?: StorageLike,
): AuthTransitionEffect {
  // Never clear the auto-reauth flag automatically. Once a redirect has been
  // attempted in this browser session, don't try again — the user can manually
  // reconnect via the "Reconnect GitHub" button. Clearing the flag on
  // "operational" status caused a redirect loop when copilotStatus oscillated
  // between operational and degraded (each return to operational re-armed the
  // redirect, so every subsequent degraded transition triggered a full-page
  // navigation to the OAuth flow).
  return {
    clearAutoReauthAttempt: false,
    clearManualLogoutSuppression: next.copilotStatus === "operational",
    redirectToLogin: shouldAutoReauthenticate(previous, next, storage),
  };
}
