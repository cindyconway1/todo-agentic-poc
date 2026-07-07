using Csla;
using Csla.Rules.CommonRules;
using Microsoft.EntityFrameworkCore;
using ToDo.Business.Services;
using ToDo.DataAccess;

namespace ToDo.Business;

[Serializable]
public class UserEdit : BusinessBase<UserEdit>
{
    public const int MinPasswordLength = 8;
    public const int MaxPasswordLength = 128;

    private const string EmailFormatPattern = @"^[^@\s]+@[^@\s]+\.[^@\s]+$";

    public static readonly PropertyInfo<Guid> IdProperty = RegisterProperty<Guid>(c => c.Id);
    public Guid Id
    {
        get => GetProperty(IdProperty);
        private set => LoadProperty(IdProperty, value);
    }

    public static readonly PropertyInfo<string> EmailProperty = RegisterProperty<string>(c => c.Email, "Email", "");
    public string Email
    {
        get => GetProperty(EmailProperty) ?? "";
        set => SetProperty(EmailProperty, value);
    }

    public static readonly PropertyInfo<string> PasswordProperty = RegisterProperty<string>(c => c.Password, "Password", "");
    public string Password
    {
        get => GetProperty(PasswordProperty) ?? "";
        set => SetProperty(PasswordProperty, value);
    }

    protected override void AddBusinessRules()
    {
        base.AddBusinessRules();

        BusinessRules.AddRule(new Required(EmailProperty));
        BusinessRules.AddRule(new MaxLength(EmailProperty, 256));
        BusinessRules.AddRule(new RegExMatch(EmailProperty, EmailFormatPattern, "Email is not a valid email address."));

        BusinessRules.AddRule(new Required(PasswordProperty));
        BusinessRules.AddRule(new MinLength(PasswordProperty, MinPasswordLength));
        BusinessRules.AddRule(new MaxLength(PasswordProperty, MaxPasswordLength));
    }

    [Create]
    private void Create()
    {
        Id = Guid.NewGuid();
        BusinessRules.CheckRules();
    }

    [Insert]
    private async Task InsertAsync([Inject] ApplicationDbContext dbContext, [Inject] IPasswordHasher passwordHasher)
    {
        var normalizedEmail = Email.Trim();

        var entity = new User
        {
            Id = Id,
            Email = normalizedEmail,
            PasswordHash = passwordHasher.Hash(Password),
        };

        dbContext.Users.Add(entity);

        try
        {
            await dbContext.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            throw new DuplicateEmailException(normalizedEmail);
        }
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException ex) =>
        ex.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 };
}
