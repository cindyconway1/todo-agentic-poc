using ToDo.Business.Security;

namespace ToDo.UnitTests.Business;

public class Argon2IdPasswordHasherTests
{
    [Fact]
    public void Hash_ThenVerify_RoundTripsSuccessfully()
    {
        var hasher = new Argon2IdPasswordHasher();
        var hash = hasher.Hash("correct horse battery staple");

        Assert.True(hasher.Verify("correct horse battery staple", hash));
    }

    [Fact]
    public void Verify_WithWrongPassword_Fails()
    {
        var hasher = new Argon2IdPasswordHasher();
        var hash = hasher.Hash("correct horse battery staple");

        Assert.False(hasher.Verify("wrong password", hash));
    }

    [Fact]
    public void Hash_ProducesArgon2idEncodedString()
    {
        var hasher = new Argon2IdPasswordHasher();
        var hash = hasher.Hash("correct horse battery staple");

        Assert.StartsWith("$argon2id$", hash);
    }

    [Fact]
    public void Hash_ProducesDifferentSaltEachTime()
    {
        var hasher = new Argon2IdPasswordHasher();
        var first = hasher.Hash("same password");
        var second = hasher.Hash("same password");

        Assert.NotEqual(first, second);
    }
}
