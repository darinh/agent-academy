import { makeStyles, shorthands } from "@fluentui/react-components";

export const useRecoveryStyles = makeStyles({
  recoveryBannerGlobal: {
    position: "fixed",
    top: "8px",
    left: "50%",
    transform: "translateX(-50%)",
    zIndex: 1000,
    maxWidth: "480px",
    width: "calc(100% - 32px)",
  },
  recoveryBanner: {
    justifySelf: "stretch",
    display: "grid",
    gap: "8px",
    border: "1px solid var(--aa-border)",
    boxShadow: "var(--aa-shadow)",
    ...shorthands.borderRadius("8px"),
    ...shorthands.padding("12px", "14px"),
  },
  recoveryBannerSyncing: {
    background: "var(--aa-panel)",
    ...shorthands.borderColor("rgba(91, 141, 239, 0.3)"),
  },
  recoveryBannerReconnecting: {
    background: "var(--aa-panel)",
    ...shorthands.borderColor("rgba(255, 152, 0, 0.3)"),
  },
  recoveryBannerCrash: {
    background: "var(--aa-panel)",
    ...shorthands.borderColor("rgba(232, 93, 93, 0.3)"),
  },
  recoveryBannerError: {
    background: "var(--aa-panel)",
    ...shorthands.borderColor("rgba(232, 93, 93, 0.3)"),
  },
  recoveryBannerBadge: {
    display: "inline-flex",
    alignItems: "center",
    gap: "6px",
    width: "fit-content",
    fontFamily: "var(--mono)",
    fontSize: "10px",
    fontWeight: 600,
    color: "var(--aa-text-strong)",
    ...shorthands.borderRadius("4px"),
    ...shorthands.padding("2px", "8px"),
    textTransform: "uppercase",
  },
  recoveryBannerAlert: {
    width: "14px",
    height: "14px",
    display: "inline-grid",
    placeItems: "center",
    ...shorthands.borderRadius("999px"),
    fontSize: "10px",
    fontWeight: 700,
  },
  recoveryBannerBody: {
    display: "grid",
    gap: "4px",
  },
  recoveryBannerMessage: {
    color: "var(--aa-text-strong)",
    fontSize: "13px",
    fontWeight: 600,
  },
  recoveryBannerDetail: {
    color: "var(--aa-soft)",
    fontSize: "12px",
    lineHeight: 1.4,
  },
});
