using ToDo.DataAccess;

namespace ToDo.Business;

/// <summary>
/// In-memory projection rows assembled by <see cref="DashboardInfo"/>'s fixed set of queries and
/// handed down the child-fetch chain as plain method arguments. They are never stored in managed
/// fields (only their scalar values are), so plain records are safe here despite MobileFormatter.
/// </summary>
internal sealed record DashboardGroupData(Guid EntityId, string EntityName, List<DashboardListData> Lists);

/// <summary>One list row for the dashboard; Items are pre-filtered (incomplete) and pre-sorted.</summary>
internal sealed record DashboardListData(Guid ListId, string ListName, List<TodoItem> Items);
