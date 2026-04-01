import type { AuthStatus, AuthUser, CopilotStatus } from "./api";

export interface CopilotStatusCopy {
  eyebrow: string;
  title: string;
  description: string;
  actionLabel: string;
  supportingNote: string;
}

export function shouldRenderWorkspace(auth: AuthStatus): boolean {
  return !auth.authEnabled || auth.copilotStatus === "operational";
}

export function getCopilotStatusCopy(
  copilotStatus: CopilotStatus,
  user?: AuthUser | null,
): CopilotStatusCopy {
  const userLabel = user?.name ?? user?.login ?? "Your";

  switch (copilotStatus) {
    case "degraded":
      return {
        eyebrow: "Copilot degraded",
        title: "Copilot SDK unavailable - agents cannot work",
        description:
          `${userLabel} GitHub session is still present in this browser, but Copilot access has dropped out. Reconnect GitHub to restore agent execution.`,
        actionLabel: "Reconnect GitHub",
        supportingNote: "Browser identity is intact; only the agent runtime is paused.",
      };
    case "unavailable":
      return {
        eyebrow: "Authentication required",
        title: "Sign in to enter the academy",
        description:
          "Use GitHub to unlock rooms, task branches, and the live agent workspace.",
        actionLabel: "Login with GitHub",
        supportingNote: "Once authenticated, the workspace and project switcher open automatically.",
      };
    case "operational":
    default:
      return {
        eyebrow: "Copilot operational",
        title: "Agent Academy is ready",
        description: "Your workspace is connected and the agent fleet is ready to take work.",
        actionLabel: "Continue",
        supportingNote: "Operational state should route straight into the workspace shell.",
      };
  }
}
