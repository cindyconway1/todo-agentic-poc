using ToDo.Business.Services;

namespace ToDo.UnitTests;

// AC-mapped: Argon2 hash/verify round-trip (BE-02 unit test list).
public class Argon2IdPasswordHasherTests
{
    private readonly IPasswordHasher _hasher = new Argon2IdPasswordHasher();

    [Fact]
    public void Hash_ThenVerify_WithSamePassword_ReturnsTrue()
    {
        var hash = _hasher.Hash("correct horse battery staple");

        Assert.True(_hasher.Verify(hash, "correct horse battery staple"));
    }

    [Fact]
    public void Verify_WithWrongPassword_ReturnsFalse()
    {
        var hash = _hasher.Hash("correct horse battery staple");

        Assert.False(_hasher.Verify(hash, "Correct Horse Battery Staple"));
    }

    [Fact]
    public void Hash_ProducesArgon2idEncodedFormat()
    {
        var hash = _hasher.Hash("password123");

        Assert.StartsWith("$argon2id$v=19$", hash);
    }

    [Fact]
    public void Hash_IsSaltedSoSamePasswordProducesDifferentHashes_BothVerify()
    {
        var first = _hasher.Hash("password123");
        var second = _hasher.Hash("password123");

        Assert.NotEqual(first, second); // random per-hash salt
        Assert.True(_hasher.Verify(first, "password123"));
        Assert.True(_hasher.Verify(second, "password123"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("$argon2id$v=19$badparams$$")]
    [InlineData("$bcrypt$v=19$m=1,t=1,p=1$c2FsdA==$aGFzaA==")]
    public void Verify_WithMalformedHash_ReturnsFalseInsteadOfThrowing(string malformed)
    {
        Assert.False(_hasher.Verify(malformed, "password123"));
    }
}
