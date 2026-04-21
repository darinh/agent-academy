using Xunit;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Serializes test classes that call Directory.SetCurrentDirectory(),
/// which is a process-global mutation incompatible with parallel execution.
/// DisableParallelization prevents these tests from running alongside ANY
/// other test collection, eliminating cwd-related race conditions.
/// </summary>
[CollectionDefinition("CwdMutating", DisableParallelization = true)]
public sealed class CwdMutatingCollection;
