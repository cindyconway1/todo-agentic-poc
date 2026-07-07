using System.Text.RegularExpressions;
using Csla;
using Csla.Core;
using Csla.Rules;
using Microsoft.EntityFrameworkCore;
using ToDo.Business.Exceptions;
using ToDo.Business.Security;
using ToDo.DataAccess;

namespace ToDo.Business;

[Serializable]
public class UserEdit : BusinessBase<UserEdit>
{
    public const int MinPasswordLength = 8;
    public const int MaxPasswordLength = 128;

    public static readonly PropertyInfo<Guid> IdProperty = RegisterProperty<Guid>(nameof(Id));
    public Guid Id => GetProperty(IdProperty);

    public static readonly PropertyInfo<string> EmailProperty = RegisterProperty<string>(nameof(Email));
    public string Email
    {
        get => GetProperty(EmailProperty) ?? string.Empty;
        set => SetProperty(EmailProperty, value);
    }

    public static readonly PropertyInfo<string> PasswordProperty = RegisterProperty<string>(nameof(Password));
    public string Password
    {
        get => GetProperty(PasswordProperty) ?? string.Empty;
        set => SetProperty(PasswordProperty, value);
    }

    protected override void AddBusinessRules()
    {
        base.AddBusinessRules();
        BusinessRules.AddRule(new EmailFormatRule(EmailProperty));
        BusinessRules.AddRule(new PasswordPolicyRule(PasswordProperty, MinPasswordLength, MaxPasswordLength));
    }

    [Create]
    private void Create()
    {
        LoadProperty(IdProperty, Guid.NewGuid());

        // Rules only re-run when their input property is Set; run them once up front so
        // Required violations surface even for properties the caller never touches.
        BusinessRules.CheckRules();
    }

    [Insert]
    private async Task InsertAsync([Inject] ApplicationDbContext dbContext, [Inject] IPasswordHasher passwordHasher)
    {
        var email = (ReadProperty(EmailProperty) ?? string.Empty).Trim();
        var password = ReadProperty(PasswordProperty) ?? string.Empty;

        var alreadyExists = await dbContext.Users.AnyAsync(u => u.Email == email);
        if (alreadyExists)
            throw new DuplicateEmailException(email);

        var entity = new User
        {
            Id = ReadProperty(IdProperty),
            Email = email,
            PasswordHash = passwordHasher.Hash(password)
        };

        dbContext.Users.Add(entity);

        try
        {
            await dbContext.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (IsUniqueEmailViolation(ex))
        {
            throw new DuplicateEmailException(email);
        }
    }

    private static bool IsUniqueEmailViolation(DbUpdateException ex) =>
        ex.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 };

    private sealed class EmailFormatRule : BusinessRule
    {
        private static readonly Regex Pattern = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);

        public EmailFormatRule(IPropertyInfo primaryProperty) : base(primaryProperty)
        {
            InputProperties.Add(primaryProperty);
        }

        protected override void Execute(IRuleContext context)
        {
            var email = (string?)context.InputPropertyValues[PrimaryProperty!];
            if (string.IsNullOrWhiteSpace(email))
            {
                context.AddErrorResult("Email is required.");
                return;
            }

            if (!Pattern.IsMatch(email))
            {
                context.AddErrorResult("Email must be a valid email address.");
            }
        }
    }

    private sealed class PasswordPolicyRule : BusinessRule
    {
        private readonly int _minLength;
        private readonly int _maxLength;

        public PasswordPolicyRule(IPropertyInfo primaryProperty, int minLength, int maxLength) : base(primaryProperty)
        {
            _minLength = minLength;
            _maxLength = maxLength;
            InputProperties.Add(primaryProperty);
        }

        protected override void Execute(IRuleContext context)
        {
            var password = (string?)context.InputPropertyValues[PrimaryProperty!];
            if (string.IsNullOrEmpty(password))
            {
                context.AddErrorResult("Password is required.");
                return;
            }

            if (password.Length < _minLength || password.Length > _maxLength)
            {
                context.AddErrorResult($"Password must be between {_minLength} and {_maxLength} characters.");
            }
        }
    }
}
