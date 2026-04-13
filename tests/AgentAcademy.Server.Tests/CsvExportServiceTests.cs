using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Tests;

public sealed class CsvExportServiceTests
{
    // ═══════════════════════════════════════════════════════════════════
    //  Escape
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Escape_Null_ReturnsEmpty()
    {
        Assert.Equal("", CsvExportService.Escape(null));
    }

    [Fact]
    public void Escape_SimpleText_ReturnsUnchanged()
    {
        Assert.Equal("hello", CsvExportService.Escape("hello"));
    }

    [Fact]
    public void Escape_EqualsPrefix_AddsSingleQuote()
    {
        Assert.Equal("'=SUM(A1)", CsvExportService.Escape("=SUM(A1)"));
    }

    [Fact]
    public void Escape_PlusPrefix_AddsSingleQuote()
    {
        Assert.Equal("'+cmd", CsvExportService.Escape("+cmd"));
    }

    [Fact]
    public void Escape_MinusPrefix_AddsSingleQuote()
    {
        Assert.Equal("'-value", CsvExportService.Escape("-value"));
    }

    [Fact]
    public void Escape_AtPrefix_AddsSingleQuote()
    {
        Assert.Equal("'@SUM(A1)", CsvExportService.Escape("@SUM(A1)"));
    }

    [Fact]
    public void Escape_ContainsComma_WrapsInQuotes()
    {
        Assert.Equal("\"hello,world\"", CsvExportService.Escape("hello,world"));
    }

    [Fact]
    public void Escape_ContainsQuote_DoublesAndWraps()
    {
        Assert.Equal("\"say \"\"hi\"\"\"", CsvExportService.Escape("say \"hi\""));
    }

    [Fact]
    public void Escape_ContainsNewline_WrapsInQuotes()
    {
        Assert.Equal("\"line1\nline2\"", CsvExportService.Escape("line1\nline2"));
    }

    [Fact]
    public void Escape_InjectionPlusComma_PrefixesAndWraps()
    {
        // Formula prefix is applied first, then quoting kicks in due to comma
        Assert.Equal("\"'=calc,data\"", CsvExportService.Escape("=calc,data"));
    }

    // ═══════════════════════════════════════════════════════════════════
    //  AgentSummaryToCsv
    // ═══════════════════════════════════════════════════════════════════

    private static AgentAnalyticsSummary MakeSummary(params AgentPerformanceMetrics[] agents)
    {
        return new AgentAnalyticsSummary(
            Agents: agents.ToList(),
            WindowStart: DateTimeOffset.UtcNow.AddDays(-7),
            WindowEnd: DateTimeOffset.UtcNow,
            TotalRequests: agents.Sum(a => a.TotalRequests),
            TotalCost: agents.Sum(a => a.TotalCost),
            TotalErrors: agents.Sum(a => a.TotalErrors));
    }

    private static AgentPerformanceMetrics MakeMetrics(
        string id = "agent-1", string name = "Agent One",
        int requests = 10, long inputTokens = 1000, long outputTokens = 500,
        double cost = 0.123456, double? avgResponseMs = 150.5,
        int errors = 1, int recoverable = 1, int unrecoverable = 0,
        int assigned = 3, int completed = 2)
    {
        return new AgentPerformanceMetrics(
            id, name, requests, inputTokens, outputTokens,
            cost, avgResponseMs, errors, recoverable, unrecoverable,
            assigned, completed, []);
    }

    [Fact]
    public void AgentSummaryToCsv_CorrectHeader()
    {
        var csv = CsvExportService.AgentSummaryToCsv(MakeSummary());
        var firstLine = csv.Split("\r\n")[0];

        Assert.Equal(
            "AgentId,AgentName,TotalRequests,TotalInputTokens,TotalOutputTokens," +
            "TotalCost,AverageResponseTimeMs,TotalErrors,RecoverableErrors," +
            "UnrecoverableErrors,TasksAssigned,TasksCompleted",
            firstLine);
    }

    [Fact]
    public void AgentSummaryToCsv_SingleAgent_CorrectDataRow()
    {
        var csv = CsvExportService.AgentSummaryToCsv(MakeSummary(MakeMetrics()));
        var lines = csv.Split("\r\n");

        Assert.Equal(
            "agent-1,Agent One,10,1000,500,0.123456,150.50,1,1,0,3,2",
            lines[1]);
    }

    [Fact]
    public void AgentSummaryToCsv_MultipleAgents_MultipleRows()
    {
        var csv = CsvExportService.AgentSummaryToCsv(MakeSummary(
            MakeMetrics(id: "a1", name: "A1"),
            MakeMetrics(id: "a2", name: "A2")));
        var lines = csv.Split("\r\n");

        // header + 2 data rows + trailing empty
        Assert.Equal(4, lines.Length);
        Assert.StartsWith("a1,", lines[1]);
        Assert.StartsWith("a2,", lines[2]);
    }

