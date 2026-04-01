import {
  Button,
  makeStyles,
  shorthands,
} from "@fluentui/react-components";
import { apiBaseUrl } from "./api";
import type { AuthUser, CopilotStatus } from "./api";

const useLocalStyles = makeStyles({
  root: {
    display: "flex",
    flexDirection: "column",
    alignItems: "center",
    justifyContent: "center",
    height: "100vh",
    width: "100%",
    gap: "32px",
    backgroundColor: "#0d1117",
  },
  card: {
    display: "flex",
    flexDirection: "column",
    alignItems: "center",
    gap: "24px",
    ...shorthands.padding("48px", "64px"),
    ...shorthands.borderRadius("20px"),
    border: "1px solid rgba(155, 176, 210, 0.12)",
    backgroundColor: "rgba(255, 255, 255, 0.02)",
    maxWidth: "420px",
  },
  title: {
    fontSize: "28px",
    fontWeight: 700,
    color: "#eff5ff",
    letterSpacing: "-0.03em",
  },
  subtitle: {
    fontSize: "14px",
    color: "#7c90b2",
    textAlign: "center" as const,
    lineHeight: "1.6",
  },
  button: {
    minWidth: "240px",
    height: "44px",
    fontSize: "15px",
    fontWeight: 600,
  },
  githubIcon: {
    width: "20px",
    height: "20px",
    marginRight: "8px",
  },
});

interface LoginPageProps {
  copilotStatus?: CopilotStatus;
  user?: AuthUser | null;
}

export default function LoginPage({
  copilotStatus = "unavailable",
  user = null,
}: LoginPageProps) {
  const s = useLocalStyles();

  const loginUrl = `${apiBaseUrl}/api/auth/login`;
  const userName = user?.name ?? user?.login;
  const reconnecting = copilotStatus === "degraded";
  const subtitle = reconnecting
    ? `${userName ? `${userName}'s` : "Your"} GitHub session is still present, but Copilot access needs to be refreshed. Sign in again to restore agent functionality.`
    : "Multi-agent collaboration platform.\nSign in with GitHub to get started.";

  return (
    <div className={s.root}>
      <div className={s.card}>
        <div className={s.title}>🏛️ Agent Academy</div>
        <div className={s.subtitle}>
          {subtitle.split("\n").map((line) => (
            <div key={line}>{line}</div>
          ))}
        </div>
        <Button
          className={s.button}
          appearance="primary"
          as="a"
          href={loginUrl}
          icon={
            <svg className={s.githubIcon} viewBox="0 0 24 24" fill="currentColor">
              <path d="M12 0C5.37 0 0 5.37 0 12c0 5.31 3.435 9.795 8.205 11.385.6.105.825-.255.825-.57 0-.285-.015-1.23-.015-2.235-3.015.555-3.795-.735-4.035-1.41-.135-.345-.72-1.41-1.23-1.695-.42-.225-1.02-.78-.015-.795.945-.015 1.62.87 1.845 1.23 1.08 1.815 2.805 1.305 3.495.99.105-.78.42-1.305.765-1.605-2.67-.3-5.46-1.335-5.46-5.925 0-1.305.465-2.385 1.23-3.225-.12-.3-.54-1.53.12-3.18 0 0 1.005-.315 3.3 1.23.96-.27 1.98-.405 3-.405s2.04.135 3 .405c2.295-1.56 3.3-1.23 3.3-1.23.66 1.65.24 2.88.12 3.18.765.84 1.23 1.905 1.23 3.225 0 4.605-2.805 5.625-5.475 5.925.435.375.81 1.095.81 2.22 0 1.605-.015 2.895-.015 3.3 0 .315.225.69.825.57A12.02 12.02 0 0 0 24 12c0-6.63-5.37-12-12-12z" />
            </svg>
          }
        >
          {reconnecting ? "Reconnect GitHub" : "Login with GitHub"}
        </Button>
      </div>
    </div>
  );
}
