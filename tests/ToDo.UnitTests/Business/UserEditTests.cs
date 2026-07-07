using Csla;
using Csla.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ToDo.Business;

namespace ToDo.UnitTests.Business;

public class UserEditTests
{
    private static IDataPortal<UserEdit> CreatePortal()
    {
        var services = new ServiceCollection();
        services.AddCsla();
        return services.BuildServiceProvider().GetRequiredService<IDataPortal<UserEdit>>();
    }

    [Fact]
    public async Task Email_Missing_IsInvalid()
    {
        var user = await CreatePortal().CreateAsync();
        user.Password = "validpassword1";

        Assert.False(user.IsValid);
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("missing-at-sign.com")]
    [InlineData("   ")]
    public async Task Email_InvalidFormat_IsInvalid(string email)
    {
        var user = await CreatePortal().CreateAsync();
        user.Email = email;
        user.Password = "validpassword1";

        Assert.False(user.IsValid);
    }

    [Fact]
    public async Task Email_ValidFormat_PassesEmailRule()
    {
        var user = await CreatePortal().CreateAsync();
        user.Email = "user@example.com";
        user.Password = "validpassword1";

        Assert.True(user.IsValid);
    }

    [Fact]
    public async Task Password_Missing_IsInvalid()
    {
        var user = await CreatePortal().CreateAsync();
        user.Email = "user@example.com";

        Assert.False(user.IsValid);
    }

    [Fact]
    public async Task Password_AtMinLength_IsAccepted()
    {
        var user = await CreatePortal().CreateAsync();
        user.Email = "user@example.com";
        user.Password = new string('a', UserEdit.MinPasswordLength);

        Assert.True(user.IsValid);
    }

    [Fact]
    public async Task Password_OneBelowMinLength_IsRejected()
    {
        var user = await CreatePortal().CreateAsync();
        user.Email = "user@example.com";
        user.Password = new string('a', UserEdit.MinPasswordLength - 1);

        Assert.False(user.IsValid);
    }

    [Fact]
    public async Task Password_AtMaxLength_IsAccepted()
    {
        var user = await CreatePortal().CreateAsync();
        user.Email = "user@example.com";
        user.Password = new string('a', UserEdit.MaxPasswordLength);

        Assert.True(user.IsValid);
    }

    [Fact]
    public async Task Password_OneAboveMaxLength_IsRejected()
    {
        var user = await CreatePortal().CreateAsync();
        user.Email = "user@example.com";
        user.Password = new string('a', UserEdit.MaxPasswordLength + 1);

        Assert.False(user.IsValid);
    }

    [Fact]
    public async Task Password_HasNoForcedCompositionRules()
    {
        // Length-over-complexity policy: an all-lowercase, no-digit, no-symbol password
        // of sufficient length must be accepted (no mandatory character-class rules).
        var user = await CreatePortal().CreateAsync();
        user.Email = "user@example.com";
        user.Password = new string('a', UserEdit.MinPasswordLength);

        Assert.True(user.IsValid);
    }
}
