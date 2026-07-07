using Csla;
using Microsoft.EntityFrameworkCore;
using ToDo.Business.Security;
using ToDo.DataAccess;

namespace ToDo.Business;

[Serializable]
public class LoginCommand : CommandBase<LoginCommand>
{
    // Verified against a fixed dummy hash when no account matches, so an unknown email
    // costs roughly the same time as a wrong password — the outcome is generic either way.
    private const string DummyHash =
        "$argon2id$v=19$m=19456,t=2,p=1$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

    public static readonly PropertyInfo<string> EmailProperty = RegisterProperty<string>(nameof(Email));
    public string Email
    {
        get => ReadProperty(EmailProperty) ?? string.Empty;
        set => LoadProperty(EmailProperty, value);
    }

    public static readonly PropertyInfo<string> PasswordProperty = RegisterProperty<string>(nameof(Password));
    public string Password
    {
        get => ReadProperty(PasswordProperty) ?? string.Empty;
        set => LoadProperty(PasswordProperty, value);
    }

    public static readonly PropertyInfo<bool> SuccessProperty = RegisterProperty<bool>(nameof(Success));
    public bool Success => ReadProperty(SuccessProperty);

    public static readonly PropertyInfo<Guid?> UserIdProperty = RegisterProperty<Guid?>(nameof(UserId));
    public Guid? UserId => ReadProperty(UserIdProperty);

    public static readonly PropertyInfo<string?> UserEmailProperty = RegisterProperty<string?>(nameof(UserEmail));
    public string? UserEmail => ReadProperty(UserEmailProperty);

    [Execute]
    private async Task ExecuteAsync([Inject] ApplicationDbContext dbContext, [Inject] IPasswordHasher passwordHasher)
    {
        var email = ReadProperty(EmailProperty)?.Trim() ?? string.Empty;
        var password = ReadProperty(PasswordProperty) ?? string.Empty;

        var user = await dbContext.Users.SingleOrDefaultAsync(u => u.Email == email);

        var result = EvaluateCredentials(user, password, passwordHasher);

        LoadProperty(SuccessProperty, result.Success);
        LoadProperty(UserIdProperty, result.UserId);
        LoadProperty(UserEmailProperty, result.Email);
    }

    internal static (bool Success, Guid? UserId, string? Email) EvaluateCredentials(
        User? user, string password, IPasswordHasher passwordHasher)
    {
        if (user is null)
        {
            passwordHasher.Verify(password, DummyHash);
            return (false, null, null);
        }

        if (!passwordHasher.Verify(password, user.PasswordHash))
        {
            return (false, null, null);
        }

        return (true, user.Id, user.Email);
    }
}
