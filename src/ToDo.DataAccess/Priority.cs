namespace ToDo.DataAccess;

/// <summary>
/// Reference lookup for to-do item priority (BE-10): a seeded physical table with fixed ids
/// (1 High, 2 Medium, 3 Low) — the single source of truth for valid priorities. SortOrder
/// drives the §7 priority-first sort (lower sorts first; items with no priority sort last).
/// </summary>
public class Priority
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int SortOrder { get; set; }
}
