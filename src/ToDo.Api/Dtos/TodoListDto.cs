namespace ToDo.Api.Dtos;

public sealed class TodoListDto
{
    public Guid Id { get; set; }
    // ScopeType TypeID name: "League", "Team", or "Volunteer".
    public string ScopeType { get; set; } = "";
    public Guid ScopeEntityId { get; set; }
}
