import { Button, Spinner } from "@fluentui/react-components";
import { ShieldCheckmarkRegular } from "@fluentui/react-icons";
import type { EvidenceRow } from "../api";
import V3Badge from "../V3Badge";
import { evidencePhaseBadge } from "./taskListHelpers";
import { useTaskDetailStyles } from "./taskDetailStyles";

interface EvidenceLedgerProps {
  evidence: EvidenceRow[];
  loading: boolean;
  loaded: boolean;
  onLoad: () => void;
}

export default function EvidenceLedger({ evidence, loading, loaded, onLoad }: EvidenceLedgerProps) {
  const s = useTaskDetailStyles();
  return (
    <div className={s.commentsSection}>
      <div className={s.sectionLabel} style={{ display: "flex", alignItems: "center", gap: "8px" }}>
        <ShieldCheckmarkRegular fontSize={13} />
        Evidence Ledger
        {!loaded && (
          <Button size="small" appearance="subtle" onClick={onLoad} disabled={loading}>
            {loading ? <Spinner size="tiny" /> : "Load"}
          </Button>
        )}
      </div>
      {loaded && evidence.length === 0 && (
        <div style={{ fontSize: "12px", color: "var(--aa-muted)", marginTop: "4px" }}>No evidence recorded</div>
      )}
      {evidence.length > 0 && (
        <table className={s.evidenceTable}>
          <thead>
            <tr>
              <th className={s.evidenceTh}>Phase</th>
              <th className={s.evidenceTh}>Check</th>
              <th className={s.evidenceTh}>Result</th>
              <th className={s.evidenceTh}>Tool</th>
              <th className={s.evidenceTh}>Agent</th>
            </tr>
          </thead>
          <tbody>
            {evidence.map((ev) => (
              <tr key={ev.id}>
                <td className={s.evidenceTd}>
                  <V3Badge color={evidencePhaseBadge(ev.phase)}>{ev.phase}</V3Badge>
                </td>
                <td className={s.evidenceTd}>{ev.checkName}</td>
                <td className={s.evidenceTd}>
                  <V3Badge color={ev.passed ? "ok" : "err"}>
                    {ev.passed ? "Pass" : "Fail"}
                  </V3Badge>
                </td>
                <td className={s.evidenceTd} title={ev.command ?? undefined}>{ev.tool}</td>
                <td className={s.evidenceTd}>{ev.agentName}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  );
}
