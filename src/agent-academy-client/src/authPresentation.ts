import type { AuthStatus, AuthUser, CopilotStatus } from "./api";

export interface CopilotStatusCopy {
  eyebrow: string;
  title: string;
  description: string;
  actionLabel: string;
  supportingNote: string;
}

export interface CopilotStatusFact {
  label: string;
  value: string;
  tone: "good" | "warning" | "critical" | "informative";
}

export function shouldRenderWorkspace(auth: AuthStatus): boolean {
  return !auth.authEnabled || auth.copilotStatus === "operational" || auth.copilotStatus === "degraded";
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
        title: "Copilot needs reconnection",
        description:
          `${userLabel} GitHub session is still present in this browser, but Copilot access has dropped out. Review the workspace in limited mode while you reconnect GitHub.`,
        actionLabel: "Reconnect GitHub",
        supportingNote: "Browser identity is intact; sending new work stays paused until Copilot is healthy again.",
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

export function getCopilotStatusFacts(copilotStatus: CopilotStatus): CopilotStatusFact[] {
  switch (copilotStatus) {
    case "degraded":
      return [
        { label: "Browser identity", value: "Still connected", tone: "good" },
        { label: "Copilot runtime", value: "Paused until re-auth", tone: "warning" },
        { label: "Workspace access", value: "Limited mode", tone: "informative" },
      ];
    case "unavailable":
      return [
        { label: "Browser identity", value: "Not connected", tone: "critical" },
        { label: "Copilot runtime", value: "Unavailable", tone: "warning" },
        { label: "Workspace access", value: "Sign-in required", tone: "critical" },
      ];
    case "operational":
    default:
      return [
        { label: "Browser identity", value: "Connected", tone: "good" },
        { label: "Copilot runtime", value: "Ready", tone: "good" },
        { label: "Workspace access", value: "Open", tone: "good" },
      ];
  }
}
