import { Spinner } from "@fluentui/react-components";
import { LinkRegular } from "@fluentui/react-icons";
import type { SpecTaskLink } from "../api";
import V3Badge from "../V3Badge";
import { specLinkBadge } from "./taskListHelpers";
import { useTaskDetailStyles } from "./taskDetailStyles";

interface SpecLinksSectionProps {
  specLinks: SpecTaskLink[];
  loading: boolean;
}

export default function SpecLinksSection({ specLinks, loading }: SpecLinksSectionProps) {
  const s = useTaskDetailStyles();
  return (
    <div className={s.commentsSection}>
      <div className={s.sectionLabel}>
        <LinkRegular fontSize={13} style={{ marginRight: 4 }} />
        Spec Links {specLinks.length > 0 ? `(${specLinks.length})` : ""}
      </div>
      {loading && <Spinner size="tiny" label="Loading spec links…" />}
      {!loading && specLinks.length === 0 && (
        <div style={{ fontSize: "12px", color: "var(--aa-muted)", marginTop: "4px" }}>No spec links</div>
      )}
      {specLinks.map((link) => (
        <div key={link.id} className={s.specLinkRow}>
          <V3Badge color={specLinkBadge(link.linkType)}>{link.linkType}</V3Badge>
          <span className={s.specLinkSection}>{link.specSectionId}</span>
          <span style={{ fontSize: "11px", color: "var(--aa-muted)" }}>
            by {link.linkedByAgentName}
          </span>
          {link.note && <span className={s.specLinkNote} title={link.note}>{link.note}</span>}
        </div>
      ))}
    </div>
  );
}
