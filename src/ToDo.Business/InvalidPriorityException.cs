namespace ToDo.Business;

/// <summary>
/// Thrown by the TodoItemEdit data portal when a non-null PriorityId does not exist in the
/// Priorities lookup (BE-10). Business-layer defense-in-depth ahead of the DB foreign key;
/// the API maps it to the contractual 422 validation shape.
/// </summary>
public class InvalidPriorityException : Exception
{
    public InvalidPriorityException(int priorityId)
        : base($"Priority {priorityId} does not exist.")
    {
        PriorityId = priorityId;
    }

    public int PriorityId { get; }
}
