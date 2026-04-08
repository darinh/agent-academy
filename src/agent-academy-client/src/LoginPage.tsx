import {
  Button,
  makeStyles,
  shorthands,
} from "@fluentui/react-components";
import { apiBaseUrl } from "./api";
import type { AuthUser, CopilotStatus } from "./api";
import { getCopilotStatusCopy, getCopilotStatusFacts, hasDisplayUser } from "./authPresentation";

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
      "radial-gradient(circle at 18% 18%, rgba(91, 141, 239, 0.18), transparent 24%), radial-gradient(circle at 84% 16%, rgba(156, 39, 176, 0.14), transparent 22%), radial-gradient(circle at 68% 82%, rgba(0, 150, 136, 0.12), transparent 26%)",
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
    border: "1px solid var(--aa-hairline)",
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
    color: "var(--aa-cyan)",
    backgroundColor: "rgba(91, 141, 239, 0.12)",
    border: "1px solid rgba(91, 141, 239, 0.22)",
    ...shorthands.borderRadius("999px"),
    ...shorthands.padding("8px", "14px"),
    fontSize: "11px",
    fontWeight: 700,
    letterSpacing: "0.14em",
    textTransform: "uppercase",
  },
  brand: {
    margin: 0,
    color: "var(--aa-text-strong)",
    fontFamily: "var(--heading)",
    fontSize: "clamp(3rem, 6vw, 5.2rem)",
    lineHeight: 0.94,
    letterSpacing: "-0.05em",
  },
  lede: {
    margin: 0,
    maxWidth: "44rem",
    color: "var(--aa-muted)",
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
    border: "1px solid var(--aa-hairline)",
    background: "linear-gradient(180deg, rgba(255, 255, 255, 0.04), rgba(255, 255, 255, 0.02))",
    ...shorthands.borderRadius("24px"),
    ...shorthands.padding("18px"),
  },
  storyCardIndex: {
    color: "var(--aa-cyan)",
    textTransform: "uppercase",
  },
  storyCardTitle: {
    color: "var(--aa-text-strong)",
    fontSize: "18px",
    fontWeight: 700,
  },
  storyCardBody: {
    color: "var(--aa-soft)",
    fontSize: "13px",
    lineHeight: 1.7,
  },
  panel: {
    position: "relative",
    display: "grid",
    alignContent: "start",
    gap: "24px",
    border: "1px solid var(--aa-hairline)",
    background:
      "linear-gradient(180deg, rgba(18, 30, 48, 0.92), rgba(9, 15, 25, 0.98))",
    boxShadow: "0 32px 90px rgba(0, 0, 0, 0.38)",
    ...shorthands.borderRadius("32px"),
    ...shorthands.padding("34px"),
  },
  panelDegraded: {
    border: "1px solid rgba(255, 152, 0, 0.28)",
    boxShadow: "0 32px 90px rgba(0, 0, 0, 0.38), inset 0 1px 0 rgba(255, 255, 255, 0.04)",
  },
  statusPill: {
    display: "inline-flex",
    alignItems: "center",
    width: "fit-content",
    color: "var(--aa-text)",
    backgroundColor: "rgba(91, 141, 239, 0.1)",
    border: "1px solid rgba(91, 141, 239, 0.2)",
    ...shorthands.borderRadius("999px"),
    ...shorthands.padding("8px", "14px"),
    fontSize: "11px",
    fontWeight: 700,
    letterSpacing: "0.12em",
    textTransform: "uppercase",
  },
  statusPillWarning: {
    color: "var(--aa-gold)",
    backgroundColor: "rgba(255, 152, 0, 0.1)",
    border: "1px solid rgba(255, 152, 0, 0.22)",
  },
  identity: {
    display: "inline-flex",
    alignItems: "center",
    gap: "10px",
    width: "fit-content",
    color: "var(--aa-text-strong)",
    backgroundColor: "rgba(255, 255, 255, 0.04)",
    border: "1px solid rgba(255, 255, 255, 0.08)",
    ...shorthands.borderRadius("16px"),
    ...shorthands.padding("10px", "14px"),
    fontSize: "13px",
  },
  identityDot: {
    width: "10px",
    height: "10px",
    backgroundColor: "var(--aa-cyan)",
    boxShadow: "0 0 0 4px rgba(91, 141, 239, 0.12)",
    ...shorthands.borderRadius("999px"),
  },
  title: {
    margin: 0,
    color: "var(--aa-text-strong)",
    fontFamily: "var(--heading)",
    fontSize: "clamp(2.2rem, 4vw, 3.3rem)",
    lineHeight: 1,
    letterSpacing: "-0.05em",
  },
  description: {
    margin: 0,
    color: "var(--aa-muted)",
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
    color: "var(--aa-text)",
    fontSize: "13px",
    lineHeight: 1.7,
  },
  detailDot: {
    width: "10px",
    height: "10px",
    marginTop: "5px",
    background: "linear-gradient(135deg, var(--aa-gold), var(--aa-cyan))",
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
    color: "var(--aa-soft)",
    fontSize: "12px",
    lineHeight: 1.7,
  },
  factGrid: {
    display: "grid",
    gap: "10px",
  },
  factRow: {
    display: "grid",
    gridTemplateColumns: "minmax(0, 1fr) auto",
    gap: "12px",
    alignItems: "center",
    border: "1px solid var(--aa-hairline)",
    background: "var(--aa-bg)",
    ...shorthands.borderRadius("18px"),
    ...shorthands.padding("12px", "14px"),
  },
  factLabel: {
    color: "var(--aa-soft)",
    fontSize: "12px",
    letterSpacing: "0.04em",
    textTransform: "uppercase",
  },
  factValue: {
    width: "fit-content",
    fontSize: "12px",
    fontWeight: 700,
    letterSpacing: "0.04em",
    textTransform: "uppercase",
    ...shorthands.borderRadius("999px"),
    ...shorthands.padding("6px", "10px"),
  },
  factValueGood: {
    color: "var(--aa-lime)",
    backgroundColor: "rgba(76, 175, 80, 0.14)",
    border: "1px solid rgba(76, 175, 80, 0.24)",
  },
  factValueWarning: {
    color: "var(--aa-gold)",
    backgroundColor: "rgba(255, 152, 0, 0.14)",
    border: "1px solid rgba(255, 152, 0, 0.24)",
  },
  factValueCritical: {
    color: "var(--aa-copper)",
    backgroundColor: "rgba(232, 93, 93, 0.14)",
    border: "1px solid rgba(232, 93, 93, 0.24)",
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
  const facts = getCopilotStatusFacts(copilotStatus);
  const userName = user?.name ?? user?.login;

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

        <section className={s.panel}>
          <div className={s.statusPill}>{copy.eyebrow}</div>

          {hasDisplayUser(user) && userName && (
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
              <span>Sign in to restore live rooms, task branches, and the full workspace shell.</span>
            </div>
            <div className={s.detailRow}>
              <span className={s.detailDot} />
              <span>Authentication restores room access, active workspace state, and the normal agent workflow.</span>
            </div>
          </div>

          <div className={s.factGrid} aria-label="System status details">
            {facts.map((fact) => (
              <div key={fact.label} className={s.factRow}>
                <span className={s.factLabel}>{fact.label}</span>
                <span
                  className={[
                    s.factValue,
                    fact.tone === "good"
                      ? s.factValueGood
                      : fact.tone === "warning"
                        ? s.factValueWarning
                        : s.factValueCritical,
                  ].join(" ")}
                >
                  {fact.value}
                </span>
              </div>
            ))}
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
