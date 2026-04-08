import { memo } from "react";
import { Spinner, mergeClasses } from "@fluentui/react-components";
import { useStyles } from "./useStyles";

export interface RecoveryBannerState {
  tone: "syncing" | "reconnecting" | "crash" | "error";
  message: string;
  detail?: string;
}

const TONE_CONFIG: Record<RecoveryBannerState["tone"], {
  styleKey: "recoveryBannerSyncing" | "recoveryBannerError" | "recoveryBannerReconnecting" | "recoveryBannerCrash";
  badge: string;
  showSpinner: boolean;
}> = {
  reconnecting: { styleKey: "recoveryBannerReconnecting", badge: "Reconnecting", showSpinner: true },
  syncing:      { styleKey: "recoveryBannerSyncing",      badge: "Recovery sync", showSpinner: true },
  crash:        { styleKey: "recoveryBannerCrash",         badge: "Crash recovered", showSpinner: false },
  error:        { styleKey: "recoveryBannerError",         badge: "Recovery needs attention", showSpinner: false },
};

const RecoveryBanner = memo(function RecoveryBanner(props: {
  state: RecoveryBannerState;
}) {
  const s = useStyles();
  const config = TONE_CONFIG[props.state.tone];

  return (
    <div
      className={mergeClasses(s.recoveryBanner, s[config.styleKey])}
      role="status"
      aria-live="polite"
    >
      <div className={s.recoveryBannerBadge}>
        {config.showSpinner ? <Spinner size="tiny" /> : <span className={s.recoveryBannerAlert}>!</span>}
        <span>{config.badge}</span>
      </div>
      <div className={s.recoveryBannerBody}>
        <div className={s.recoveryBannerMessage}>{props.state.message}</div>
        {props.state.detail ? <div className={s.recoveryBannerDetail}>{props.state.detail}</div> : null}
      </div>
    </div>
  );
});

export default RecoveryBanner;
