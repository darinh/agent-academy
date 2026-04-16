using BenchmarkDotNet.Attributes;
using AgentAcademy.Server.Commands;
using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Benchmarks;

/// <summary>
/// Benchmarks for <see cref="CommandAuthorizer.Authorize"/> — the permission checking
/// step in the command pipeline (exact match, prefix wildcard, full wildcard).
/// </summary>
[MemoryDiagnoser]
[CategoriesColumn]
public class CommandAuthorizerBenchmarks
{
    private readonly CommandAuthorizer _authorizer = new();

    private CommandEnvelope _readFileCommand = default!;
    private AgentDefinition _wildcardAgent = default!;
    private AgentDefinition _explicitAllowAgent = default!;
    private AgentDefinition _prefixWildcardAgent = default!;
    private AgentDefinition _deniedAgent = default!;
    private AgentDefinition _noPermissionsAgent = default!;

    [GlobalSetup]
    public void Setup()
    {
        _readFileCommand = new CommandEnvelope(
            "READ_FILE",
            new Dictionary<string, object?> { ["path"] = "src/Program.cs" },
            CommandStatus.Success,
            null, null,
            Guid.NewGuid().ToString(),
            DateTime.UtcNow,
            "agent-alpha");

        _wildcardAgent = new AgentDefinition(
            "agent-alpha", "Alpha", "Engineer", "Test agent", "You are Alpha.",
            null, [], [], true,
            Permissions: new CommandPermissionSet(["*"], []));

        _explicitAllowAgent = new AgentDefinition(
            "agent-beta", "Beta", "Engineer", "Test agent", "You are Beta.",
            null, [], [], true,
            Permissions: new CommandPermissionSet(
                ["READ_FILE", "SEARCH_CODE", "LIST_ROOMS", "LIST_AGENTS", "LIST_TASKS",
                 "REMEMBER", "RECALL", "APPROVE_TASK", "MERGE_TASK"],
                []));

        _prefixWildcardAgent = new AgentDefinition(
            "agent-gamma", "Gamma", "Engineer", "Test agent", "You are Gamma.",
            null, [], [], true,
            Permissions: new CommandPermissionSet(
                ["READ_*", "LIST_*", "SEARCH_*"], []));

        _deniedAgent = new AgentDefinition(
            "agent-delta", "Delta", "Engineer", "Test agent", "You are Delta.",
            null, [], [], true,
            Permissions: new CommandPermissionSet(
                ["*"], ["READ_FILE", "SHELL"]));

        _noPermissionsAgent = new AgentDefinition(
            "agent-epsilon", "Epsilon", "Engineer", "Test agent", "You are Epsilon.",
            null, [], [], true);
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Authorize")]
    public object? WildcardAllow() => _authorizer.Authorize(_readFileCommand, _wildcardAgent);

    [Benchmark]
    [BenchmarkCategory("Authorize")]
    public object? ExplicitAllow() => _authorizer.Authorize(_readFileCommand, _explicitAllowAgent);

    [Benchmark]
    [BenchmarkCategory("Authorize")]
    public object? PrefixWildcard() => _authorizer.Authorize(_readFileCommand, _prefixWildcardAgent);

    [Benchmark]
    [BenchmarkCategory("Authorize")]
    public object? DenyOverride() => _authorizer.Authorize(_readFileCommand, _deniedAgent);

    [Benchmark]
    [BenchmarkCategory("Authorize")]
    public object? NoPermissions() => _authorizer.Authorize(_readFileCommand, _noPermissionsAgent);
}
