using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Tests for <see cref="CsvExportService"/> — CSV formatting, escaping,
/// formula injection protection, and edge cases.
/// </summary>
public sealed class CsvExportTests
{
    // ── AgentSummaryToCsv ──

    [Fact]
    public void AgentSummaryToCsv_EmptyAgents_ReturnsHeaderOnly()
    {
        var summary = new AgentAnalyticsSummary(
            Agents: new List<AgentPerformanceMetrics>(),
            WindowStart: DateTimeOffset.UtcNow.AddHours(-1),
            WindowEnd: DateTimeOffset.UtcNow,
            TotalRequests: 0, TotalCost: 0, TotalErrors: 0);

        var csv = CsvExportService.AgentSummaryToCsv(summary);

        Assert.StartsWith("AgentId,AgentName,", csv);
        // Only header line + trailing CRLF
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);
    }

    [Fact]
    public void AgentSummaryToCsv_SingleAgent_CorrectColumns()
    {
        var agent = new AgentPerformanceMetrics(
            AgentId: "planner-1",
            AgentName: "Planner",
            TotalRequests: 42,
            TotalInputTokens: 10000,
            TotalOutputTokens: 5000,
            TotalCost: 1.234567,
            AverageResponseTimeMs: 350.5,
            TotalErrors: 2,
            RecoverableErrors: 1,
            UnrecoverableErrors: 1,
            TasksAssigned: 5,
            TasksCompleted: 3,
            TokenTrend: Enumerable.Repeat(0L, 12).ToList());

        var summary = new AgentAnalyticsSummary(
            Agents: new List<AgentPerformanceMetrics> { agent },
            WindowStart: DateTimeOffset.UtcNow.AddHours(-1),
            WindowEnd: DateTimeOffset.UtcNow,
            TotalRequests: 42, TotalCost: 1.234567, TotalErrors: 2);

        var csv = CsvExportService.AgentSummaryToCsv(summary);
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(2, lines.Length); // header + 1 data row
        var dataFields = lines[1].Split(',');
        Assert.Equal("planner-1", dataFields[0]);
        Assert.Equal("Planner", dataFields[1]);
        Assert.Equal("42", dataFields[2]);
        Assert.Equal("10000", dataFields[3]);
        Assert.Equal("5000", dataFields[4]);
        Assert.Equal("1.234567", dataFields[5]);
        Assert.Equal("350.50", dataFields[6]);
        Assert.Equal("2", dataFields[7]);
        Assert.Equal("5", dataFields[10]); // TasksAssigned
        Assert.Equal("3", dataFields[11]); // TasksCompleted
    }

    [Fact]
    public void AgentSummaryToCsv_NullAverageResponseTime_EmptyField()
    {
        var agent = new AgentPerformanceMetrics(
            "a1", "Agent", 1, 100, 50, 0.01, null, 0, 0, 0, 0, 0,
            Enumerable.Repeat(0L, 12).ToList());

        var summary = new AgentAnalyticsSummary(
            new List<AgentPerformanceMetrics> { agent },
            DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow,
            1, 0.01, 0);

        var csv = CsvExportService.AgentSummaryToCsv(summary);
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var fields = lines[1].Split(',');

        // AverageResponseTimeMs is column index 6 — should be empty
        Assert.Equal("", fields[6]);
    }

    // ── UsageRecordsToCsv ──

    [Fact]
    public void UsageRecordsToCsv_EmptyRecords_ReturnsHeaderOnly()
    {
        var csv = CsvExportService.UsageRecordsToCsv(new List<LlmUsageRecord>());
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);
        Assert.StartsWith("Id,AgentId,", lines[0]);
    }

    [Fact]
    public void UsageRecordsToCsv_SingleRecord_CorrectFormat()
    {
        var record = new LlmUsageRecord(
            Id: "rec-1",
            AgentId: "coder-1",
            RoomId: "room-1",
            Model: "gpt-4",
            InputTokens: 1000,
            OutputTokens: 500,
            CacheReadTokens: 200,
            CacheWriteTokens: 100,
            Cost: 0.05,
            DurationMs: 1200,
            ReasoningEffort: "medium",
            RecordedAt: new DateTime(2026, 4, 11, 12, 0, 0, DateTimeKind.Utc));

        var csv = CsvExportService.UsageRecordsToCsv(new List<LlmUsageRecord> { record });
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(2, lines.Length);
        var fields = lines[1].Split(',');
        Assert.Equal("rec-1", fields[0]);
        Assert.Equal("coder-1", fields[1]);
        Assert.Equal("room-1", fields[2]);
        Assert.Equal("gpt-4", fields[3]);
        Assert.Equal("1000", fields[4]);
        Assert.Equal("500", fields[5]);
        Assert.Contains("2026-04-11", fields[11]); // ISO 8601 date
    }

    [Fact]
    public void UsageRecordsToCsv_NullOptionalFields_EmptyInCsv()
    {
        var record = new LlmUsageRecord(
            "rec-2", "agent-1", null, null,
            100, 50, 0, 0, null, null, null,
            DateTime.UtcNow);

        var csv = CsvExportService.UsageRecordsToCsv(new List<LlmUsageRecord> { record });
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var fields = lines[1].Split(',');

        Assert.Equal("", fields[2]); // RoomId
        Assert.Equal("", fields[3]); // Model
        Assert.Equal("", fields[8]); // Cost
        Assert.Equal("", fields[9]); // DurationMs
        Assert.Equal("", fields[10]); // ReasoningEffort
    }

    // ── CSV Escaping ──

    [Fact]
    public void Escape_NullValue_ReturnsEmpty()
    {
        Assert.Equal("", CsvExportService.Escape(null));
    }

    [Fact]
    public void Escape_PlainText_ReturnsUnchanged()
    {
        Assert.Equal("hello", CsvExportService.Escape("hello"));
    }

    [Fact]
    public void Escape_ContainsComma_WrapsInQuotes()
    {
        Assert.Equal("\"hello, world\"", CsvExportService.Escape("hello, world"));
    }

    [Fact]
    public void Escape_ContainsQuote_EscapesDoubleQuote()
    {
        Assert.Equal("\"say \"\"hi\"\"\"", CsvExportService.Escape("say \"hi\""));
    }

    [Fact]
    public void Escape_ContainsNewline_WrapsInQuotes()
    {
        Assert.Equal("\"line1\nline2\"", CsvExportService.Escape("line1\nline2"));
    }

    [Theory]
    [InlineData("=SUM(A1:A10)")]
    [InlineData("+cmd|' /C calc'!A0")]
    [InlineData("-1+2")]
    [InlineData("@import")]
    public void Escape_FormulaInjection_PrefixesSingleQuote(string input)
    {
        var result = CsvExportService.Escape(input);
        Assert.StartsWith("'", result);
        Assert.DoesNotContain("=SUM", result.Substring(0, 1)); // First char is '
    }

    [Fact]
    public void Escape_FormulaWithComma_WrapsInQuotesAndPrefixes()
    {
        var result = CsvExportService.Escape("=SUM(A1,A2)");
        // Should be prefixed with ' and then wrapped in quotes due to comma
        Assert.Equal("\"'=SUM(A1,A2)\"", result);
    }

    // ── CRLF line endings ──

    [Fact]
    public void CsvOutput_UsesCrlfLineEndings()
    {
        var csv = CsvExportService.UsageRecordsToCsv(new List<LlmUsageRecord>());
        Assert.Contains("\r\n", csv);
        // Should not have bare \n (every \n should be preceded by \r)
        var withoutCrlf = csv.Replace("\r\n", "");
        Assert.DoesNotContain("\n", withoutCrlf);
    }

    // ── Multiple agents ──

    [Fact]
    public void AgentSummaryToCsv_MultipleAgents_CorrectRowCount()
    {
        var agents = Enumerable.Range(1, 5).Select(i =>
            new AgentPerformanceMetrics(
                $"agent-{i}", $"Agent {i}", i * 10, i * 1000L, i * 500L,
                i * 0.1, i * 100.0, 0, 0, 0, i, i - 1,
                Enumerable.Repeat(0L, 12).ToList())).ToList();

        var summary = new AgentAnalyticsSummary(
            agents, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow,
            150, 1.5, 0);

        var csv = CsvExportService.AgentSummaryToCsv(summary);
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(6, lines.Length); // 1 header + 5 data rows
    }
}
