using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace ToDo.Business.Security;

/// <summary>
/// Hashes and verifies passwords with Argon2id, storing parameters alongside the hash
/// (PHC-style encoded string) so verification doesn't depend on fixed constants forever.
/// </summary>
public sealed class Argon2IdPasswordHasher : IPasswordHasher
{
    private const int SaltSizeBytes = 16;
    private const int HashSizeBytes = 32;
    private const int MemorySizeKib = 19456;
    private const int Iterations = 2;
    private const int DegreeOfParallelism = 1;
    private const int AlgorithmVersion = 19;

    public string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        var salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
        var hash = ComputeHash(password, salt, MemorySizeKib, Iterations, DegreeOfParallelism, HashSizeBytes);

        return $"$argon2id$v={AlgorithmVersion}$m={MemorySizeKib},t={Iterations},p={DegreeOfParallelism}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public bool Verify(string password, string encodedHash)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        ArgumentException.ThrowIfNullOrEmpty(encodedHash);

        var parts = encodedHash.Split('$', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5 || parts[0] != "argon2id")
            return false;

        var parameters = parts[2].Split(',');
        if (parameters.Length != 3)
            return false;

        var memorySize = int.Parse(parameters[0].Split('=')[1]);
        var iterations = int.Parse(parameters[1].Split('=')[1]);
        var parallelism = int.Parse(parameters[2].Split('=')[1]);

        var salt = Convert.FromBase64String(parts[3]);
        var expectedHash = Convert.FromBase64String(parts[4]);

        var actualHash = ComputeHash(password, salt, memorySize, iterations, parallelism, expectedHash.Length);

        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
    }

    private static byte[] ComputeHash(string password, byte[] salt, int memorySizeKib, int iterations, int degreeOfParallelism, int hashSize)
    {
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = memorySizeKib,
            Iterations = iterations,
            DegreeOfParallelism = degreeOfParallelism
        };

        return argon2.GetBytes(hashSize);
    }
}
