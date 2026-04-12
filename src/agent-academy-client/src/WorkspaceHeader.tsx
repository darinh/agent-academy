import { mergeClasses } from "@fluentui/react-components";
import type { CircuitBreakerState } from "./useCircuitBreakerPolling";

export interface HeaderModel {
  title: string;
  meta: string | null;
  showPhasePill: boolean;
  workspaceLimited: boolean;
  degradedEyebrow: string | null;
  circuitBreakerState: CircuitBreakerState;
}

export interface WorkspaceHeaderProps {
  model: HeaderModel;
  styles: Record<string, string>;
}

export default function WorkspaceHeader({ model, styles: s }: WorkspaceHeaderProps) {
  return (
    <div className={s.workspaceHeader}>
      <div className={s.workspaceHeaderBody}>
        <div className={s.workspaceHeaderTopRow}>
          <div className={s.workspaceTitle}>{model.title}</div>
          {model.meta && (<>
            <span className={s.headerDivider} />
            <span className={s.workspaceMetaText}>{model.meta}</span>
          </>)}
          <div style={{ flex: 1 }} />
          <div className={s.workspaceHeaderSignals}>
            {model.workspaceLimited && (
              <div className={mergeClasses(s.workspaceSignal, s.workspaceSignalWarning)}>
                {model.degradedEyebrow ?? "Limited mode"}
              </div>
            )}
            {model.circuitBreakerState && model.circuitBreakerState !== "Closed" && (
              <div className={mergeClasses(s.workspaceSignal, s.workspaceSignalWarning)}>
                Circuit {model.circuitBreakerState === "Open" ? "open" : "probing"}
              </div>
            )}
            {model.showPhasePill && (
              <div className={s.phasePill}>
                <span className={s.phasePillDot} />
                Connected
              </div>
            )}
          </div>
        </div>
      </div>
    </div>
  );
}
