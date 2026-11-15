using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Generators;

namespace LINEAPI.Registration;

internal static class RegistrationPasswordHasher
{
    private const string Operation = "getPasswordHashingParametersForPwdReg";
    private static readonly SemaphoreSlim Slots = new(Math.Clamp(Environment.ProcessorCount / 2, 1, 4));

    internal static async Task<string> HashAsync(
        string password, string keyBase64, string saltBase64, string nrp, int length, CancellationToken ct)
    {
        if (!uint.TryParse(nrp, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var packed) ||
            length is < 1 or > 128 || keyBase64.Length > 1024 || saltBase64.Length > 1024)
            throw new LineApiException(Operation, LineFailure.Protocol);

        var exponent = (int)(packed >> 16);
        var blockSize = (int)((packed >> 8) & 0xff);
        var parallelization = (int)(packed & 0xff);
        if (exponent is < 1 or > 20 || blockSize < 1 || parallelization < 1)
            throw new LineApiException(Operation, LineFailure.Protocol);

        var cost = 1 << exponent;
        if (128L * cost * blockSize > 64L * 1024 * 1024 || (long)cost * blockSize * parallelization > 1_048_576)
            throw new LineApiException(Operation, LineFailure.Protocol);

        await Slots.WaitAsync(ct);
        try
        {
            var hash = await Task.Run(() => Derive(password, keyBase64, saltBase64, cost, blockSize, parallelization, length), ct);
            ct.ThrowIfCancellationRequested();
            return $"$s0${nrp}${saltBase64}${hash}";
        }
        finally
        {
            Slots.Release();
        }
    }

    private static string Derive(string password, string keyBase64, string saltBase64, int cost, int blockSize, int parallelization, int length)
    {
        byte[]? key = null;
        byte[]? passwordBytes = null;
        byte[]? intermediate = null;
        byte[]? hash = null;
        try
        {
            key = Convert.FromBase64String(keyBase64);
            var salt = Convert.FromBase64String(saltBase64);
            if (key.Length == 0 || salt.Length == 0)
                throw new LineApiException(Operation, LineFailure.Protocol);

            passwordBytes = Encoding.UTF8.GetBytes(password);
            intermediate = HMACSHA256.HashData(key, passwordBytes);
            hash = SCrypt.Generate(intermediate, salt, cost, blockSize, parallelization, length);
            return Convert.ToBase64String(hash);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            throw new LineApiException(Operation, LineFailure.Protocol);
        }
        finally
        {
            foreach (var buffer in new[] { key, passwordBytes, intermediate, hash })
                if (buffer is not null)
                    CryptographicOperations.ZeroMemory(buffer);
        }
    }
}
