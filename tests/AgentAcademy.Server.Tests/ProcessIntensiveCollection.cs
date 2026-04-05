using Xunit;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Serializes test classes that spawn external processes (shell scripts, CLI wrappers).
/// Under full-suite parallelism (1300+ tests), process resource contention causes
/// intermittent failures. Serializing these classes eliminates the flakiness.
/// </summary>
[CollectionDefinition("ProcessIntensive")]
public sealed class ProcessIntensiveCollection;
