using AgentAcademy.Server.Controllers;
using AgentAcademy.Shared.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentAcademy.Server.Tests;

public sealed class SearchControllerTests : IDisposable
{
    private readonly TestServiceGraph _svc;
    private readonly SearchController _controller;

    public SearchControllerTests()
    {
        _svc = new TestServiceGraph();
        _controller = new SearchController(_svc.SearchService, _svc.RoomService);
    }

    public void Dispose() => _svc.Dispose();

    [Fact]
    public async Task Search_EmptyQuery_ReturnsBadRequest()
    {
        var result = await _controller.Search(q: null);
        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        ProblemDetailsAssert.HasCode(bad.Value, "empty_query");
    }

    [Fact]
    public async Task Search_WhitespaceQuery_ReturnsBadRequest()
    {
        var result = await _controller.Search(q: "   ");
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Search_InvalidScope_ReturnsBadRequest()
    {
        var result = await _controller.Search(q: "test", scope: "invalid");
        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        ProblemDetailsAssert.HasCode(bad.Value, "invalid_scope");
    }

    [Fact]
    public async Task Search_MessageLimitTooLow_ReturnsBadRequest()
    {
        var result = await _controller.Search(q: "test", messageLimit: 0);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Search_MessageLimitTooHigh_ReturnsBadRequest()
    {
        var result = await _controller.Search(q: "test", messageLimit: 101);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Search_TaskLimitTooLow_ReturnsBadRequest()
    {
        var result = await _controller.Search(q: "test", taskLimit: 0);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Search_TaskLimitTooHigh_ReturnsBadRequest()
    {
        var result = await _controller.Search(q: "test", taskLimit: 101);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Theory]
    [InlineData("all")]
    [InlineData("messages")]
    [InlineData("tasks")]
    public async Task Search_ValidScopesReturnOk(string scope)
    {
        var result = await _controller.Search(q: "hello", scope: scope);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var searchResults = Assert.IsType<SearchResults>(ok.Value);
        Assert.Equal("hello", searchResults.Query);
    }

    [Fact]
    public async Task Search_ReturnsEmptyResultsForNoMatches()
    {
        var result = await _controller.Search(q: "nonexistent-xyz-12345");
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var searchResults = Assert.IsType<SearchResults>(ok.Value);
        Assert.Empty(searchResults.Messages);
        Assert.Empty(searchResults.Tasks);
        Assert.Equal(0, searchResults.TotalCount);
    }
}
