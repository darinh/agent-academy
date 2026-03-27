# 001 — Domain Model

## Purpose
Defines the core domain types used throughout Agent Academy. These types live in `AgentAcademy.Shared` and are referenced by the server, tests, and (via API responses) the frontend.

## Current Behavior

> **Status: Planned** — Types defined in spec only. No code implemented yet.

### Enumerations

```csharp
// Phases of a collaboration session
public enum CollaborationPhase
{
    Planning,       // Agents discuss approach
    Execution,      // Agents write code
    Review,         // Adversarial review of results
    Complete        // Task finished
}

// Roles an agent can play in a room
public enum AgentRole
{
    Lead,           // Drives the task forward
    Contributor,    // Writes code or content
    Reviewer,       // Reviews others' work
    Observer        // Watches but doesn't participate
}

// Types of messages in a collaboration
public enum MessageKind
{
    Chat,           // Free-form discussion
    CodeBlock,      // Code snippet with language tag
    Proposal,       // Suggested approach or change
    Decision,       // Accepted decision
    SystemEvent     // Room state change (agent joined, phase changed, etc.)
}

// Room lifecycle states
public enum RoomStatus
{
    Open,           // Active collaboration
    Paused,         // Temporarily suspended
    Closed          // Completed or abandoned
}

// Task lifecycle states
public enum TaskStatus
{
    Pending,        // Not started
    InProgress,     // Being worked on
    InReview,       // Awaiting adversarial review
    Done,           // Completed successfully
    Failed          // Could not be completed
}
```

### Records

```csharp
// Defines an agent's identity and capabilities
public record AgentDefinition(
    string Id,
    string Name,
    AgentRole Role,
    string SystemPrompt,
    DateTimeOffset CreatedAt
);

// Snapshot of a collaboration room's state
public record RoomSnapshot(
    string Id,
    string Title,
    RoomStatus Status,
    CollaborationPhase Phase,
    IReadOnlyList<string> AgentIds,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);

// Snapshot of a task's state
public record TaskSnapshot(
    string Id,
    string RoomId,
    string Title,
    string Description,
    TaskStatus Status,
    string? AssignedAgentId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);

// A message in a collaboration room
public record ChatEnvelope(
    string Id,
    string RoomId,
    string SenderId,
    MessageKind Kind,
    string Content,
    DateTimeOffset Timestamp
);
```

### Notification Types

```csharp
// A notification to be delivered via a provider
public record NotificationMessage(
    string Title,
    string Body,
    string? Url,
    NotificationSeverity Severity
);

public enum NotificationSeverity
{
    Info,
    Warning,
    Error
}

// Provider interface for pluggable notification delivery
public interface INotificationProvider
{
    string Name { get; }
    Task SendAsync(NotificationMessage message, CancellationToken ct = default);
}
```

## Interfaces & Contracts

All types above are planned for `AgentAcademy.Shared`. They will be:
- C# records (immutable by default)
- Serializable to/from JSON via System.Text.Json
- Used in API responses and SignalR messages

## Invariants

- All domain types are immutable records
- All IDs are non-empty strings
- `RoomSnapshot.AgentIds` is never null (empty list if no agents)
- `ChatEnvelope.Timestamp` is always UTC
- `INotificationProvider` implementations must be thread-safe

## Known Gaps

- No persistence mapping (EF Core entity configuration) defined yet
- No validation attributes or FluentValidation rules specified
- Relationship between rooms, tasks, and agents not fully specified
- Event sourcing vs. CRUD approach not decided

## Revision History

| Date | Change | Task |
|------|--------|------|
| Initial | Created domain model spec with planned types | scaffold-solution |
