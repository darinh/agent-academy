using System.Globalization;
using System.Text;
using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Converts analytics models to RFC 4180 CSV with formula injection protection.
/// All output uses InvariantCulture and ISO 8601 timestamps.
/// </summary>
public static class CsvExportService
{
    private const string Crlf = "\r\n";

    /// <summary>
    /// Converts agent performance summary to CSV — one row per agent.
    /// </summary>
    public static string AgentSummaryToCsv(AgentAnalyticsSummary summary)
    {
        var sb = new StringBuilder();

        sb.Append("AgentId,AgentName,TotalRequests,TotalInputTokens,TotalOutputTokens,");
        sb.Append("TotalCost,AverageResponseTimeMs,TotalErrors,RecoverableErrors,");
        sb.Append("UnrecoverableErrors,TasksAssigned,TasksCompleted");
        sb.Append(Crlf);

        foreach (var a in summary.Agents)
        {
            sb.Append(Escape(a.AgentId)).Append(',');
            sb.Append(Escape(a.AgentName)).Append(',');
            sb.Append(a.TotalRequests.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(a.TotalInputTokens.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(a.TotalOutputTokens.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(a.TotalCost.ToString("F6", CultureInfo.InvariantCulture)).Append(',');
            sb.Append(FormatNullableDouble(a.AverageResponseTimeMs)).Append(',');
            sb.Append(a.TotalErrors.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(a.RecoverableErrors.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(a.UnrecoverableErrors.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(a.TasksAssigned.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(a.TasksCompleted.ToString(CultureInfo.InvariantCulture));
            sb.Append(Crlf);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Converts individual LLM usage records to CSV — one row per API call.
    /// Returns (csv, truncated) where truncated indicates the result was capped.
    /// </summary>
    public static string UsageRecordsToCsv(List<LlmUsageRecord> records)
    {
        var sb = new StringBuilder();

        sb.Append("Id,AgentId,RoomId,Model,InputTokens,OutputTokens,");
        sb.Append("CacheReadTokens,CacheWriteTokens,Cost,DurationMs,");
        sb.Append("ReasoningEffort,RecordedAt");
        sb.Append(Crlf);

        foreach (var r in records)
        {
            sb.Append(Escape(r.Id)).Append(',');
            sb.Append(Escape(r.AgentId)).Append(',');
            sb.Append(Escape(r.RoomId)).Append(',');
            sb.Append(Escape(r.Model)).Append(',');
            sb.Append(r.InputTokens.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(r.OutputTokens.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(r.CacheReadTokens.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(r.CacheWriteTokens.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(FormatNullableDouble(r.Cost)).Append(',');
            sb.Append(FormatNullableInt(r.DurationMs)).Append(',');
            sb.Append(Escape(r.ReasoningEffort)).Append(',');
            sb.Append(r.RecordedAt.ToString("o", CultureInfo.InvariantCulture));
            sb.Append(Crlf);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Escapes a CSV field per RFC 4180 with formula injection protection.
    /// Fields starting with =, +, -, or @ are prefixed with a single quote.
    /// </summary>
    internal static string Escape(string? value)
    {
        if (value is null)
            return "";

        // Formula injection protection: prefix dangerous characters
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@')
            value = "'" + value;

        // If the field contains a comma, quote, newline, or carriage return, wrap in quotes
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
        {
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        return value;
    }

    private static string FormatNullableDouble(double? value)
    {
        return value.HasValue
            ? value.Value.ToString("F2", CultureInfo.InvariantCulture)
            : "";
    }

    private static string FormatNullableInt(int? value)
    {
        return value.HasValue
            ? value.Value.ToString(CultureInfo.InvariantCulture)
            : "";
    }
}
