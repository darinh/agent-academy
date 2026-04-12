using Xunit;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Serializes test classes that call InitializationService.InitializeAsync(),
/// which mutates the static CurrentCrashDetected/CurrentInstanceId properties
/// on CrashRecoveryService. Without serialization, parallel test classes race
/// on those statics and cause intermittent failures.
/// </summary>
[CollectionDefinition("WorkspaceRuntime")]
public sealed class WorkspaceRuntimeCollection;
