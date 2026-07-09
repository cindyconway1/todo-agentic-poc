namespace ToDo.DataAccess;

// Volunteer↔Team many-to-many tag set. Composite PK (VolunteerId, TeamId); the join carries no
// fields of its own. Both FKs cascade on delete: deleting a volunteer or a team removes its tag rows.
public class VolunteerTeam
{
    public Guid VolunteerId { get; set; }
    public Guid TeamId { get; set; }
}
