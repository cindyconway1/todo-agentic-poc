namespace ToDo.Business;

/// <summary>
/// TypeID reference type (per CLAUDE.md: DB-backed reference data is a TypeID reference type,
/// not an enum) for the kind of entity a to-do list is scoped to. The closed set of instances
/// below is the source of truth; the integer <see cref="Id"/> is what persists in
/// <c>TodoList.ScopeTypeId</c>. Instances are singletons, so reference equality works.
/// </summary>
public sealed class ScopeType
{
    public static readonly ScopeType League = new(1, "League");
    public static readonly ScopeType Team = new(2, "Team");
    public static readonly ScopeType Volunteer = new(3, "Volunteer");

    public static IReadOnlyList<ScopeType> All { get; } = [League, Team, Volunteer];

    private ScopeType(int id, string name)
    {
        Id = id;
        Name = name;
    }

    public int Id { get; }
    public string Name { get; }

    public static bool IsKnownId(int id) => TryFromId(id) is not null;

    public static ScopeType? TryFromId(int id) => All.FirstOrDefault(t => t.Id == id);

    public static ScopeType FromId(int id) =>
        TryFromId(id) ?? throw new ArgumentOutOfRangeException(nameof(id), id, "Unknown scope type id.");

    public static ScopeType? TryFromName(string? name) =>
        All.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    public override string ToString() => Name;
}
