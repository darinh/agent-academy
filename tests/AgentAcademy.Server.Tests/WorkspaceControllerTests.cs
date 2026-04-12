using AgentAcademy.Server.Controllers;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Shared.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentAcademy.Server.Tests;

public sealed class WorkspaceControllerTests : IDisposable
{
    private readonly TestServiceGraph _svc;
    private readonly WorkspaceController _controller;

    public WorkspaceControllerTests()
    {
        _svc = new TestServiceGraph();
        _controller = new WorkspaceController(
            _svc.ProjectScanner, _svc.RoomService,
            _svc.TaskOrchestrationService, _svc.TaskQueryService,
            _svc.Orchestrator, _svc.Executor,
            _svc.SessionService, _svc.Db,
            NullLogger<WorkspaceController>.Instance);
    }

    public void Dispose() => _svc.Dispose();

    // ── GetActiveWorkspace ───────────────────────────────────────

    [Fact]
    public async Task GetActiveWorkspace_NoWorkspace_ReturnsNullActive()
    {
        var result = await _controller.GetActiveWorkspace();
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains("\"active\":null", json);
    }

    [Fact]
    public async Task GetActiveWorkspace_WithActive_ReturnsIt()
    {
        _svc.Db.Workspaces.Add(new WorkspaceEntity
        {
            Path = "/home/test/project",
            ProjectName = "Test Project",
            IsActive = true,
            LastAccessedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        });
        await _svc.Db.SaveChangesAsync();

        var result = await _controller.GetActiveWorkspace();
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains("Test Project", json);
    }

    // ── ListWorkspaces ───────────────────────────────────────────

    [Fact]
    public async Task ListWorkspaces_EmptyDb_ReturnsEmptyList()
    {
        var result = await _controller.ListWorkspaces();
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var list = Assert.IsType<List<WorkspaceMeta>>(ok.Value);
        Assert.Empty(list);
    }

    [Fact]
    public async Task ListWorkspaces_ReturnsOrderedByLastAccessed()
    {
        _svc.Db.Workspaces.AddRange(
            new WorkspaceEntity
            {
                Path = "/home/test/old",
                ProjectName = "Old",
                IsActive = false,
                LastAccessedAt = DateTime.UtcNow.AddDays(-2),
                CreatedAt = DateTime.UtcNow.AddDays(-2)
            },
            new WorkspaceEntity
            {
                Path = "/home/test/new",
                ProjectName = "New",
                IsActive = true,
                LastAccessedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow
            });
        await _svc.Db.SaveChangesAsync();

        var result = await _controller.ListWorkspaces();
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var list = Assert.IsType<List<WorkspaceMeta>>(ok.Value);
        Assert.Equal(2, list.Count);
        Assert.Equal("New", list[0].ProjectName);
    }

    // ── SetActiveWorkspace ───────────────────────────────────────

    [Fact]
    public async Task SetActiveWorkspace_EmptyPath_ReturnsBadRequest()
    {
        var result = await _controller.SetActiveWorkspace(new SwitchWorkspaceRequest(""));
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task SetActiveWorkspace_OutsideHome_ReturnsBadRequest()
    {
        var result = await _controller.SetActiveWorkspace(new SwitchWorkspaceRequest("/etc"));
        Assert.IsType<BadRequestObjectResult>(result);
    }

    // ── ScanProject ──────────────────────────────────────────────

    [Fact]
    public void ScanProject_EmptyPath_ReturnsBadRequest()
    {
        var result = _controller.ScanProject(new ScanRequest(""));
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public void ScanProject_OutsideHome_ReturnsBadRequest()
    {
        var result = _controller.ScanProject(new ScanRequest("/etc"));
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public void ScanProject_NonexistentPath_ReturnsNotFound()
    {
        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var result = _controller.ScanProject(new ScanRequest(
            Path.Combine(homeDir, "nonexistent-project-xyz-99999")));
        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public void ScanProject_ValidPath_ReturnsScanResult()
    {
        // Scan the actual repo root — it's a real directory
        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        // Use the home directory itself as a valid scannable path
        var result = _controller.ScanProject(new ScanRequest(homeDir));
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var scan = Assert.IsType<ProjectScanResult>(ok.Value);
        Assert.Equal(homeDir, scan.Path);
    }

    // ── OnboardProject ───────────────────────────────────────────

    [Fact]
    public async Task OnboardProject_EmptyPath_ReturnsBadRequest()
    {
        var result = await _controller.OnboardProject(new ScanRequest(""));
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task OnboardProject_OutsideHome_ReturnsBadRequest()
    {
        var result = await _controller.OnboardProject(new ScanRequest("/etc"));
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task OnboardProject_NonexistentPath_ReturnsNotFound()
    {
        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var result = await _controller.OnboardProject(new ScanRequest(
            Path.Combine(homeDir, "nonexistent-project-xyz-99999")));
        Assert.IsType<NotFoundObjectResult>(result.Result);
    }
}
