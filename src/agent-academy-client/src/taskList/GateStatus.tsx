import { Button, mergeClasses, Spinner } from "@fluentui/react-components";
import { ShieldCheckmarkRegular } from "@fluentui/react-icons";
import type { GateCheckResult } from "../api";
import V3Badge from "../V3Badge";
import { useTaskDetailStyles } from "./taskDetailStyles";

interface GateStatusProps {
  gate: GateCheckResult | null;
  loading: boolean;
  onCheck: () => void;
}

export default function GateStatus({ gate, loading, onCheck }: GateStatusProps) {
  const s = useTaskDetailStyles();
  return (
    <div className={s.commentsSection}>
      <div className={s.sectionLabel} style={{ display: "flex", alignItems: "center", gap: "8px" }}>
        <ShieldCheckmarkRegular fontSize={13} />
        Gate Status
        <Button size="small" appearance="subtle" onClick={onCheck} disabled={loading}>
          {loading ? <Spinner size="tiny" /> : gate ? "Recheck" : "Check Gates"}
        </Button>
      </div>
      {gate && (
        <div className={mergeClasses(s.gateBox, gate.met ? s.gateMetBorder : s.gateNotMetBorder)}>
          <V3Badge color={gate.met ? "ok" : "warn"}>
            {gate.met ? "Gate met" : `${gate.passedChecks}/${gate.requiredChecks} required`}
          </V3Badge>
          <span style={{ fontSize: "11px", color: "var(--aa-muted)" }}>
            {gate.currentPhase} → {gate.targetPhase}
          </span>
          {gate.missingChecks.length > 0 && (
            <span style={{ fontSize: "11px", color: "var(--aa-soft)" }}>
              Missing: {gate.missingChecks.join(", ")}
            </span>
          )}
        </div>
      )}
    </div>
  );
}
