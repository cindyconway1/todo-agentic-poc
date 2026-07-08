using Csla;
using Csla.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ToDo.Business;

namespace ToDo.UnitTests;

// AC-mapped: email-format validation + password-policy validation (BE-02 unit test list).
// Exercises UserEdit's business rules through a CSLA local data portal; no database is touched
// because [Create] only assigns an Id and runs rules.
public class UserEditValidationTests
{
    private const string ValidPassword = "password123";
    private const string ValidEmail = "user@example.com";

    private static async Task<UserEdit> NewUserAsync()
    {
        var services = new ServiceCollection();
        services.AddCsla();
        var provider = services.BuildServiceProvider();
        return await provider.GetRequiredService<IDataPortal<UserEdit>>().CreateAsync();
    }

    private static async Task<UserEdit> NewUserAsync(string email, string password)
    {
        var user = await NewUserAsync();
        user.Email = email;
        user.Password = password;
        return user;
    }

    [Theory]
    [InlineData("user@example.com")]
    [InlineData("first.last@sub.example.co.uk")]
    [InlineData("a@b.io")]
    public async Task Email_WithValidFormat_IsAccepted(string email)
    {
        var user = await NewUserAsync(email, ValidPassword);

        Assert.True(user.IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("notanemail")]
    [InlineData("missing@domain")]
    [InlineData("@example.com")]
    [InlineData("spaces in@example.com")]
    [InlineData("two@@example.com")]
    public async Task Email_WithInvalidFormat_IsRejected(string email)
    {
        var user = await NewUserAsync(email, ValidPassword);

        Assert.False(user.IsValid);
        Assert.Contains(user.BrokenRulesCollection, r => r.Property == nameof(UserEdit.Email));
    }

    [Theory]
    [InlineData(8, true)]    // minimum accepted
    [InlineData(7, false)]   // below minimum rejected
    [InlineData(128, true)]  // maximum accepted
    [InlineData(129, false)] // above maximum rejected
    public async Task Password_LengthPolicy_IsEnforced(int length, bool expectedValid)
    {
        var user = await NewUserAsync(ValidEmail, new string('a', length));

        Assert.Equal(expectedValid, user.IsValid);
        if (!expectedValid)
        {
            Assert.Contains(user.BrokenRulesCollection, r => r.Property == nameof(UserEdit.Password));
        }
    }

    [Fact]
    public async Task Password_Policy_ConstantsMatchSettledDecision()
    {
        // Guards the settled decision (min 8 / max 128) against silent drift.
        Assert.Equal(8, UserEdit.MinPasswordLength);
        Assert.Equal(128, UserEdit.MaxPasswordLength);
    }
}
