using Csla;
using Csla.Core;
using ToDo.DataAccess;

namespace ToDo.Business;

[Serializable]
public class VolunteerInfo : ReadOnlyBase<VolunteerInfo>
{
    public static readonly PropertyInfo<Guid> IdProperty = RegisterProperty<Guid>(c => c.Id);
    public Guid Id
    {
        get => GetProperty(IdProperty);
        private set => LoadProperty(IdProperty, value);
    }

    public static readonly PropertyInfo<string> NameProperty = RegisterProperty<string>(c => c.Name, "Name", "");
    public string Name
    {
        get => GetProperty(NameProperty) ?? "";
        private set => LoadProperty(NameProperty, value);
    }

    public static readonly PropertyInfo<Guid?> LeagueIdProperty = RegisterProperty<Guid?>(c => c.LeagueId);
    public Guid? LeagueId
    {
        get => GetProperty(LeagueIdProperty);
        private set => LoadProperty(LeagueIdProperty, value);
    }

    // MobileList (not List<Guid>) because the local data portal clones via MobileFormatter,
    // which silently drops managed fields that aren't primitives or IMobileObject.
    public static readonly PropertyInfo<MobileList<Guid>> TeamIdsProperty =
        RegisterProperty<MobileList<Guid>>(nameof(TeamIds));
    public IReadOnlyList<Guid> TeamIds => GetProperty(TeamIdsProperty) ?? [];

    [FetchChild]
    private void FetchChild(Volunteer entity, List<Guid> teamIds)
    {
        Id = entity.Id;
        Name = entity.Name;
        LeagueId = entity.LeagueId;
        LoadProperty(TeamIdsProperty, new MobileList<Guid>(teamIds));
    }
}
