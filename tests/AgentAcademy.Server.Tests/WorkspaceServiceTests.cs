using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentAcademy.Server.Tests;

public class WorkspaceServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AgentAcademyDbContext _db;
    private readonly WorkspaceService _sut;

    public WorkspaceServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AgentAcademyDbContext>()
            .UseSqlite(_connection).Options;
        _db = new AgentAcademyDbContext(options);
        _db.Database.EnsureCreated();
        _sut = new WorkspaceService(_db, NullLogger<WorkspaceService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    // ── Helpers ──────────────────────────────────────────────────

    private WorkspaceEntity MakeWorkspace(
        string path, string? name = null, bool active = false,
        DateTime? lastAccessed = null)
    {
        return new WorkspaceEntity
        {
            Path = path,
            ProjectName = name ?? Path.GetFileName(path),
            IsActive = active,
            LastAccessedAt = lastAccessed ?? DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        };
    }

    private ProjectScanResult MakeScan(
        string path, string? projectName = null,
        string? repoUrl = null, string? defaultBranch = null,
        string? hostProvider = null)
    {
        return new ProjectScanResult(
            Path: path,
            ProjectName: projectName ?? Path.GetFileName(path),
            TechStack: ["C#"],
            HasSpecs: true,
            HasReadme: true,
            IsGitRepo: true,
            GitBranch: "develop",
            DetectedFiles: [],
            RepositoryUrl: repoUrl,
            DefaultBranch: defaultBranch,
            HostProvider: hostProvider);
    }

    // ── GetActiveWorkspaceAsync ─────────────────────────────────

    [Fact]
    public async Task GetActive_ReturnsNull_WhenNoWorkspaces()
    {
        var result = await _sut.GetActiveWorkspaceAsync();
        Assert.Null(result);
    }

    [Fact]
    public async Task GetActive_ReturnsNull_WhenNoneActive()
    {
        _db.Workspaces.Add(MakeWorkspace("/project/a", active: false));
        await _db.SaveChangesAsync();

        var result = await _sut.GetActiveWorkspaceAsync();
        Assert.Null(result);
    }

    [Fact]
    public async Task GetActive_ReturnsActiveWorkspace()
    {
        _db.Workspaces.Add(MakeWorkspace("/project/a", "ProjectA", active: true));
        _db.Workspaces.Add(MakeWorkspace("/project/b", "ProjectB", active: false));
        await _db.SaveChangesAsync();

        var result = await _sut.GetActiveWorkspaceAsync();

        Assert.NotNull(result);
        Assert.Equal("/project/a", result.Path);
        Assert.Equal("ProjectA", result.ProjectName);
    }

    // ── ListWorkspacesAsync ─────────────────────────────────────

    [Fact]
    public async Task List_ReturnsEmpty_WhenNoWorkspaces()
    {
        var result = await _sut.ListWorkspacesAsync();
        Assert.Empty(result);
    }

    [Fact]
    public async Task List_ReturnsAllWorkspaces_OrderedByLastAccessed()
    {
        var older = DateTime.UtcNow.AddHours(-2);
        var newer = DateTime.UtcNow.AddHours(-1);

        _db.Workspaces.Add(MakeWorkspace("/project/old", lastAccessed: older));
        _db.Workspaces.Add(MakeWorkspace("/project/new", lastAccessed: newer));
        await _db.SaveChangesAsync();

        var result = await _sut.ListWorkspacesAsync();

        Assert.Equal(2, result.Count);
        Assert.Equal("/project/new", result[0].Path);
        Assert.Equal("/project/old", result[1].Path);
    }

    // ── ActivateWorkspaceAsync ──────────────────────────────────

    [Fact]
    public async Task Activate_CreatesNewWorkspace_WhenNotExists()
    {
        var scan = MakeScan("/project/new", "NewProject",
            repoUrl: "https://github.com/org/repo.git",
            defaultBranch: "main",
            hostProvider: "github");

        var result = await _sut.ActivateWorkspaceAsync(scan);

        Assert.Equal("/project/new", result.Path);
        Assert.Equal("NewProject", result.ProjectName);
        Assert.Equal("https://github.com/org/repo.git", result.RepositoryUrl);
        Assert.Equal("main", result.DefaultBranch);
        Assert.Equal("github", result.HostProvider);

        var entity = await _db.Workspaces.FindAsync("/project/new");
        Assert.NotNull(entity);
        Assert.True(entity.IsActive);
    }

    [Fact]
    public async Task Activate_UpdatesExistingWorkspace_WhenAlreadyExists()
    {
        _db.Workspaces.Add(MakeWorkspace("/project/existing", "OldName", active: false));
        await _db.SaveChangesAsync();

        var scan = MakeScan("/project/existing", "NewName",
            repoUrl: "https://github.com/org/repo.git",
            defaultBranch: "develop");

        var result = await _sut.ActivateWorkspaceAsync(scan);

        Assert.Equal("NewName", result.ProjectName);
        Assert.Equal("https://github.com/org/repo.git", result.RepositoryUrl);
        Assert.Equal("develop", result.DefaultBranch);

        var entity = await _db.Workspaces.FindAsync("/project/existing");
        Assert.True(entity!.IsActive);
    }

    [Fact]
    public async Task Activate_DeactivatesPreviouslyActiveWorkspace()
    {
        _db.Workspaces.Add(MakeWorkspace("/project/old", active: true));
        await _db.SaveChangesAsync();

        await _sut.ActivateWorkspaceAsync(MakeScan("/project/new"));

        // ExecuteUpdateAsync bypasses change tracker — reload from DB
        _db.ChangeTracker.Clear();
        var old = await _db.Workspaces.FindAsync("/project/old");
        Assert.False(old!.IsActive);

        var @new = await _db.Workspaces.FindAsync("/project/new");
        Assert.True(@new!.IsActive);
    }

    [Fact]
    public async Task Activate_TrimsHistoryToTwenty()
    {
        // Seed 21 workspaces (all inactive)
        for (var i = 0; i < 21; i++)
        {
            _db.Workspaces.Add(MakeWorkspace(
                $"/project/{i:D3}",
                lastAccessed: DateTime.UtcNow.AddMinutes(i)));
        }
        await _db.SaveChangesAsync();

        // Activate a new 22nd workspace — should trigger trim
        await _sut.ActivateWorkspaceAsync(MakeScan("/project/newest"));

        _db.ChangeTracker.Clear();
        var count = await _db.Workspaces.CountAsync();
        // 20 remaining after trim: 1 active (newest) + 19 most recent inactive
        Assert.Equal(20, count);

        // The newly activated one must survive
        var newest = await _db.Workspaces.FindAsync("/project/newest");
        Assert.NotNull(newest);
        Assert.True(newest.IsActive);

        // The oldest workspace (lowest LastAccessedAt) should be gone
        var oldest = await _db.Workspaces.FindAsync("/project/000");
        Assert.Null(oldest);
    }

    [Fact]
    public async Task Activate_PreservesNullRepoFields_OnExistingWorkspace()
    {
        _db.Workspaces.Add(new WorkspaceEntity
        {
            Path = "/project/with-repo",
            ProjectName = "Repo",
            RepositoryUrl = "https://github.com/org/repo.git",
            DefaultBranch = "main",
            HostProvider = "github",
            CreatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        // Scan with null optional fields should NOT overwrite existing values
        var scan = MakeScan("/project/with-repo", "Repo");

        await _sut.ActivateWorkspaceAsync(scan);

        var entity = await _db.Workspaces.FindAsync("/project/with-repo");
        Assert.Equal("https://github.com/org/repo.git", entity!.RepositoryUrl);
        Assert.Equal("main", entity.DefaultBranch);
        Assert.Equal("github", entity.HostProvider);
    }

    // ── Mapping ─────────────────────────────────────────────────

    [Fact]
    public async Task ToMeta_MapsAllFields()
    {
        var now = DateTime.UtcNow;
        _db.Workspaces.Add(new WorkspaceEntity
        {
            Path = "/project/full",
            ProjectName = "Full",
            IsActive = true,
            LastAccessedAt = now,
            CreatedAt = now,
            RepositoryUrl = "https://github.com/org/repo.git",
            DefaultBranch = "develop",
            HostProvider = "github"
        });
        await _db.SaveChangesAsync();

        var result = await _sut.GetActiveWorkspaceAsync();

        Assert.NotNull(result);
        Assert.Equal("/project/full", result.Path);
        Assert.Equal("Full", result.ProjectName);
        Assert.Equal(now, result.LastAccessedAt);
        Assert.Equal("https://github.com/org/repo.git", result.RepositoryUrl);
        Assert.Equal("develop", result.DefaultBranch);
        Assert.Equal("github", result.HostProvider);
    }
}
