import {
  Button,
  makeStyles,
  shorthands,
} from "@fluentui/react-components";
import { apiBaseUrl } from "./api";
import type { AuthUser, CopilotStatus } from "./api";
import { getCopilotStatusCopy } from "./authPresentation";

const useLocalStyles = makeStyles({
  root: {
    position: "relative",
    minHeight: "100vh",
    display: "grid",
    alignItems: "center",
    overflow: "hidden",
    ...shorthands.padding("40px", "24px"),
  },
  ambient: {
    position: "absolute",
    inset: 0,
    background:
      "radial-gradient(circle at 18% 18%, rgba(131, 207, 255, 0.18), transparent 24%), radial-gradient(circle at 84% 16%, rgba(127, 107, 255, 0.14), transparent 22%), radial-gradient(circle at 68% 82%, rgba(217, 166, 103, 0.12), transparent 26%)",
    pointerEvents: "none",
  },
  layout: {
    position: "relative",
    width: "min(1160px, 100%)",
    margin: "0 auto",
    display: "grid",
    gridTemplateColumns: "minmax(0, 1.05fr) minmax(360px, 0.95fr)",
    gap: "28px",
    alignItems: "stretch",
    "@media (max-width: 960px)": {
      gridTemplateColumns: "1fr",
    },
  },
  story: {
    position: "relative",
    display: "grid",
    alignContent: "space-between",
    gap: "28px",
    minHeight: "560px",
    border: "1px solid rgba(163, 180, 208, 0.16)",
    background:
      "linear-gradient(180deg, rgba(13, 22, 37, 0.88), rgba(8, 14, 24, 0.96))",
    boxShadow: "0 32px 90px rgba(0, 0, 0, 0.34)",
    ...shorthands.borderRadius("32px"),
    ...shorthands.padding("36px"),
  },
  storyHeader: {
    display: "grid",
    gap: "16px",
    maxWidth: "580px",
  },
  eyebrow: {
    display: "inline-flex",
    alignItems: "center",
    width: "fit-content",
    color: "#f3d4a8",
    backgroundColor: "rgba(217, 166, 103, 0.12)",
    border: "1px solid rgba(217, 166, 103, 0.22)",
    ...shorthands.borderRadius("999px"),
    ...shorthands.padding("8px", "14px"),
    fontSize: "11px",
    fontWeight: 700,
    letterSpacing: "0.14em",
    textTransform: "uppercase",
  },
  brand: {
    margin: 0,
    color: "#f8fbff",
    fontFamily: "var(--heading)",
    fontSize: "clamp(3rem, 6vw, 5.2rem)",
    lineHeight: 0.94,
    letterSpacing: "-0.05em",
  },
  lede: {
    margin: 0,
    maxWidth: "44rem",
    color: "#aec0de",
    fontSize: "17px",
    lineHeight: 1.75,
  },
  storyGrid: {
    display: "grid",
    gridTemplateColumns: "repeat(3, minmax(0, 1fr))",
    gap: "14px",
    "@media (max-width: 960px)": {
      gridTemplateColumns: "1fr",
    },
  },
  storyCard: {
    display: "grid",
    gap: "10px",
    alignContent: "start",
    minHeight: "132px",
    border: "1px solid rgba(163, 180, 208, 0.12)",
    background: "linear-gradient(180deg, rgba(255, 255, 255, 0.04), rgba(255, 255, 255, 0.02))",
    ...shorthands.borderRadius("24px"),
    ...shorthands.padding("18px"),
  },
  storyCardIndex: {
    color: "#f3d4a8",
    fontSize: "11px",
    fontWeight: 700,
    letterSpacing: "0.12em",
    textTransform: "uppercase",
  },
  storyCardTitle: {
    color: "#eef4ff",
    fontSize: "18px",
    fontWeight: 700,
    letterSpacing: "-0.02em",
  },
  storyCardBody: {
    color: "#8da3c4",
    fontSize: "13px",
    lineHeight: 1.7,
  },
  panel: {
    position: "relative",
    display: "grid",
    alignContent: "start",
    gap: "24px",
    border: "1px solid rgba(163, 180, 208, 0.16)",
    background:
      "linear-gradient(180deg, rgba(18, 30, 48, 0.92), rgba(9, 15, 25, 0.98))",
    boxShadow: "0 32px 90px rgba(0, 0, 0, 0.38)",
    ...shorthands.borderRadius("32px"),
    ...shorthands.padding("34px"),
  },
  panelDegraded: {
    border: "1px solid rgba(217, 166, 103, 0.28)",
    boxShadow: "0 32px 90px rgba(0, 0, 0, 0.38), inset 0 1px 0 rgba(255, 255, 255, 0.04)",
  },
  statusPill: {
    display: "inline-flex",
    alignItems: "center",
    width: "fit-content",
    color: "#c7def7",
    backgroundColor: "rgba(131, 207, 255, 0.1)",
    border: "1px solid rgba(131, 207, 255, 0.2)",
    ...shorthands.borderRadius("999px"),
    ...shorthands.padding("8px", "14px"),
    fontSize: "11px",
    fontWeight: 700,
    letterSpacing: "0.12em",
    textTransform: "uppercase",
  },
  statusPillWarning: {
    color: "#f3d4a8",
    backgroundColor: "rgba(217, 166, 103, 0.1)",
    border: "1px solid rgba(217, 166, 103, 0.22)",
  },
  identity: {
    display: "inline-flex",
    alignItems: "center",
    gap: "10px",
    width: "fit-content",
    color: "#f0f5ff",
    backgroundColor: "rgba(255, 255, 255, 0.04)",
    border: "1px solid rgba(255, 255, 255, 0.08)",
    ...shorthands.borderRadius("16px"),
    ...shorthands.padding("10px", "14px"),
    fontSize: "13px",
  },
  identityDot: {
    width: "10px",
    height: "10px",
    backgroundColor: "#83cfff",
    boxShadow: "0 0 0 4px rgba(131, 207, 255, 0.12)",
    ...shorthands.borderRadius("999px"),
  },
  title: {
    margin: 0,
    color: "#f8fbff",
    fontFamily: "var(--heading)",
    fontSize: "clamp(2.2rem, 4vw, 3.3rem)",
    lineHeight: 1,
    letterSpacing: "-0.05em",
  },
  description: {
    margin: 0,
    color: "#aec0de",
    fontSize: "15px",
    lineHeight: 1.8,
  },
  detailList: {
    display: "grid",
    gap: "10px",
  },
  detailRow: {
    display: "grid",
    gridTemplateColumns: "10px 1fr",
    gap: "12px",
    alignItems: "start",
    color: "#d7e2f4",
    fontSize: "13px",
    lineHeight: 1.7,
  },
  detailDot: {
    width: "10px",
    height: "10px",
    marginTop: "5px",
    background: "linear-gradient(135deg, #d9a667, #83cfff)",
    ...shorthands.borderRadius("999px"),
  },
  actionRow: {
    display: "grid",
    gap: "12px",
    marginTop: "8px",
  },
  button: {
    width: "100%",
    minHeight: "48px",
    fontSize: "15px",
    fontWeight: 700,
    letterSpacing: "0.01em",
  },
  supportingNote: {
    color: "#7f94b6",
    fontSize: "12px",
    lineHeight: 1.7,
  },
});

