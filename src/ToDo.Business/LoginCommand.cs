using Csla;
using Microsoft.EntityFrameworkCore;
using ToDo.Business.Services;
using ToDo.DataAccess;

namespace ToDo.Business;

[Serializable]
public class LoginCommand : CommandBase<LoginCommand>
{
    // Well-formed but unusable hash so a lookup miss still pays the full Argon2 cost,
    // keeping unknown-email and wrong-password timings indistinguishable.
    private static readonly string DummyHash =
        $"$argon2id$v=19$m=19456,t=2,p=1${Convert.ToBase64String(new byte[16])}${Convert.ToBase64String(new byte[32])}";

    public static readonly PropertyInfo<bool> SucceededProperty = RegisterProperty<bool>(nameof(Succeeded));
    public bool Succeeded
    {
        get => ReadProperty(SucceededProperty);
        private set => LoadProperty(SucceededProperty, value);
    }

    public static readonly PropertyInfo<Guid?> UserIdProperty = RegisterProperty<Guid?>(nameof(UserId));
    public Guid? UserId
    {
        get => ReadProperty(UserIdProperty);
        private set => LoadProperty(UserIdProperty, value);
    }

    public static readonly PropertyInfo<string?> EmailProperty = RegisterProperty<string?>(nameof(Email));
    public string? Email
    {
        get => ReadProperty(EmailProperty);
        private set => LoadProperty(EmailProperty, value);
    }

    [Execute]
    private async Task ExecuteAsync(
        string email,
        string password,
        [Inject] ApplicationDbContext dbContext,
        [Inject] IPasswordHasher passwordHasher)
    {
        var normalizedEmail = email.Trim();
        var user = await dbContext.Users.SingleOrDefaultAsync(u => u.Email == normalizedEmail);

        var hashToVerify = user?.PasswordHash ?? DummyHash;
        var passwordMatches = passwordHasher.Verify(hashToVerify, password);

        if (user is not null && passwordMatches)
        {
            Succeeded = true;
            UserId = user.Id;
            Email = user.Email;
        }
        else
        {
            // Generic failure: never reveal whether the email or the password was wrong.
            Succeeded = false;
            UserId = null;
            Email = null;
        }
    }
}
