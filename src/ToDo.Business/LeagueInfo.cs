using Csla;
using ToDo.DataAccess;

namespace ToDo.Business;

[Serializable]
public class LeagueInfo : ReadOnlyBase<LeagueInfo>
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

    [FetchChild]
    private void FetchChild(League entity)
    {
        Id = entity.Id;
        Name = entity.Name;
    }
}
