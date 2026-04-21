import { useState, useCallback } from "react";
import Markdown from "react-markdown";
import remarkGfm from "remark-gfm";
import {
  makeStyles,
  shorthands,
} from "@fluentui/react-components";
import type { SprintArtifact, SprintStage } from "../api";
import V3Badge from "../V3Badge";
import { STAGE_META, artifactTypeLabel } from "./sprintConstants";
import { formatElapsed } from "../panelUtils";

const useStyles = makeStyles({
  detailSection: {
    display: "grid",
    gap: "12px",
  },
  sectionTitle: {
    fontFamily: "var(--mono)",
    fontSize: "11px",
    fontWeight: 600,
    textTransform: "uppercase",
    letterSpacing: "0.08em",
    color: "var(--aa-muted)",
  },
  artifactCard: {
    display: "grid",
    gap: "8px",
    ...shorthands.padding("14px"),
    background: "var(--aa-surface)",
    ...shorthands.borderRadius("6px"),
    ...shorthands.border("1px", "solid", "var(--aa-border)"),
  },
  artifactHeader: {
    display: "flex",
    alignItems: "center",
    gap: "8px",
    flexWrap: "wrap",
  },
  artifactTitle: {
    fontSize: "13px",
    fontWeight: 600,
    color: "var(--aa-text)",
  },
  artifactMeta: {
    fontSize: "11px",
    color: "var(--aa-muted)",
    fontFamily: "var(--mono)",
  },
  artifactContent: {
    fontSize: "12px",
    lineHeight: 1.6,
    color: "var(--aa-soft)",
    whiteSpace: "pre-wrap",
    fontFamily: "var(--mono)",
    maxHeight: "300px",
    overflowY: "auto",
    background: "rgba(0,0,0,0.15)",
    ...shorthands.padding("10px"),
    ...shorthands.borderRadius("4px"),
  },
  empty: {
    display: "grid",
    placeItems: "center",
    minHeight: "200px",
    color: "var(--aa-muted)",
    fontSize: "13px",
    textAlign: "center" as const,
    gap: "8px",
  },
  expandToggle: {
    fontSize: "11px",
    color: "var(--aa-cyan, #5b8def)",
    cursor: "pointer",
    fontFamily: "var(--mono)",
    textDecoration: "underline",
    background: "transparent",
    ...shorthands.border("0"),
    ...shorthands.padding("0"),
  },
});

interface ArtifactListProps {
  selectedStage: SprintStage;
  artifacts: SprintArtifact[];
}

export default function ArtifactList({
  selectedStage,
  artifacts,
}: ArtifactListProps) {
  const s = useStyles();
  const [expandedArtifacts, setExpandedArtifacts] = useState<Set<number>>(
    new Set(),
  );

  const toggleArtifact = useCallback((id: number) => {
    setExpandedArtifacts((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  }, []);

  return (
    <div className={s.detailSection}>
      <span className={s.sectionTitle}>
        {STAGE_META[selectedStage].icon}{" "}
        {STAGE_META[selectedStage].label} artifacts
      </span>
      {artifacts.length === 0 ? (
        <div className={s.empty}>
          <span>No artifacts for this stage yet</span>
        </div>
      ) : (
        artifacts.map((artifact) => {
          const expanded = expandedArtifacts.has(artifact.id);
          return (
            <div key={artifact.id} className={s.artifactCard}>
              <div className={s.artifactHeader}>
                <span className={s.artifactTitle}>
                  {artifactTypeLabel(artifact.type)}
                </span>
                <V3Badge color="info">{artifact.stage}</V3Badge>
                {artifact.createdByAgentId && (
                  <V3Badge color="tool">
                    {artifact.createdByAgentId}
                  </V3Badge>
                )}
                <span className={s.artifactMeta}>
                  {formatElapsed(artifact.createdAt)}
                </span>
              </div>
              {artifact.content.length > 200 && !expanded ? (
                <>
                  <div className={s.artifactContent}>
                    <Markdown remarkPlugins={[remarkGfm]}>
                      {artifact.content.slice(0, 200) + "…"}
                    </Markdown>
                  </div>
                  <button
                    className={s.expandToggle}
                    onClick={() => toggleArtifact(artifact.id)}
                  >
                    Show full content
                  </button>
                </>
              ) : (
                <>
                  <div className={s.artifactContent}>
                    <Markdown remarkPlugins={[remarkGfm]}>
                      {artifact.content}
                    </Markdown>
                  </div>
                  {artifact.content.length > 200 && (
                    <button
                      className={s.expandToggle}
                      onClick={() => toggleArtifact(artifact.id)}
                    >
                      Collapse
                    </button>
                  )}
                </>
              )}
            </div>
          );
        })
      )}
    </div>
  );
}
