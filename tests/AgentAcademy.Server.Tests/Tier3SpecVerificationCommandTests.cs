using AgentAcademy.Server.Commands;
using AgentAcademy.Server.Commands.Handlers;
using AgentAcademy.Server.Services;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Tests for Tier 3A Spec Verification command handlers:
/// VerifySpecSectionHandler, CompareSpecToCodeHandler, DetectOrphanedSectionsHandler,
/// and the shared SpecReferenceExtractor.
/// </summary>
[Collection("CwdMutating")]
public sealed class Tier3SpecVerificationCommandTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _specsDir;
    private readonly string _srcDir;

    public Tier3SpecVerificationCommandTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"tier3a-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(Path.Combine(_tempDir, "AgentAcademy.sln"), "");

        // Create specs structure with file path references
        _specsDir = Path.Combine(_tempDir, "specs");

        Directory.CreateDirectory(Path.Combine(_specsDir, "003-agent-system"));
        File.WriteAllText(Path.Combine(_specsDir, "003-agent-system", "spec.md"),
            """
            # 003 — Agent Execution System

            > **Status: Implemented**

            ## Purpose
            Defines how Agent Academy sends prompts to LLM providers.

            ## Implementation

            **File**: `src/AgentAcademy.Server/Services/AgentExecutor.cs`
            **File**: `src/AgentAcademy.Server/Services/Missing.cs`
            **Interface**: `src/AgentAcademy.Server/Services/IAgentExecutor.cs`

            The `RememberHandler.cs` stores memories.
            Uses `CLAIM_TASK` and `RELEASE_TASK` commands.
            Also references `NONEXISTENT_COMMAND`.
            """);

        Directory.CreateDirectory(Path.Combine(_specsDir, "007-agent-commands"));
        File.WriteAllText(Path.Combine(_specsDir, "007-agent-commands", "spec.md"),
            """
            # 007 — Agent Command System

            > **Status: Implemented**

            ## Purpose
            Defines the command pipeline.

            ## Handler Evidence

            **Evidence**: `src/AgentAcademy.Server/Commands/Handlers/RememberHandler.cs`, `src/AgentAcademy.Server/Commands/Handlers/RecallHandler.cs`

            The `OpenSpecHandler.cs` handles OPEN_SPEC.
            """);

        Directory.CreateDirectory(Path.Combine(_specsDir, "014-database-schema"));
        File.WriteAllText(Path.Combine(_specsDir, "014-database-schema", "spec.md"),
            """
            # 014 — Database Schema

            ## Purpose
            Defines the SQLite schema. No file references here.
            """);

        Directory.CreateDirectory(Path.Combine(_specsDir, "099-clean-section"));
        File.WriteAllText(Path.Combine(_specsDir, "099-clean-section", "spec.md"),
            """
            # 099 — Clean Section

            ## Purpose
            All references are valid.

            **File**: `src/AgentAcademy.Server/Services/TaskService.cs`
            """);

        // Create src files that EXIST (for valid references)
        _srcDir = Path.Combine(_tempDir, "src");
        Directory.CreateDirectory(Path.Combine(_srcDir, "AgentAcademy.Server", "Services"));
        File.WriteAllText(Path.Combine(_srcDir, "AgentAcademy.Server", "Services", "AgentExecutor.cs"), "// stub");
        File.WriteAllText(Path.Combine(_srcDir, "AgentAcademy.Server", "Services", "IAgentExecutor.cs"), "// stub");
        File.WriteAllText(Path.Combine(_srcDir, "AgentAcademy.Server", "Services", "TaskService.cs"), "// stub");
        // Note: Missing.cs does NOT exist — deliberately orphaned reference

        Directory.CreateDirectory(Path.Combine(_srcDir, "AgentAcademy.Server", "Commands", "Handlers"));
        File.WriteAllText(Path.Combine(_srcDir, "AgentAcademy.Server", "Commands", "Handlers", "RememberHandler.cs"), "// stub");
        File.WriteAllText(Path.Combine(_srcDir, "AgentAcademy.Server", "Commands", "Handlers", "RecallHandler.cs"), "// stub");
        File.WriteAllText(Path.Combine(_srcDir, "AgentAcademy.Server", "Commands", "Handlers", "OpenSpecHandler.cs"), "// stub");

        RunGit("init");
        RunGit("config user.email test@test.com");
        RunGit("config user.name Test");
        RunGit("add -A");
        RunGit("commit -m init");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private void RunGit(string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git", args)
        {
            WorkingDirectory = _tempDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.WaitForExit();
    }

    private CommandContext MakeContext(IServiceProvider? services = null) => new(
        AgentId: "test-agent",
        AgentName: "Tester",
        AgentRole: "SoftwareEngineer",
        RoomId: "main",
        BreakoutRoomId: null,
        Services: services ?? BuildSpecServices()
    );

    private ServiceProvider BuildSpecServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISpecManager>(new SpecManager(_specsDir));
        return services.BuildServiceProvider();
    }

    private static CommandEnvelope MakeCommand(string name, Dictionary<string, object?> args) => new(
        Command: name,
        Args: args,
        Status: CommandStatus.Success,
        Result: null,
        Error: null,
        CorrelationId: Guid.NewGuid().ToString(),
        Timestamp: DateTime.UtcNow,
        ExecutedBy: "test-agent"
    );

    // ==================== SpecReferenceExtractor ====================

    [Fact]
    public void Extractor_ExtractsLabeledPaths()
    {
        var content = "**File**: `src/AgentAcademy.Server/Services/Foo.cs`\n";
        var paths = SpecReferenceExtractor.ExtractFilePaths(content);
        Assert.Contains("src/AgentAcademy.Server/Services/Foo.cs", paths);
    }

    [Fact]
    public void Extractor_ExtractsMultipleCommaDelimitedPaths()
    {
        var content = "**Files**: `src/A.cs`, `src/B.cs`\n";
        var paths = SpecReferenceExtractor.ExtractFilePaths(content);
        Assert.Contains("src/A.cs", paths);
        Assert.Contains("src/B.cs", paths);
    }

    [Fact]
    public void Extractor_ExtractsEvidencePaths()
    {
        var content = "**Evidence**: `src/Services/Handler.cs`\n";
        var paths = SpecReferenceExtractor.ExtractFilePaths(content);
        Assert.Contains("src/Services/Handler.cs", paths);
    }

    [Fact]
    public void Extractor_ExtractsBacktickPaths()
    {
        var content = "The service lives at `src/AgentAcademy.Server/Services/MyService.cs`.\n";
        var paths = SpecReferenceExtractor.ExtractFilePaths(content);
        Assert.Contains("src/AgentAcademy.Server/Services/MyService.cs", paths);
    }

    [Fact]
    public void Extractor_ExtractsParentheticalPaths()
    {
        var content = "ConversationSessionService (src/AgentAcademy.Server/Services/ConversationSessionService.cs) manages sessions.\n";
        var paths = SpecReferenceExtractor.ExtractFilePaths(content);
        Assert.Contains("src/AgentAcademy.Server/Services/ConversationSessionService.cs", paths);
    }

    [Fact]
    public void Extractor_DeduplicatesPaths()
    {
        var content = """
            **File**: `src/A.cs`
            References `src/A.cs` again.
            """;
        var paths = SpecReferenceExtractor.ExtractFilePaths(content);
        Assert.Single(paths);
    }

    [Fact]
    public void Extractor_ExtractsHandlerNames()
    {
        var content = "`RememberHandler.cs`, `RecallHandler.cs`, and `RememberHandler.cs` again.";
        var handlers = SpecReferenceExtractor.ExtractHandlerNames(content);
        Assert.Contains("RememberHandler", handlers);
        Assert.Contains("RecallHandler", handlers);
        Assert.Equal(2, handlers.Count); // deduplicated
    }

    [Fact]
    public void Extractor_ValidatePaths_ReportsExistenceCorrectly()
    {
        var paths = new[] { "src/AgentAcademy.Server/Services/AgentExecutor.cs", "src/DoesNotExist.cs" };
        var results = SpecReferenceExtractor.ValidatePaths(paths, _tempDir);
        Assert.True(results[0].Exists);
        Assert.False(results[1].Exists);
    }

    [Fact]
    public void Extractor_ValidatePaths_BlocksTraversal()
    {
        var paths = new[] { "src/../../../etc/passwd" };
        var results = SpecReferenceExtractor.ValidatePaths(paths, _tempDir);
        Assert.False(results[0].Exists);
        Assert.Equal("Path traversal blocked", results[0].Reason);
    }

    // ==================== VERIFY_SPEC_SECTION ====================

    [Fact]
    public async Task VerifySpecSection_FindsBrokenReferences()
    {
        var handler = new VerifySpecSectionHandler();
        var oldDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_tempDir);
        try
        {
            var result = await handler.ExecuteAsync(
                MakeCommand("VERIFY_SPEC_SECTION", new() { ["id"] = "003-agent-system" }),
                MakeContext());

            Assert.Equal(CommandStatus.Success, result.Status);
            var dict = (Dictionary<string, object?>)result.Result!;
            Assert.Equal("003-agent-system", dict["sectionId"]);
            Assert.Equal("DRIFT_DETECTED", dict["status"]);

            var broken = (List<Dictionary<string, object?>>)dict["brokenPaths"]!;
            Assert.True(broken.Count > 0, "Should detect at least one broken path");
            Assert.Contains(broken, b => b["path"]?.ToString()?.Contains("Missing.cs") == true);
        }
        finally { Directory.SetCurrentDirectory(oldDir); }
    }

    [Fact]
    public async Task VerifySpecSection_ReportsCleanWhenAllValid()
    {
        var handler = new VerifySpecSectionHandler();
        var oldDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_tempDir);
        try
        {
            var result = await handler.ExecuteAsync(
                MakeCommand("VERIFY_SPEC_SECTION", new() { ["id"] = "099-clean-section" }),
                MakeContext());

            Assert.Equal(CommandStatus.Success, result.Status);
            var dict = (Dictionary<string, object?>)result.Result!;
            Assert.Equal("CLEAN", dict["status"]);
            Assert.Equal(0, dict["broken"]);
        }
        finally { Directory.SetCurrentDirectory(oldDir); }
    }

    [Fact]
    public async Task VerifySpecSection_AcceptsNumericPrefix()
    {
        var handler = new VerifySpecSectionHandler();
        var oldDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_tempDir);
        try
        {
            var result = await handler.ExecuteAsync(
                MakeCommand("VERIFY_SPEC_SECTION", new() { ["id"] = "099" }),
                MakeContext());

            Assert.Equal(CommandStatus.Success, result.Status);
        }
        finally { Directory.SetCurrentDirectory(oldDir); }
    }

    [Fact]
    public async Task VerifySpecSection_AcceptsValueArg()
    {
        var handler = new VerifySpecSectionHandler();
        var oldDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_tempDir);
        try
        {
            var result = await handler.ExecuteAsync(
                MakeCommand("VERIFY_SPEC_SECTION", new() { ["value"] = "099" }),
                MakeContext());

            Assert.Equal(CommandStatus.Success, result.Status);
        }
        finally { Directory.SetCurrentDirectory(oldDir); }
    }

    [Fact]
    public async Task VerifySpecSection_ReturnsError_WhenMissingId()
    {
        var handler = new VerifySpecSectionHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("VERIFY_SPEC_SECTION", new()),
            MakeContext());

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
    }

    [Fact]
    public async Task VerifySpecSection_ReturnsNotFound_ForUnknownSection()
    {
        var handler = new VerifySpecSectionHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("VERIFY_SPEC_SECTION", new() { ["id"] = "999-nonexistent" }),
            MakeContext());

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.NotFound, result.ErrorCode);
    }

    // ==================== COMPARE_SPEC_TO_CODE ====================

    [Fact]
    public async Task CompareSpecToCode_ReportsFilePathClaims()
    {
        var handler = new CompareSpecToCodeHandler();
        var oldDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_tempDir);
        try
        {
            var result = await handler.ExecuteAsync(
                MakeCommand("COMPARE_SPEC_TO_CODE", new() { ["id"] = "003" }),
                MakeContext());

            Assert.Equal(CommandStatus.Success, result.Status);
            var dict = (Dictionary<string, object?>)result.Result!;
            Assert.Equal("003-agent-system", dict["sectionId"]);

            var claims = (List<Dictionary<string, object?>>)dict["claims"]!;
            var fileClaims = claims.Where(c => c["type"]?.ToString() == "file_path").ToList();
            Assert.True(fileClaims.Count >= 2, "Should find file path claims");

            // At least one should be verified, at least one broken
            Assert.Contains(fileClaims, c => (bool)c["verified"]!);
            Assert.Contains(fileClaims, c => !(bool)c["verified"]!);
        }
        finally { Directory.SetCurrentDirectory(oldDir); }
    }

    [Fact]
    public async Task CompareSpecToCode_ReportsHandlerClassClaims()
    {
        var handler = new CompareSpecToCodeHandler();
        var oldDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_tempDir);
        try
        {
            var result = await handler.ExecuteAsync(
                MakeCommand("COMPARE_SPEC_TO_CODE", new() { ["id"] = "003" }),
                MakeContext());

            Assert.Equal(CommandStatus.Success, result.Status);
            var dict = (Dictionary<string, object?>)result.Result!;
            var claims = (List<Dictionary<string, object?>>)dict["claims"]!;
            var handlerClaims = claims.Where(c => c["type"]?.ToString() == "handler_class").ToList();
            Assert.True(handlerClaims.Count >= 1, "Should find handler class claims");
            Assert.Contains(handlerClaims, c => c["claim"]?.ToString() == "RememberHandler");
        }
        finally { Directory.SetCurrentDirectory(oldDir); }
    }

    [Fact]
    public async Task CompareSpecToCode_ReportsCommandNameClaims()
    {
        var handler = new CompareSpecToCodeHandler();
        var oldDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_tempDir);
        try
        {
            var result = await handler.ExecuteAsync(
                MakeCommand("COMPARE_SPEC_TO_CODE", new() { ["id"] = "003" }),
                MakeContext());

            Assert.Equal(CommandStatus.Success, result.Status);
            var dict = (Dictionary<string, object?>)result.Result!;
            var claims = (List<Dictionary<string, object?>>)dict["claims"]!;
            var cmdClaims = claims.Where(c => c["type"]?.ToString() == "command_name").ToList();
            Assert.True(cmdClaims.Count >= 1, "Should find command name claims");

            // CLAIM_TASK is in KnownCommands — should be verified
            var claimTask = cmdClaims.FirstOrDefault(c => c["claim"]?.ToString() == "CLAIM_TASK");
            Assert.NotNull(claimTask);
            Assert.True((bool)claimTask["verified"]!);

            // NONEXISTENT_COMMAND should not be verified
            var nonexistent = cmdClaims.FirstOrDefault(c => c["claim"]?.ToString() == "NONEXISTENT_COMMAND");
            Assert.NotNull(nonexistent);
            Assert.False((bool)nonexistent["verified"]!);
        }
        finally { Directory.SetCurrentDirectory(oldDir); }
    }

    [Fact]
    public async Task CompareSpecToCode_ReportsAccuracy()
    {
        var handler = new CompareSpecToCodeHandler();
        var oldDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_tempDir);
        try
        {
            var result = await handler.ExecuteAsync(
                MakeCommand("COMPARE_SPEC_TO_CODE", new() { ["id"] = "003" }),
                MakeContext());

            Assert.Equal(CommandStatus.Success, result.Status);
            var dict = (Dictionary<string, object?>)result.Result!;
            var accuracy = (double)dict["accuracy"]!;
            Assert.True(accuracy > 0 && accuracy < 100,
                $"Expected partial accuracy (some valid, some broken), got {accuracy}%");
        }
        finally { Directory.SetCurrentDirectory(oldDir); }
    }

    [Fact]
    public async Task CompareSpecToCode_ExtractsDeclaredStatus()
    {
        var handler = new CompareSpecToCodeHandler();
        var oldDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_tempDir);
        try
        {
            var result = await handler.ExecuteAsync(
                MakeCommand("COMPARE_SPEC_TO_CODE", new() { ["id"] = "003" }),
                MakeContext());

            Assert.Equal(CommandStatus.Success, result.Status);
            var dict = (Dictionary<string, object?>)result.Result!;
            Assert.Equal("Implemented", dict["declaredStatus"]);
        }
        finally { Directory.SetCurrentDirectory(oldDir); }
    }

    [Fact]
    public async Task CompareSpecToCode_ReturnsError_WhenMissingId()
    {
        var handler = new CompareSpecToCodeHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("COMPARE_SPEC_TO_CODE", new()),
            MakeContext());

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
    }

    // ==================== DETECT_ORPHANED_SECTIONS ====================

    [Fact]
    public async Task DetectOrphaned_FindsOrphanedReferences()
    {
        var handler = new DetectOrphanedSectionsHandler();
        var oldDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_tempDir);
        try
        {
            var result = await handler.ExecuteAsync(
                MakeCommand("DETECT_ORPHANED_SECTIONS", new()),
                MakeContext());

            Assert.Equal(CommandStatus.Success, result.Status);
            var dict = (Dictionary<string, object?>)result.Result!;
            Assert.Equal("ORPHANS_DETECTED", dict["status"]);

            var orphanedSections = (List<Dictionary<string, object?>>)dict["sectionsWithOrphans"]!;
            Assert.True(orphanedSections.Count > 0, "Should detect orphaned sections");

            // Section 003 has Missing.cs which doesn't exist
            Assert.Contains(orphanedSections,
                s => s["sectionId"]?.ToString() == "003-agent-system");
        }
        finally { Directory.SetCurrentDirectory(oldDir); }
    }

    [Fact]
    public async Task DetectOrphaned_ReportsCleanSections()
    {
        var handler = new DetectOrphanedSectionsHandler();
        var oldDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_tempDir);
        try
        {
            var result = await handler.ExecuteAsync(
                MakeCommand("DETECT_ORPHANED_SECTIONS", new()),
                MakeContext());

            Assert.Equal(CommandStatus.Success, result.Status);
            var dict = (Dictionary<string, object?>)result.Result!;
            var orphanedSections = (List<Dictionary<string, object?>>)dict["sectionsWithOrphans"]!;

            // Section 099 is clean — should NOT appear in orphaned list
            Assert.DoesNotContain(orphanedSections,
                s => s["sectionId"]?.ToString() == "099-clean-section");
        }
        finally { Directory.SetCurrentDirectory(oldDir); }
    }

    [Fact]
    public async Task DetectOrphaned_SkipsSectionsWithNoReferences()
    {
        var handler = new DetectOrphanedSectionsHandler();
        var oldDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_tempDir);
        try
        {
            var result = await handler.ExecuteAsync(
                MakeCommand("DETECT_ORPHANED_SECTIONS", new()),
                MakeContext());

            Assert.Equal(CommandStatus.Success, result.Status);
            var dict = (Dictionary<string, object?>)result.Result!;
            var orphanedSections = (List<Dictionary<string, object?>>)dict["sectionsWithOrphans"]!;

            // Section 014 has no file refs — shouldn't be in orphaned list
            Assert.DoesNotContain(orphanedSections,
                s => s["sectionId"]?.ToString() == "014-database-schema");
        }
        finally { Directory.SetCurrentDirectory(oldDir); }
    }

    [Fact]
    public async Task DetectOrphaned_CanFilterBySection()
    {
        var handler = new DetectOrphanedSectionsHandler();
        var oldDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_tempDir);
        try
        {
            var result = await handler.ExecuteAsync(
                MakeCommand("DETECT_ORPHANED_SECTIONS", new() { ["id"] = "099" }),
                MakeContext());

            Assert.Equal(CommandStatus.Success, result.Status);
            var dict = (Dictionary<string, object?>)result.Result!;
            // Only 099 is scanned, it's clean
            Assert.Equal("CLEAN", dict["status"]);
            Assert.Equal(0, dict["totalOrphaned"]);
        }
        finally { Directory.SetCurrentDirectory(oldDir); }
    }

    [Fact]
    public async Task DetectOrphaned_ReturnsError_WhenNoSpecs()
    {
        var emptyDir = Path.Combine(Path.GetTempPath(), $"empty-{Guid.NewGuid():N}");
        Directory.CreateDirectory(emptyDir);
        try
        {
            var services = new ServiceCollection();
            services.AddSingleton<ISpecManager>(new SpecManager(Path.Combine(emptyDir, "specs")));
            var sp = services.BuildServiceProvider();

            var handler = new DetectOrphanedSectionsHandler();
            var result = await handler.ExecuteAsync(
                MakeCommand("DETECT_ORPHANED_SECTIONS", new()),
                MakeContext(sp));

            Assert.Equal(CommandStatus.Error, result.Status);
            Assert.Equal(CommandErrorCode.NotFound, result.ErrorCode);
        }
        finally { Directory.Delete(emptyDir, true); }
    }

    [Fact]
    public async Task DetectOrphaned_ReturnsNotFound_ForUnmatchedFilter()
    {
        var handler = new DetectOrphanedSectionsHandler();
        var oldDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_tempDir);
        try
        {
            var result = await handler.ExecuteAsync(
                MakeCommand("DETECT_ORPHANED_SECTIONS", new() { ["id"] = "999-nonexistent" }),
                MakeContext());

            Assert.Equal(CommandStatus.Error, result.Status);
            Assert.Equal(CommandErrorCode.NotFound, result.ErrorCode);
        }
        finally { Directory.SetCurrentDirectory(oldDir); }
    }

    [Fact]
    public async Task DetectOrphaned_AcceptsValueArg()
    {
        var handler = new DetectOrphanedSectionsHandler();
        var oldDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_tempDir);
        try
        {
            var result = await handler.ExecuteAsync(
                MakeCommand("DETECT_ORPHANED_SECTIONS", new() { ["value"] = "099" }),
                MakeContext());

            Assert.Equal(CommandStatus.Success, result.Status);
            var dict = (Dictionary<string, object?>)result.Result!;
            Assert.Equal("CLEAN", dict["status"]);
        }
        finally { Directory.SetCurrentDirectory(oldDir); }
    }

    [Fact]
    public async Task DetectOrphaned_ReportsTotalReferencesChecked()
    {
        var handler = new DetectOrphanedSectionsHandler();
        var oldDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_tempDir);
        try
        {
            var result = await handler.ExecuteAsync(
                MakeCommand("DETECT_ORPHANED_SECTIONS", new()),
                MakeContext());

            Assert.Equal(CommandStatus.Success, result.Status);
            var dict = (Dictionary<string, object?>)result.Result!;
            var total = (int)dict["totalReferencesChecked"]!;
            Assert.True(total > 0, "Should have checked at least one reference");
        }
        finally { Directory.SetCurrentDirectory(oldDir); }
    }

    // ==================== Handler Metadata ====================

    [Fact]
    public void VerifySpecSection_IsRetrySafe()
    {
        var handler = new VerifySpecSectionHandler();
        Assert.True(handler.IsRetrySafe);
        Assert.Equal("VERIFY_SPEC_SECTION", handler.CommandName);
    }

    [Fact]
    public void CompareSpecToCode_IsRetrySafe()
    {
        var handler = new CompareSpecToCodeHandler();
        Assert.True(handler.IsRetrySafe);
        Assert.Equal("COMPARE_SPEC_TO_CODE", handler.CommandName);
    }

    [Fact]
    public void DetectOrphanedSections_IsRetrySafe()
    {
        var handler = new DetectOrphanedSectionsHandler();
        Assert.True(handler.IsRetrySafe);
        Assert.Equal("DETECT_ORPHANED_SECTIONS", handler.CommandName);
    }
}
