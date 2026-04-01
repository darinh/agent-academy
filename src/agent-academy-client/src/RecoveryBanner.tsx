import { memo } from "react";
import { Spinner, mergeClasses } from "@fluentui/react-components";
import { useStyles } from "./useStyles";

export interface RecoveryBannerState {
  tone: "syncing" | "error";
  message: string;
  detail?: string;
}

const RecoveryBanner = memo(function RecoveryBanner(props: {
  state: RecoveryBannerState;
}) {
  const s = useStyles();
  const isSyncing = props.state.tone === "syncing";

  return (
    <div
      className={mergeClasses(
        s.recoveryBanner,
        isSyncing ? s.recoveryBannerSyncing : s.recoveryBannerError,
      )}
      role="status"
      aria-live="polite"
    >
      <div className={s.recoveryBannerBadge}>
        {isSyncing ? <Spinner size="tiny" /> : <span className={s.recoveryBannerAlert}>!</span>}
        <span>{isSyncing ? "Recovery sync" : "Recovery needs attention"}</span>
      </div>
      <div className={s.recoveryBannerBody}>
        <div className={s.recoveryBannerMessage}>{props.state.message}</div>
        {props.state.detail ? <div className={s.recoveryBannerDetail}>{props.state.detail}</div> : null}
      </div>
    </div>
  );
});

export default RecoveryBanner;
