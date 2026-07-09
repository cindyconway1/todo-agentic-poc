using Csla;
using ToDo.DataAccess;

namespace ToDo.Business;

[Serializable]
public class TeamInfo : ReadOnlyBase<TeamInfo>
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

    [FetchChild]
    private void FetchChild(Team entity)
    {
        Id = entity.Id;
        Name = entity.Name;
        LeagueId = entity.LeagueId;
    }
}
