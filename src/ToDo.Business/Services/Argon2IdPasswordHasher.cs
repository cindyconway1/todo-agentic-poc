using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace ToDo.Business.Services;

// OWASP-aligned starting parameters: 19 MiB memory, 2 iterations, 1 degree of parallelism.
public sealed class Argon2IdPasswordHasher : IPasswordHasher
{
    private const int SaltSizeBytes = 16;
    private const int HashSizeBytes = 32;
    private const int MemorySizeKib = 19456;
    private const int Iterations = 2;
    private const int Parallelism = 1;

    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
        var hash = ComputeHash(password, salt, MemorySizeKib, Iterations, Parallelism, HashSizeBytes);

        return string.Create(CultureInfo.InvariantCulture,
            $"$argon2id$v=19$m={MemorySizeKib},t={Iterations},p={Parallelism}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}");
    }

    public bool Verify(string encodedHash, string password)
    {
        if (!TryParse(encodedHash, out var memoryKib, out var iterations, out var parallelism, out var salt, out var expectedHash))
        {
            return false;
        }

        var actualHash = ComputeHash(password, salt, memoryKib, iterations, parallelism, expectedHash.Length);
        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
    }

    private static byte[] ComputeHash(string password, byte[] salt, int memoryKib, int iterations, int parallelism, int hashSize)
    {
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            DegreeOfParallelism = parallelism,
            Iterations = iterations,
            MemorySize = memoryKib,
        };
        return argon2.GetBytes(hashSize);
    }

    private static bool TryParse(
        string encodedHash,
        out int memoryKib,
        out int iterations,
        out int parallelism,
        out byte[] salt,
        out byte[] hash)
    {
        memoryKib = 0;
        iterations = 0;
        parallelism = 0;
        salt = [];
        hash = [];

        // Format: $argon2id$v=19$m=<kib>,t=<iterations>,p=<parallelism>$<saltBase64>$<hashBase64>
        var parts = encodedHash.Split('$', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5 || parts[0] != "argon2id")
        {
            return false;
        }

        var parameters = parts[2].Split(',');
        if (parameters.Length != 3)
        {
            return false;
        }

        try
        {
            memoryKib = int.Parse(parameters[0].Split('=')[1], CultureInfo.InvariantCulture);
            iterations = int.Parse(parameters[1].Split('=')[1], CultureInfo.InvariantCulture);
            parallelism = int.Parse(parameters[2].Split('=')[1], CultureInfo.InvariantCulture);
            salt = Convert.FromBase64String(parts[3]);
            hash = Convert.FromBase64String(parts[4]);
        }
        catch (Exception ex) when (ex is FormatException or IndexOutOfRangeException or OverflowException)
        {
            return false;
        }

        return true;
    }
}
