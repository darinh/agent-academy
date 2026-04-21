using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Config;

/// <summary>
/// Mutable singleton implementing <see cref="IAgentCatalog"/>.
/// Reads through a volatile reference so hot-reload swaps are
/// immediately visible to all consumers without lock contention.
/// </summary>
public sealed class AgentCatalog : IAgentCatalog
{
    private volatile AgentCatalogOptions _current;

    public string DefaultRoomId => _current.DefaultRoomId;
    public string DefaultRoomName => _current.DefaultRoomName;
    public IReadOnlyList<AgentDefinition> Agents => _current.Agents;

    public AgentCatalog(AgentCatalogOptions initial)
    {
        _current = initial ?? throw new ArgumentNullException(nameof(initial));
    }

    /// <summary>
    /// Returns the underlying snapshot. Used internally by the loader
    /// and watcher — consumers should use the interface properties.
    /// </summary>
    internal AgentCatalogOptions Snapshot => _current;

    /// <summary>
    /// Atomically swaps the catalog data. Existing readers that already
    /// captured a reference to <see cref="Agents"/> continue to iterate
    /// the old list safely; new reads see the updated list.
    /// </summary>
    internal void Update(AgentCatalogOptions newCatalog)
    {
        _current = newCatalog ?? throw new ArgumentNullException(nameof(newCatalog));
    }
}
