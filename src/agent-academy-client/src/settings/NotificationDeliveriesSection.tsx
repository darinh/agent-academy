import { useCallback, useEffect, useState } from "react";
import { Button, Spinner } from "@fluentui/react-components";
import { ArrowSyncRegular } from "@fluentui/react-icons";
import {
  getNotificationDeliveries,
  getNotificationDeliveryStats,
  type NotificationDeliveryDto,
  type NotificationDeliveryStats,
} from "../api";
import { useSettingsStyles } from "./settingsStyles";
import V3Badge from "../V3Badge";
import { formatTimestamp } from "../panelUtils";

export default function NotificationDeliveriesSection() {
  const shared = useSettingsStyles();
  const [deliveries, setDeliveries] = useState<NotificationDeliveryDto[]>([]);
  const [stats, setStats] = useState<NotificationDeliveryStats | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const fetchData = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const [d, s] = await Promise.all([
        getNotificationDeliveries(30),
        getNotificationDeliveryStats(),
      ]);
      setDeliveries(d);
      setStats(s);
    } catch (e) {
      setError(e instanceof Error ? e.message : "Failed to load deliveries");
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => { fetchData(); }, [fetchData]);

  return (
    <>
      <div style={{ display: "flex", alignItems: "center", justifyContent: "space-between", marginTop: 28, marginBottom: 12 }}>
        <div style={{ fontWeight: 600, color: "#e2e8f0", fontSize: 14 }}>
          Delivery History
        </div>
        <Button
          appearance="subtle"
          size="small"
          icon={<ArrowSyncRegular />}
          onClick={fetchData}
          disabled={loading}
        >
          Refresh
        </Button>
      </div>

      {/* Stats summary */}
      {stats && Object.keys(stats).length > 0 && (
        <div style={{ display: "flex", gap: 8, flexWrap: "wrap", marginBottom: 12 }}>
          {Object.entries(stats).map(([status, count]) => (
            <V3Badge
              key={status}
              color={status.toLowerCase() === "delivered" || status.toLowerCase() === "sent" ? "ok" : status.toLowerCase() === "failed" ? "err" : "muted"}
            >
              {status}: {count}
            </V3Badge>
          ))}
        </div>
      )}

      {loading && <Spinner size="small" label="Loading deliveries…" />}
      {error && <div className={shared.errorText}>{error}</div>}

      {!loading && !error && deliveries.length === 0 && (
        <div className={shared.emptyState}>No deliveries yet</div>
      )}

      {!loading && deliveries.length > 0 && (
        <div style={{ display: "flex", flexDirection: "column", gap: 4, maxHeight: 400, overflowY: "auto" }}>
          {deliveries.map((d) => (
            <div
              key={d.id}
              style={{
                display: "grid",
                gridTemplateColumns: "80px 1fr 80px 120px",
                gap: 12,
                alignItems: "center",
                padding: "8px 12px",
                borderRadius: 6,
                background: "rgba(0,0,0,0.15)",
                border: "1px solid rgba(255,255,255,0.04)",
                fontSize: 12,
              }}
            >
              <V3Badge color={d.status.toLowerCase() === "delivered" || d.status.toLowerCase() === "sent" ? "ok" : d.status.toLowerCase() === "failed" ? "err" : "muted"}>
                {d.status}
              </V3Badge>
              <span style={{ color: "#e2e8f0", overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }}>
                {d.title ?? d.body ?? "—"}
              </span>
              <span style={{ color: "rgba(148,163,184,0.5)" }}>{d.channel}</span>
              <span style={{ color: "rgba(148,163,184,0.4)", textAlign: "right" }}>
                {formatTimestamp(d.attemptedAt)}
              </span>
            </div>
          ))}
        </div>
      )}
    </>
  );
}
