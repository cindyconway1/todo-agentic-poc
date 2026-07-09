namespace ToDo.DataAccess;

public class Volunteer : AuditableEntity
{
    public Guid Id { get; set; }
    public Guid OwnerUserId { get; set; }
    public string Name { get; set; } = "";
    // Optional league tag; the FK is ON DELETE SET NULL so deleting the league clears the tag.
    public Guid? LeagueId { get; set; }
}
