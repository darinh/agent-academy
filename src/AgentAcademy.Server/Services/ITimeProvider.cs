namespace AgentAcademy.Server.Services;

/// <summary>
/// Provides access to the current UTC time.
/// </summary>
public interface ITimeProvider
{
    /// <summary>
    /// Gets the current UTC date and time.
    /// </summary>
    DateTime UtcNow { get; }
}

/// <summary>
/// Production implementation of <see cref="ITimeProvider"/> backed by the system clock.
/// </summary>
public sealed class SystemTimeProvider : ITimeProvider
{
    /// <summary>
    /// Gets the current UTC date and time from <see cref="DateTime.UtcNow"/>.
    /// </summary>
    public DateTime UtcNow => DateTime.UtcNow;
}
