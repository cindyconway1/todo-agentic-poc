using ToDo.Business;
using ToDo.Business.Security;
using ToDo.DataAccess;

namespace ToDo.UnitTests.Business;

public class LoginCommandTests
{
    [Fact]
    public void EvaluateCredentials_UnknownEmail_ReturnsGenericFailure()
    {
        var result = LoginCommand.EvaluateCredentials(null, "any-password", new Argon2IdPasswordHasher());

        Assert.False(result.Success);
        Assert.Null(result.UserId);
        Assert.Null(result.Email);
    }

    [Fact]
    public void EvaluateCredentials_WrongPassword_ReturnsGenericFailure()
    {
        var hasher = new Argon2IdPasswordHasher();
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = "user@example.com",
            PasswordHash = hasher.Hash("correct-password")
        };

        var result = LoginCommand.EvaluateCredentials(user, "wrong-password", hasher);

        Assert.False(result.Success);
        Assert.Null(result.UserId);
        Assert.Null(result.Email);
    }

    [Fact]
    public void EvaluateCredentials_UnknownEmailAndWrongPassword_ProduceTheSameFailureShape()
    {
        // Neither failure path should be distinguishable from the other by its result shape,
        // since the API must return a single generic message regardless of which failed.
        var hasher = new Argon2IdPasswordHasher();
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = "user@example.com",
            PasswordHash = hasher.Hash("correct-password")
        };

        var unknownEmailResult = LoginCommand.EvaluateCredentials(null, "correct-password", hasher);
        var wrongPasswordResult = LoginCommand.EvaluateCredentials(user, "wrong-password", hasher);

        Assert.Equal(unknownEmailResult, wrongPasswordResult);
    }

    [Fact]
    public void EvaluateCredentials_CorrectPassword_Succeeds()
    {
        var hasher = new Argon2IdPasswordHasher();
        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            Email = "user@example.com",
            PasswordHash = hasher.Hash("correct-password")
        };

        var result = LoginCommand.EvaluateCredentials(user, "correct-password", hasher);

        Assert.True(result.Success);
        Assert.Equal(userId, result.UserId);
        Assert.Equal("user@example.com", result.Email);
    }
}
