using AgentAcademy.Server.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentAcademy.Server.Tests;

public sealed class FilesystemControllerTests
{
    private readonly FilesystemController _controller;

    public FilesystemControllerTests()
    {
        _controller = new FilesystemController(NullLogger<FilesystemController>.Instance);
    }

    [Fact]
    public void Browse_RelativePath_ReturnsBadRequest()
    {
        var result = _controller.Browse(path: "relative/path", showHidden: null);
        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("invalid_path", bad.Value!.ToString()!);
    }

    [Fact]
    public void Browse_OutsideHome_ReturnsBadRequest()
    {
        var result = _controller.Browse(path: "/etc", showHidden: null);
        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("invalid_path", bad.Value!.ToString()!);
    }

    [Fact]
    public void Browse_NonexistentDir_ReturnsNotFound()
    {
        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var result = _controller.Browse(path: Path.Combine(homeDir, "nonexistent-dir-xyz-99999"), showHidden: null);
        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public void Browse_DefaultPath_ReturnsHomeDirectory()
    {
        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var result = _controller.Browse(path: null, showHidden: null);
        var ok = Assert.IsType<OkObjectResult>(result);

        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains(homeDir, json);
    }

    [Fact]
    public void Browse_HomeDir_ReturnsDirectoriesOnly()
    {
        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var result = _controller.Browse(path: homeDir, showHidden: null);
        var ok = Assert.IsType<OkObjectResult>(result);

        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        // All entries should have isDirectory = true
        Assert.Contains("isDirectory", json);
    }

    [Fact]
    public void Browse_HiddenDirsExcludedByDefault()
    {
        // Create a controlled test directory with hidden and non-hidden subdirs
        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var testDir = Path.Combine(homeDir, $".anvil-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(testDir, ".hidden-child"));
        Directory.CreateDirectory(Path.Combine(testDir, "visible-child"));

        try
        {
            var result = _controller.Browse(path: testDir, showHidden: null);
            var ok = Assert.IsType<OkObjectResult>(result);

            var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var entries = doc.RootElement.GetProperty("entries");

            var names = entries.EnumerateArray()
                .Select(e => e.GetProperty("name").GetString()!)
                .ToList();

            Assert.DoesNotContain(".hidden-child", names);
            Assert.Contains("visible-child", names);
        }
        finally
        {
            Directory.Delete(testDir, recursive: true);
        }
    }

    [Fact]
    public void Browse_ShowHiddenTrue_IncludesHiddenDirs()
    {
        // Create a controlled test directory with hidden and non-hidden subdirs
        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var testDir = Path.Combine(homeDir, $".anvil-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(testDir, ".hidden-child"));
        Directory.CreateDirectory(Path.Combine(testDir, "visible-child"));

        try
        {
            var result = _controller.Browse(path: testDir, showHidden: "true");
            var ok = Assert.IsType<OkObjectResult>(result);

            var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var entries = doc.RootElement.GetProperty("entries");

            var names = entries.EnumerateArray()
                .Select(e => e.GetProperty("name").GetString()!)
                .ToList();

            Assert.Contains(".hidden-child", names);
            Assert.Contains("visible-child", names);
        }
        finally
        {
            Directory.Delete(testDir, recursive: true);
        }
    }

    [Fact]
    public void Browse_PathTraversal_Blocked()
    {
        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        // Try to escape via path traversal
        var result = _controller.Browse(path: homeDir + "/../../../etc", showHidden: null);
        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("invalid_path", bad.Value!.ToString()!);
    }
}
