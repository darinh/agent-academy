import { useCallback, useEffect, useRef, useState } from "react";
import type { AuthStatus } from "./api";
import { apiBaseUrl, getAuthStatus, logout } from "./api";
import {
  AUTH_STATUS_POLL_MS,
  clearAutoReauthAttempt,
  clearManualLogout,
  getAuthTransitionEffect,
  markAutoReauthAttempt,
  markManualLogout,
  shouldAttemptAutoReauth,
} from "./authMonitor";

export interface UseAuthOptions {
  /** Called when the user's session needs reconnection but auto-reauth was suppressed. */
  onSessionWarning?: () => void;
}

export interface UseAuthResult {
  auth: AuthStatus | null;
  loginUrl: string;
  logoutDialog: {
    open: boolean;
    request: () => void;
    confirm: () => void;
    cancel: () => void;
  };
}

/**
 * Manages authentication lifecycle: initial check with retry,
 * periodic polling, transition effects (auto-reauth / manual logout),
 * and the logout confirmation dialog state.
 */
export function useAuth({ onSessionWarning }: UseAuthOptions = {}): UseAuthResult {
  const [auth, setAuth] = useState<AuthStatus | null>(null);
  const [logoutConfirmOpen, setLogoutConfirmOpen] = useState(false);
  const previousAuthRef = useRef<AuthStatus | null>(null);
  const authRefreshInFlight = useRef(false);
  const onSessionWarningRef = useRef(onSessionWarning);
  onSessionWarningRef.current = onSessionWarning;

  const loginUrl = `${apiBaseUrl}/api/auth/login`;

  const refreshAuthStatus = useCallback(async () => {
    if (authRefreshInFlight.current) return;
    authRefreshInFlight.current = true;
    try {
      const status = await getAuthStatus();
      setAuth(status);
    } finally {
      authRefreshInFlight.current = false;
    }
  }, []);

  // Check auth status on mount — retry if backend is still starting
  useEffect(() => {
    let cancelled = false;
    let retryTimer: ReturnType<typeof setTimeout> | null = null;
    async function checkAuth(attempt: number) {
      if (cancelled) return;
      try {
        const status = await getAuthStatus();
        if (!cancelled) setAuth(status);
      } catch {
        if (cancelled) return;
        if (attempt < 3) {
          retryTimer = setTimeout(() => void checkAuth(attempt + 1), 2000);
        } else {
          setAuth({
            authEnabled: false,
            authenticated: false,
            copilotStatus: "unavailable",
          });
        }
      }
    }
    void checkAuth(0);
    return () => { cancelled = true; if (retryTimer) clearTimeout(retryTimer); };
  }, []);

  // Poll auth status periodically
  useEffect(() => {
    if (auth === null) return undefined;
    const timer = window.setInterval(() => {
      void refreshAuthStatus().catch(() => undefined);
    }, AUTH_STATUS_POLL_MS);
    return () => window.clearInterval(timer);
  }, [auth, refreshAuthStatus]);

  // Handle auth state transitions
  useEffect(() => {
    if (!auth) return;

    const transitionEffect = getAuthTransitionEffect(previousAuthRef.current, auth);

    if (transitionEffect.clearAutoReauthAttempt) {
      clearAutoReauthAttempt();
    }
    if (transitionEffect.clearManualLogoutSuppression) {
      clearManualLogout();
    }
    if (transitionEffect.redirectToLogin) {
      markAutoReauthAttempt();
      window.location.assign(loginUrl);
    } else if (
      shouldAttemptAutoReauth(previousAuthRef.current, auth)
      && !transitionEffect.redirectToLogin
    ) {
      onSessionWarningRef.current?.();
    }

    previousAuthRef.current = auth;
  }, [auth, loginUrl]);

  const doLogout = useCallback(async () => {
    markManualLogout();
    clearAutoReauthAttempt();
    try {
      await logout();
      setAuth({
        authEnabled: true,
        authenticated: false,
        copilotStatus: "unavailable",
        user: null,
      });
    } catch {
      clearManualLogout();
    }
  }, []);

  const requestLogout = useCallback(() => {
    setLogoutConfirmOpen(true);
  }, []);

  const confirmLogout = useCallback(() => {
    setLogoutConfirmOpen(false);
    void doLogout();
  }, [doLogout]);

  const cancelLogout = useCallback(() => {
    setLogoutConfirmOpen(false);
  }, []);

  return {
    auth,
    loginUrl,
    logoutDialog: {
      open: logoutConfirmOpen,
      request: requestLogout,
      confirm: confirmLogout,
      cancel: cancelLogout,
    },
  };
}
