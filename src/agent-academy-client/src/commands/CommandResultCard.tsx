import V3Badge from "../V3Badge";
import type { CommandExecutionResponse } from "../api";
import type { HumanCommandDefinition } from "../commandCatalog";
import {
  badgeColorForStatus,
  findPrimaryList,
  findPreviewBlock,
  summarizeResult,
} from "../commandsPanelUtils";
import { useCommandsPanelStyles } from "./commandsPanelStyles";

export interface CommandHistoryItem {
  definition: HumanCommandDefinition;
  response: CommandExecutionResponse;
  args?: Record<string, string>;
}

interface CommandResultCardProps {
  item: CommandHistoryItem;
  compact?: boolean;
}

export default function CommandResultCard({ item, compact = false }: CommandResultCardProps) {
  const s = useCommandsPanelStyles();
  const metadata = summarizeResult(item.response.result);
  const arrayEntries = findPrimaryList(item.response.result);
  const preview = findPreviewBlock(item.response.result);
  const argsText = item.args && Object.keys(item.args).length > 0
    ? Object.entries(item.args).map(([key, value]) => `${key}: ${value}`).join(" · ")
    : "No args";

  return (
    <article className={s.historyItem}>
      <div className={s.historyHeader}>
        <div className={s.historyTitleBlock}>
          <div className={s.historyTitle}>{item.definition.title}</div>
          <div className={s.historyMeta}>
            {new Date(item.response.timestamp).toLocaleString()} · {argsText}
          </div>
        </div>
        <div className={s.badgeRow}>
          <V3Badge color={badgeColorForStatus(item.response.status)}>
            {item.response.status}
          </V3Badge>
          <V3Badge color={item.definition.isAsync ? "warn" : "info"}>
            {item.response.command}
          </V3Badge>
        </div>
      </div>

      {item.response.error && (
        <div className={s.errorBox}>
          {item.response.errorCode && (
            <V3Badge color="err" style={{ marginRight: 8 }}>
              {item.response.errorCode}
            </V3Badge>
          )}
          {item.response.error}
        </div>
      )}

      {metadata.length > 0 && (
        <div className={s.summaryGrid}>
          {metadata.map(([label, value]) => (
            <div key={label} className={s.summaryCard}>
              <div className={s.summaryLabel}>{label}</div>
              <div className={s.summaryValue}>{value}</div>
            </div>
          ))}
        </div>
      )}

      {!compact && arrayEntries.length > 0 && (
        <div className={s.recordList}>
          {arrayEntries.map((entry, index) => (
            <div key={index} className={s.recordListItem}>
              <div className={s.recordPrimary}>{entry.primary}</div>
              {entry.secondary && <div className={s.recordSecondary}>{entry.secondary}</div>}
            </div>
          ))}
        </div>
      )}

      {preview && <pre className={s.preview}>{preview}</pre>}
      {!preview && item.response.result != null && (
        <pre className={s.preview}>{JSON.stringify(item.response.result, null, 2)}</pre>
      )}
    </article>
  );
}
