import { useEffect, useState } from "react";
import { Spinner } from "@fluentui/react-components";
import { getAvailableModels, type ModelsResponse } from "../api";
import { useSettingsStyles } from "./settingsStyles";
import V3Badge from "../V3Badge";

export default function ModelsTab() {
  const shared = useSettingsStyles();
  const [data, setData] = useState<ModelsResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    setLoading(true);
    getAvailableModels()
      .then(setData)
      .catch((e) => setError(e instanceof Error ? e.message : "Failed to load models"))
      .finally(() => setLoading(false));
  }, []);

  if (loading) {
    return <Spinner size="small" label="Loading models…" />;
  }

  if (error) {
    return <div className={shared.errorText}>{error}</div>;
  }

  if (!data) return null;

  return (
    <>
      <div className={shared.sectionTitle}>Available Models</div>

      <div style={{ display: "flex", alignItems: "center", gap: 12, marginBottom: 20 }}>
        <span style={{ fontSize: 13, color: "rgba(148,163,184,0.7)" }}>Executor status:</span>
        <V3Badge color={data.executorOperational ? "ok" : "err"}>
          {data.executorOperational ? "Operational" : "Degraded"}
        </V3Badge>
      </div>

      {data.models.length === 0 ? (
        <div className={shared.emptyState}>No models configured</div>
      ) : (
        <div style={{ display: "flex", flexDirection: "column", gap: 6 }}>
          {data.models.map((m) => (
            <div
              key={m.id}
              style={{
                display: "flex",
                alignItems: "center",
                justifyContent: "space-between",
                padding: "10px 14px",
                borderRadius: 8,
                background: "rgba(0,0,0,0.2)",
                border: "1px solid rgba(255,255,255,0.05)",
              }}
            >
              <span style={{ fontSize: 13, fontWeight: 600, color: "#e2e8f0" }}>{m.name}</span>
              <span style={{ fontSize: 12, fontFamily: "'JetBrains Mono', monospace", color: "rgba(148,163,184,0.5)" }}>
                {m.id}
              </span>
            </div>
          ))}
        </div>
      )}

      <div style={{ marginTop: 16, fontSize: 12, color: "rgba(148,163,184,0.4)" }}>
        {data.models.length} model{data.models.length !== 1 ? "s" : ""} available
      </div>
    </>
  );
}
