using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentAcademy.Server.Tests;

public sealed class ArtifactEvaluatorTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AgentAcademyDbContext _db;
    private readonly ArtifactEvaluatorService _evaluator;
    private readonly string _workspaceDir;

    public ArtifactEvaluatorTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AgentAcademyDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AgentAcademyDbContext(options);
        _db.Database.EnsureCreated();

        _workspaceDir = Path.Combine(Path.GetTempPath(), "artifact-eval-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_workspaceDir);

        _evaluator = new ArtifactEvaluatorService(_db, NullLogger<ArtifactEvaluatorService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        if (Directory.Exists(_workspaceDir))
            Directory.Delete(_workspaceDir, recursive: true);
    }

    // ── Helpers ──────────────────────────────────────────────────

    private void SeedRoom(string roomId, string? workspacePath = null)
    {
        _db.Rooms.Add(new RoomEntity
        {
            Id = roomId,
            Name = roomId,
            Status = "Active",
            CurrentPhase = "Intake",
            WorkspacePath = workspacePath,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

        if (workspacePath is not null)
        {
            if (!_db.Workspaces.Any(w => w.Path == workspacePath))
            {
                _db.Workspaces.Add(new WorkspaceEntity
                {
                    Path = workspacePath,
                    ProjectName = "test-project",
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow,
                });
            }
        }

        _db.SaveChanges();
    }

    private void SeedArtifact(string roomId, string filePath, string operation = "Created",
        string agentId = "agent-1", DateTime? timestamp = null)
    {
        _db.RoomArtifacts.Add(new RoomArtifactEntity
        {
            RoomId = roomId,
            AgentId = agentId,
            FilePath = filePath,
            Operation = operation,
            Timestamp = timestamp ?? DateTime.UtcNow,
        });
        _db.SaveChanges();
    }

    private string CreateWorkspaceFile(string relativePath, string content)
    {
        var fullPath = Path.Combine(_workspaceDir, relativePath);
        var dir = Path.GetDirectoryName(fullPath);
        if (dir is not null)
            Directory.CreateDirectory(dir);
        File.WriteAllText(fullPath, content);
        return fullPath;
    }

    // ── No artifacts ─────────────────────────────────────────────

    [Fact]
    public async Task EvaluateRoom_NoArtifacts_ReturnsEmptyResult()
    {
        SeedRoom("room-1", _workspaceDir);

        var (artifacts, score) = await _evaluator.EvaluateRoomArtifactsAsync("room-1");

        Assert.Empty(artifacts);
        Assert.Equal(0.0, score);
    }

    [Fact]
    public async Task EvaluateRoom_NonexistentRoom_ReturnsEmptyResult()
    {
        var (artifacts, score) = await _evaluator.EvaluateRoomArtifactsAsync("nonexistent");

        Assert.Empty(artifacts);
        Assert.Equal(0.0, score);
    }

    // ── Valid file ───────────────────────────────────────────────

    [Fact]
    public async Task EvaluateRoom_ExistingValidFile_ScoresMaximum()
    {
        SeedRoom("room-1", _workspaceDir);
        CreateWorkspaceFile("src/app.cs", "namespace App { public class Program { } }");
        SeedArtifact("room-1", "src/app.cs");

        var (artifacts, score) = await _evaluator.EvaluateRoomArtifactsAsync("room-1");

        Assert.Single(artifacts);
        var result = artifacts[0];
        Assert.Equal("src/app.cs", result.FilePath);
        Assert.True(result.Exists);
        Assert.True(result.NonEmpty);
        Assert.True(result.SyntaxValid);
        Assert.True(result.Complete);
        Assert.Equal(100.0, result.Score);
        Assert.Empty(result.Issues);
    }

    // ── Missing file ─────────────────────────────────────────────

    [Fact]
    public async Task EvaluateRoom_MissingFile_ScoresZero()
    {
        SeedRoom("room-1", _workspaceDir);
        SeedArtifact("room-1", "src/missing.cs");

        var (artifacts, _) = await _evaluator.EvaluateRoomArtifactsAsync("room-1");

        Assert.Single(artifacts);
        var result = artifacts[0];
        Assert.False(result.Exists);
        Assert.False(result.NonEmpty);
        Assert.False(result.SyntaxValid);
        Assert.False(result.Complete);
        Assert.Equal(0.0, result.Score);
        Assert.Contains(result.Issues, i => i.Contains("does not exist"));
    }

    // ── Empty file ───────────────────────────────────────────────

    [Fact]
    public async Task EvaluateRoom_EmptyFile_PartialScore()
    {
        SeedRoom("room-1", _workspaceDir);
        CreateWorkspaceFile("src/empty.cs", "");
        SeedArtifact("room-1", "src/empty.cs");

        var (artifacts, _) = await _evaluator.EvaluateRoomArtifactsAsync("room-1");

        Assert.Single(artifacts);
        var result = artifacts[0];
        Assert.True(result.Exists);
        Assert.False(result.NonEmpty);
        Assert.False(result.SyntaxValid);
        Assert.False(result.Complete);
        Assert.Equal(40.0, result.Score); // Only Exists points
        Assert.Contains(result.Issues, i => i.Contains("empty"));
    }

    // ── Invalid JSON ─────────────────────────────────────────────

    [Fact]
    public async Task EvaluateRoom_InvalidJson_SyntaxValidFalse()
    {
        SeedRoom("room-1", _workspaceDir);
        CreateWorkspaceFile("config.json", "{ invalid json }");
        SeedArtifact("room-1", "config.json");

        var (artifacts, _) = await _evaluator.EvaluateRoomArtifactsAsync("room-1");

        Assert.Single(artifacts);
        var result = artifacts[0];
        Assert.True(result.Exists);
        Assert.True(result.NonEmpty);
        Assert.False(result.SyntaxValid);
        Assert.Contains(result.Issues, i => i.Contains("Invalid JSON"));
    }

    [Fact]
    public async Task EvaluateRoom_ValidJson_SyntaxValidTrue()
    {
        SeedRoom("room-1", _workspaceDir);
        CreateWorkspaceFile("config.json", """{ "key": "value" }""");
        SeedArtifact("room-1", "config.json");

        var (artifacts, _) = await _evaluator.EvaluateRoomArtifactsAsync("room-1");

        Assert.Single(artifacts);
        Assert.True(artifacts[0].SyntaxValid);
    }

    // ── Invalid XML ──────────────────────────────────────────────

    [Fact]
    public async Task EvaluateRoom_InvalidXml_SyntaxValidFalse()
    {
        SeedRoom("room-1", _workspaceDir);
        CreateWorkspaceFile("data.xml", "<root><unclosed>");
        SeedArtifact("room-1", "data.xml");

        var (artifacts, _) = await _evaluator.EvaluateRoomArtifactsAsync("room-1");

        Assert.Single(artifacts);
        Assert.False(artifacts[0].SyntaxValid);
        Assert.Contains(artifacts[0].Issues, i => i.Contains("Invalid XML"));
    }

    [Fact]
    public async Task EvaluateRoom_ValidCsproj_SyntaxValidTrue()
    {
        SeedRoom("room-1", _workspaceDir);
        CreateWorkspaceFile("test.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        SeedArtifact("room-1", "test.csproj");

        var (artifacts, _) = await _evaluator.EvaluateRoomArtifactsAsync("room-1");

        Assert.Single(artifacts);
        Assert.True(artifacts[0].SyntaxValid);
    }

    // ── Completeness markers ─────────────────────────────────────

    [Fact]
    public async Task EvaluateRoom_TodoMarker_CompleteFalse()
    {
        SeedRoom("room-1", _workspaceDir);
        CreateWorkspaceFile("src/service.cs", "public class Svc { // TODO: implement this }");
        SeedArtifact("room-1", "src/service.cs");

        var (artifacts, _) = await _evaluator.EvaluateRoomArtifactsAsync("room-1");

        Assert.Single(artifacts);
        Assert.False(artifacts[0].Complete);
        Assert.Contains(artifacts[0].Issues, i => i.Contains("TODO"));
    }

    [Fact]
    public async Task EvaluateRoom_FixmeMarker_CompleteFalse()
    {
        SeedRoom("room-1", _workspaceDir);
        CreateWorkspaceFile("src/handler.cs", "public class H { // FIXME: broken logic }");
        SeedArtifact("room-1", "src/handler.cs");

        var (artifacts, _) = await _evaluator.EvaluateRoomArtifactsAsync("room-1");

        Assert.Single(artifacts);
        Assert.False(artifacts[0].Complete);
    }

    [Fact]
    public async Task EvaluateRoom_HackMarker_CompleteFalse()
    {
        SeedRoom("room-1", _workspaceDir);
        CreateWorkspaceFile("src/util.cs", "public class U { // HACK: temporary workaround }");
        SeedArtifact("room-1", "src/util.cs");

        var (artifacts, _) = await _evaluator.EvaluateRoomArtifactsAsync("room-1");

        Assert.Single(artifacts);
        Assert.False(artifacts[0].Complete);
    }

    // ── Deleted file exclusion ───────────────────────────────────

    [Fact]
    public async Task EvaluateRoom_DeletedFile_IsExcluded()
    {
        SeedRoom("room-1", _workspaceDir);
        SeedArtifact("room-1", "src/old.cs", "Created", timestamp: DateTime.UtcNow.AddMinutes(-2));
        SeedArtifact("room-1", "src/old.cs", "Deleted", timestamp: DateTime.UtcNow);

        var (artifacts, _) = await _evaluator.EvaluateRoomArtifactsAsync("room-1");

        Assert.Empty(artifacts);
    }

    // ── Deduplication ────────────────────────────────────────────

    [Fact]
    public async Task EvaluateRoom_MultipleOperationsSameFile_UsesLatest()
    {
        SeedRoom("room-1", _workspaceDir);
        CreateWorkspaceFile("src/app.cs", "namespace App { }");

        SeedArtifact("room-1", "src/app.cs", "Created", timestamp: DateTime.UtcNow.AddMinutes(-3));
        SeedArtifact("room-1", "src/app.cs", "Updated", timestamp: DateTime.UtcNow.AddMinutes(-1));
        SeedArtifact("room-1", "src/app.cs", "Committed", timestamp: DateTime.UtcNow);

        var (artifacts, _) = await _evaluator.EvaluateRoomArtifactsAsync("room-1");

        Assert.Single(artifacts);
    }

    // ── Aggregate score ──────────────────────────────────────────

    [Fact]
    public async Task EvaluateRoom_MultipleFiles_CalculatesAverageScore()
    {
        SeedRoom("room-1", _workspaceDir);
        CreateWorkspaceFile("src/good.cs", "namespace Good { }");
        SeedArtifact("room-1", "src/good.cs");
        SeedArtifact("room-1", "src/missing.cs");

        var (artifacts, score) = await _evaluator.EvaluateRoomArtifactsAsync("room-1");

        Assert.Equal(2, artifacts.Count);
        Assert.Equal(50.0, score);
    }

    // ── Path traversal protection ────────────────────────────────

    [Fact]
    public async Task EvaluateRoom_PathTraversal_ScoresZeroWithIssue()
    {
        SeedRoom("room-1", _workspaceDir);
        SeedArtifact("room-1", "../../../etc/passwd");

        var (artifacts, _) = await _evaluator.EvaluateRoomArtifactsAsync("room-1");

        Assert.Single(artifacts);
        var result = artifacts[0];
        Assert.Equal(0.0, result.Score);
        Assert.False(result.Exists);
        Assert.Contains(result.Issues, i => i.Contains("traversal"));
    }

    [Fact]
    public async Task EvaluateRoom_AbsolutePath_ScoresZeroWithIssue()
    {
        SeedRoom("room-1", _workspaceDir);
        SeedArtifact("room-1", "/etc/passwd");

        var (artifacts, _) = await _evaluator.EvaluateRoomArtifactsAsync("room-1");

        Assert.Single(artifacts);
        Assert.Equal(0.0, artifacts[0].Score);
        Assert.Contains(artifacts[0].Issues, i =>
            i.Contains("traversal") || i.Contains("Invalid"));
    }

    // ── No workspace configured ──────────────────────────────────

    [Fact]
    public async Task EvaluateRoom_NoWorkspace_AllFilesScoreZero()
    {
        SeedRoom("room-no-ws");
        SeedArtifact("room-no-ws", "src/app.cs");

        var (artifacts, _) = await _evaluator.EvaluateRoomArtifactsAsync("room-no-ws");

        Assert.Single(artifacts);
        Assert.Equal(0.0, artifacts[0].Score);
        Assert.Contains(artifacts[0].Issues, i => i.Contains("No workspace"));
    }

    // ── Non-parseable file types pass syntax ─────────────────────

    [Fact]
    public async Task EvaluateRoom_CSharpFile_SyntaxPassesByDefault()
    {
        SeedRoom("room-1", _workspaceDir);
        CreateWorkspaceFile("src/broken.cs", "this is not valid C# syntax {{{");
        SeedArtifact("room-1", "src/broken.cs");

        var (artifacts, _) = await _evaluator.EvaluateRoomArtifactsAsync("room-1");

        Assert.Single(artifacts);
        Assert.True(artifacts[0].SyntaxValid);
    }

    // ── Score components ─────────────────────────────────────────

    [Fact]
    public async Task EvaluateRoom_FileWithTodoOnly_ScoreIs85()
    {
        SeedRoom("room-1", _workspaceDir);
        CreateWorkspaceFile("src/app.cs", "namespace App { } // TODO: refine");
        SeedArtifact("room-1", "src/app.cs");

        var (artifacts, _) = await _evaluator.EvaluateRoomArtifactsAsync("room-1");

        Assert.Single(artifacts);
        var result = artifacts[0];
        Assert.True(result.Exists);
        Assert.True(result.NonEmpty);
        Assert.True(result.SyntaxValid);
        Assert.False(result.Complete);
        Assert.Equal(85.0, result.Score);
    }

    [Fact]
    public async Task EvaluateRoom_InvalidJsonWithTodo_ScoreIs60()
    {
        SeedRoom("room-1", _workspaceDir);
        CreateWorkspaceFile("config.json", "{ invalid TODO json }");
        SeedArtifact("room-1", "config.json");

        var (artifacts, _) = await _evaluator.EvaluateRoomArtifactsAsync("room-1");

        Assert.Single(artifacts);
        var result = artifacts[0];
        Assert.True(result.Exists);
        Assert.True(result.NonEmpty);
        Assert.False(result.SyntaxValid);
        Assert.False(result.Complete);
        Assert.Equal(60.0, result.Score);
    }
}
