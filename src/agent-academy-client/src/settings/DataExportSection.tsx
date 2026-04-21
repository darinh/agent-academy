import { useCallback, useState } from "react";
import { Button, Spinner } from "@fluentui/react-components";
import { ArrowDownloadRegular } from "@fluentui/react-icons";
import { exportAgents, exportUsage } from "../api";

export default function DataExportSection() {
  const [pending, setPending] = useState<string | null>(null);
  const [message, setMessage] = useState<{ ok: boolean; text: string } | null>(null);

  const handleExportAgents = useCallback(async (format: "json" | "csv") => {
    setPending(`agents-${format}`);
    setMessage(null);
    try {
      await exportAgents(format);
      setMessage({ ok: true, text: `Agent data exported as ${format.toUpperCase()}` });
    } catch (e) {
      setMessage({ ok: false, text: e instanceof Error ? e.message : "Export failed" });
    } finally {
      setPending(null);
    }
  }, []);

  const handleExportUsage = useCallback(async (format: "json" | "csv") => {
    setPending(`usage-${format}`);
    setMessage(null);
    try {
      await exportUsage(format);
      setMessage({ ok: true, text: `Usage data exported as ${format.toUpperCase()}` });
    } catch (e) {
      setMessage({ ok: false, text: e instanceof Error ? e.message : "Export failed" });
    } finally {
      setPending(null);
    }
  }, []);

  return (
    <>
      <div style={{ fontWeight: 600, marginTop: 28, marginBottom: 10, color: "#e2e8f0", fontSize: 14 }}>
        Data Export
      </div>
      <div style={{ marginBottom: 14, color: "rgba(148,163,184,0.6)", fontSize: 13, lineHeight: 1.5 }}>
        Download agent configuration and usage analytics for external analysis or backup.
      </div>

      <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
        {/* Agent data */}
        <div style={{ display: "flex", alignItems: "center", gap: 12, flexWrap: "wrap" }}>
          <span style={{ fontSize: 13, color: "#e2e8f0", minWidth: 120 }}>Agent configuration</span>
          <Button
            size="small"
            appearance="subtle"
            icon={pending === "agents-json" ? <Spinner size="tiny" /> : <ArrowDownloadRegular />}
            disabled={pending != null}
            onClick={() => handleExportAgents("json")}
          >
            JSON
          </Button>
          <Button
            size="small"
            appearance="subtle"
            icon={pending === "agents-csv" ? <Spinner size="tiny" /> : <ArrowDownloadRegular />}
            disabled={pending != null}
            onClick={() => handleExportAgents("csv")}
          >
            CSV
          </Button>
        </div>

        {/* Usage data */}
        <div style={{ display: "flex", alignItems: "center", gap: 12, flexWrap: "wrap" }}>
          <span style={{ fontSize: 13, color: "#e2e8f0", minWidth: 120 }}>Usage analytics</span>
          <Button
            size="small"
            appearance="subtle"
            icon={pending === "usage-json" ? <Spinner size="tiny" /> : <ArrowDownloadRegular />}
            disabled={pending != null}
            onClick={() => handleExportUsage("json")}
          >
            JSON
          </Button>
          <Button
            size="small"
            appearance="subtle"
            icon={pending === "usage-csv" ? <Spinner size="tiny" /> : <ArrowDownloadRegular />}
            disabled={pending != null}
            onClick={() => handleExportUsage("csv")}
          >
            CSV
          </Button>
        </div>
      </div>

      {message && (
        <div style={{
          marginTop: 12,
          padding: "6px 12px",
          fontSize: 12,
          borderRadius: 6,
          color: message.ok ? "#4ade80" : "#f87171",
          background: message.ok ? "rgba(74,222,128,0.08)" : "rgba(248,113,113,0.08)",
          border: `1px solid ${message.ok ? "rgba(74,222,128,0.15)" : "rgba(248,113,113,0.15)"}`,
        }}>
          {message.text}
        </div>
      )}
    </>
  );
}
