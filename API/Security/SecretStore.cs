using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LineJobs.Configuration;

namespace LineJobs.Security;

internal sealed class SecretStore(ApiSettings settings)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const int NonceSize = 12;
    private const int TagSize = 16;

    internal string Seal<T>(string scope, T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        try
        {
            var buffer = new byte[NonceSize + TagSize + bytes.Length];
            var nonce = buffer.AsSpan(0, NonceSize);
            RandomNumberGenerator.Fill(nonce);
            using var cipher = new AesGcm(settings.StorageKey, TagSize);
            cipher.Encrypt(nonce, bytes, buffer.AsSpan(NonceSize + TagSize),
                buffer.AsSpan(NonceSize, TagSize), Encoding.UTF8.GetBytes(scope));
            return Convert.ToBase64String(buffer);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    internal T Open<T>(string scope, string encrypted)
    {
        var buffer = Convert.FromBase64String(encrypted);
        if (buffer.Length < NonceSize + TagSize)
            throw new CryptographicException();

        var bytes = new byte[buffer.Length - NonceSize - TagSize];
        try
        {
            using var cipher = new AesGcm(settings.StorageKey, TagSize);
            cipher.Decrypt(buffer.AsSpan(0, NonceSize), buffer.AsSpan(NonceSize + TagSize),
                buffer.AsSpan(NonceSize, TagSize), bytes, Encoding.UTF8.GetBytes(scope));
            return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new JsonException();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    internal string Fingerprint(string domain, string value) =>
        Convert.ToHexString(HMACSHA256.HashData(settings.StorageKey, Encoding.UTF8.GetBytes(domain + "\0" + value)));

    internal string Fingerprint<T>(string domain, T value) => Fingerprint(domain, JsonSerializer.Serialize(value, Json));
}
