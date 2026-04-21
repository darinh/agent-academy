import { Button, Input, Spinner } from "@fluentui/react-components";
import { ArrowResetRegular, SaveRegular } from "@fluentui/react-icons";
import type { QuotaStatus } from "../api";
import type { useAgentConfigCardStyles } from "./AgentConfigCardStyles";

interface QuotaSectionProps {
  s: ReturnType<typeof useAgentConfigCardStyles>;
  quota: QuotaStatus | null;
  hasQuotaConfigured: boolean;
  hasQuotaChanges: boolean;
  maxRequestsPerHour: string;
  maxTokensPerHour: string;
  maxCostPerHour: string;
  onMaxRequestsChange: (value: string) => void;
  onMaxTokensChange: (value: string) => void;
  onMaxCostChange: (value: string) => void;
  quotaSaving: boolean;
  onSave: () => void;
  onRemove: () => void;
}

export default function QuotaSection({
  s,
  quota,
  hasQuotaConfigured,
  hasQuotaChanges,
  maxRequestsPerHour,
  maxTokensPerHour,
  maxCostPerHour,
  onMaxRequestsChange,
  onMaxTokensChange,
  onMaxCostChange,
  quotaSaving,
  onSave,
  onRemove,
}: QuotaSectionProps) {
  return (
    <div className={s.quotaSection}>
      <div className={s.quotaHeader}>
        <span className={s.quotaSectionLabel}>Resource Quotas</span>
        {hasQuotaConfigured && (
          <Button
            appearance="subtle"
            size="small"
            icon={<ArrowResetRegular />}
            disabled={quotaSaving}
            onClick={onRemove}
          >
            Remove Limits
          </Button>
        )}
      </div>

      {!hasQuotaConfigured && !hasQuotaChanges && (
        <span className={s.quotaUnlimited}>No limits configured — agent has unlimited access.</span>
      )}

      <div className={s.quotaGrid}>
        <div className={s.fieldGroup}>
          <label className={s.fieldLabel}>Max Requests / Hour</label>
          <Input
            type="number"
            placeholder="Unlimited"
            value={maxRequestsPerHour}
            onChange={(_, data) => onMaxRequestsChange(data.value)}
            min={0}
          />
          {quota?.currentUsage != null && (
            <span className={s.quotaUsage}>
              Current: {quota.currentUsage.requestCount} requests this hour
            </span>
          )}
        </div>
        <div className={s.fieldGroup}>
          <label className={s.fieldLabel}>Max Tokens / Hour</label>
          <Input
            type="number"
            placeholder="Unlimited"
            value={maxTokensPerHour}
            onChange={(_, data) => onMaxTokensChange(data.value)}
            min={0}
          />
          {quota?.currentUsage != null && (
            <span className={s.quotaUsage}>
              Current: {quota.currentUsage.totalTokens.toLocaleString()} tokens this hour
            </span>
          )}
        </div>
        <div className={s.fieldGroup}>
          <label className={s.fieldLabel}>Max Cost / Hour ($)</label>
          <Input
            type="number"
            placeholder="Unlimited"
            value={maxCostPerHour}
            onChange={(_, data) => onMaxCostChange(data.value)}
            min={0}
            step={0.01}
          />
          {quota?.currentUsage != null && (
            <span className={s.quotaUsage}>
              Current: ${quota.currentUsage.totalCost.toFixed(4)} this hour
            </span>
          )}
        </div>
      </div>

      <div className={s.actions}>
        <Button
          appearance="primary"
          size="small"
          icon={<SaveRegular />}
          disabled={quotaSaving || !hasQuotaChanges}
          onClick={onSave}
        >
          {quotaSaving ? <Spinner size="tiny" /> : "Save Quotas"}
        </Button>
      </div>
    </div>
  );
}
