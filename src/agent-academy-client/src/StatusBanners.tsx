import {
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
} from "@fluentui/react-components";
import type { RecoveryBannerState } from "./RecoveryBanner";
import RecoveryBanner from "./RecoveryBanner";
import CircuitBreakerBanner from "./CircuitBreakerBanner";
import type { CircuitBreakerState } from "./useCircuitBreakerPolling";

export interface StatusBannersProps {
  err: string | null;
  switchError: string;
  recoveryBanner: RecoveryBannerState | null;
  circuitBreakerState: CircuitBreakerState;
  connectionDetail: string | null;
  styles: Record<string, string>;
}

export default function StatusBanners({
  err,
  switchError,
  recoveryBanner,
  circuitBreakerState,
  connectionDetail,
  styles: s,
}: StatusBannersProps) {
  return (
    <>
      {(err || switchError) && (
        <div className={s.errorBar}>
          <MessageBar intent="error">
            <MessageBarBody>
              <MessageBarTitle>Error</MessageBarTitle>
              {err || switchError}
            </MessageBarBody>
          </MessageBar>
        </div>
      )}

      {recoveryBanner && (
        <div className={s.recoveryBannerGlobal}>
          <RecoveryBanner state={recoveryBanner} />
        </div>
      )}

      <CircuitBreakerBanner state={circuitBreakerState} />

      {connectionDetail && (
        <div className={s.errorBar}>
          <MessageBar intent="warning">
            <MessageBarBody>
              <MessageBarTitle>Offline</MessageBarTitle>
              {connectionDetail}
            </MessageBarBody>
          </MessageBar>
        </div>
      )}
    </>
  );
}