    [Fact]
    public void AgentSummaryToCsv_EmptyAgents_HeaderOnly()
    {
        var csv = CsvExportService.AgentSummaryToCsv(MakeSummary());
        var lines = csv.Split("\r\n");

        // header + trailing empty
        Assert.Equal(2, lines.Length);
        Assert.Equal("", lines[1]);
    }

    [Fact]
    public void AgentSummaryToCsv_CostFormatted_SixDecimals()
    {
        var csv = CsvExportService.AgentSummaryToCsv(MakeSummary(
            MakeMetrics(cost: 1.5)));
        var dataLine = csv.Split("\r\n")[1];
        var fields = dataLine.Split(',');

        Assert.Equal("1.500000", fields[5]); // TotalCost
    }

    [Fact]
    public void AgentSummaryToCsv_NullAverageResponseTime_EmptyField()
    {
        var csv = CsvExportService.AgentSummaryToCsv(MakeSummary(
            MakeMetrics(avgResponseMs: null)));
        var dataLine = csv.Split("\r\n")[1];
        var fields = dataLine.Split(',');

        Assert.Equal("", fields[6]); // AverageResponseTimeMs
    }

    // ═══════════════════════════════════════════════════════════════════
    //  UsageRecordsToCsv
    // ═══════════════════════════════════════════════════════════════════

    private static LlmUsageRecord MakeRecord(
        string id = "rec-1", string agentId = "agent-1",
        string? roomId = "room-1", string? model = "gpt-4",
        long input = 100, long output = 50,
        long cacheRead = 0, long cacheWrite = 0,
        double? cost = 0.01, int? durationMs = 200,
        string? reasoningEffort = null,
        DateTime? recordedAt = null)
    {
        return new LlmUsageRecord(
            id, agentId, roomId, model,
            input, output, cacheRead, cacheWrite,
            cost, durationMs, reasoningEffort,
            recordedAt ?? new DateTime(2025, 1, 15, 12, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void UsageRecordsToCsv_CorrectHeader()
    {
        var csv = CsvExportService.UsageRecordsToCsv([]);
        var firstLine = csv.Split("\r\n")[0];

        Assert.Equal(
            "Id,AgentId,RoomId,Model,InputTokens,OutputTokens," +
            "CacheReadTokens,CacheWriteTokens,Cost,DurationMs," +
            "ReasoningEffort,RecordedAt",
            firstLine);
    }

    [Fact]
    public void UsageRecordsToCsv_SingleRecord_CorrectDataRow()
    {
        var csv = CsvExportService.UsageRecordsToCsv([MakeRecord()]);
        var dataLine = csv.Split("\r\n")[1];
        var fields = dataLine.Split(',');

        Assert.Equal("rec-1", fields[0]);
        Assert.Equal("agent-1", fields[1]);
        Assert.Equal("room-1", fields[2]);
        Assert.Equal("gpt-4", fields[3]);
        Assert.Equal("100", fields[4]);
        Assert.Equal("50", fields[5]);
        Assert.Equal("0", fields[6]);
        Assert.Equal("0", fields[7]);
        Assert.Equal("0.01", fields[8]);
        Assert.Equal("200", fields[9]);
    }

    [Fact]
    public void UsageRecordsToCsv_EmptyList_HeaderOnly()
    {
        var csv = CsvExportService.UsageRecordsToCsv([]);
        var lines = csv.Split("\r\n");

        Assert.Equal(2, lines.Length);
        Assert.Equal("", lines[1]);
    }

    [Fact]
    public void UsageRecordsToCsv_Timestamps_Iso8601()
    {
        var ts = new DateTime(2025, 6, 15, 10, 30, 0, DateTimeKind.Utc);
        var csv = CsvExportService.UsageRecordsToCsv([MakeRecord(recordedAt: ts)]);
        var dataLine = csv.Split("\r\n")[1];

        // ISO 8601 "o" format should appear in the last field
        Assert.Contains("2025-06-15T10:30:00", dataLine);
    }

    [Fact]
    public void UsageRecordsToCsv_NullCostAndDuration_EmptyFields()
    {
        var csv = CsvExportService.UsageRecordsToCsv(
            [MakeRecord(cost: null, durationMs: null)]);
        var dataLine = csv.Split("\r\n")[1];
        var fields = dataLine.Split(',');

        Assert.Equal("", fields[8]);  // Cost
        Assert.Equal("", fields[9]);  // DurationMs
    }
}
