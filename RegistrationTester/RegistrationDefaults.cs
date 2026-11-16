using System.Security.Cryptography;

namespace RegistrationTester;

internal sealed record RegistrationDefaults(string Password, string DeviceId, string DeviceModel)
{
    private const string Uppercase = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string Lowercase = "abcdefghijklmnopqrstuvwxyz";
    private const string Digits = "0123456789";
    private const string Alphanumeric = Uppercase + Lowercase + Digits;

    private static readonly string[] Models =
    [
        "SM-S911B", "SM-S916B", "SM-S918B", "SM-S921B", "SM-S926B", "SM-S928B",
        "SM-A346B", "SM-A546B", "SM-A736B", "SM-A146B", "SM-A246B",
        "Pixel 7", "Pixel 7 Pro", "Pixel 8", "Pixel 8 Pro", "Pixel 8a",
        "23078PND5G", "23090RA98C", "23116PN5BC", "2312DRA50C", "2312CRAD4C",
        "24031PN0DC", "24053PY09C", "24076PND5G", "2407FPN8EG",
        "RMX3890", "RMX3900", "RMX3999", "RMX3991", "RMX3989",
        "V2250", "V2307A", "V2309A", "V2330A", "V2334A",
        "NX721J", "NX729J", "NX731J", "NX739J"
    ];

    internal static RegistrationDefaults Generate()
    {
        var password = RandomNumberGenerator.GetString(Alphanumeric, 12).ToCharArray();
        password[0] = RandomNumberGenerator.GetString(Uppercase, 1)[0];
        password[1] = RandomNumberGenerator.GetString(Lowercase, 1)[0];
        password[2] = RandomNumberGenerator.GetString(Digits, 1)[0];
        RandomNumberGenerator.Shuffle(password.AsSpan());

        return new RegistrationDefaults(new string(password),
            Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)),
            Models[RandomNumberGenerator.GetInt32(Models.Length)]);
    }
}