interface LoginPageProps {
  copilotStatus?: CopilotStatus;
  user?: AuthUser | null;
}

const STORY_CARDS = [
  {
    index: "01",
    title: "Room-by-room command",
    body: "Keep planning, review, and delivery visible in one shared surface instead of juggling separate tools.",
  },
  {
    index: "02",
    title: "Task branches with memory",
    body: "Move from discussion into implementation without losing project context, branch history, or the current spec.",
  },
  {
    index: "03",
    title: "Operational signal first",
    body: "When Copilot drops out, the UI should explain the situation immediately and tell you exactly how to recover.",
  },
];

export default function LoginPage({
  copilotStatus = "unavailable",
  user = null,
}: LoginPageProps) {
  const s = useLocalStyles();
  const loginUrl = `${apiBaseUrl}/api/auth/login`;
  const copy = getCopilotStatusCopy(copilotStatus, user);
  const userName = user?.name ?? user?.login;
  const degraded = copilotStatus === "degraded";

  return (
    <div className={s.root}>
      <div className={s.ambient} />
      <div className={s.layout}>
        <section className={s.story}>
          <div className={s.storyHeader}>
            <div className={s.eyebrow}>Collaborative delivery cockpit</div>
            <h1 className={s.brand}>Agent Academy</h1>
            <p className={s.lede}>
              A studio for spec-aware, branch-based software work. The interface should feel calm when the
              system is healthy and unmistakably clear when the agent runtime is not.
            </p>
          </div>

          <div className={s.storyGrid}>
            {STORY_CARDS.map((card) => (
              <div key={card.index} className={s.storyCard}>
                <div className={s.storyCardIndex}>{card.index}</div>
                <div className={s.storyCardTitle}>{card.title}</div>
                <div className={s.storyCardBody}>{card.body}</div>
              </div>
            ))}
          </div>
        </section>

        <section className={`${s.panel} ${degraded ? s.panelDegraded : ""}`}>
          <div className={`${s.statusPill} ${degraded ? s.statusPillWarning : ""}`}>{copy.eyebrow}</div>

          {userName && (
            <div className={s.identity}>
              <span className={s.identityDot} />
              Connected as <strong>{userName}</strong>
            </div>
          )}

          <h2 className={s.title}>{copy.title}</h2>
          <p className={s.description}>{copy.description}</p>

          <div className={s.detailList}>
            <div className={s.detailRow}>
              <span className={s.detailDot} />
              <span>Workspace access stays fail-closed until Copilot is fully operational.</span>
            </div>
            <div className={s.detailRow}>
              <span className={s.detailDot} />
              <span>
                {degraded
                  ? "Your browser identity is still available, but agent execution is paused until you reconnect GitHub."
                  : "Signing in restores room access, active workspace state, and the normal agent workflow."}
              </span>
            </div>
          </div>

          <div className={s.actionRow}>
            <Button className={s.button} appearance="primary" as="a" href={loginUrl}>
              {copy.actionLabel}
            </Button>
            <div className={s.supportingNote}>{copy.supportingNote}</div>
          </div>
        </section>
      </div>
    </div>
  );
}
