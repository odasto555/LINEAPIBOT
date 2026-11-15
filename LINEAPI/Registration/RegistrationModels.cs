namespace LINEAPI.Registration;

public sealed record SimCard(string CountryCode, string NetworkCode, string CarrierName);
public sealed record RegistrationDevice(string DeviceId, string Model);
public sealed record CountryInfo(string CountryCode, bool? InEea);
public sealed record PhoneVerificationInfo(string FormattedNumber, IReadOnlyList<PhoneVerificationMethod> AvailableMethods);
public sealed record PhoneVerificationResult(bool? AccountExists, bool? IsSameDevice, bool? AllowedToRegister);

public enum RegistrationMethod
{
    Unknown = 0,
    Phone = 1,
    ExternalAccount = 2
}

public enum PhoneVerificationMethod
{
    Unknown = 0,
    Sms = 1,
    Voice = 2,
    SmsPull = 3
}

public sealed class RegisteredAccount
{
    internal RegisteredAccount(string userId, string? authToken, string accessToken, string? refreshToken)
    {
        UserId = userId;
        AuthToken = authToken;
        AccessToken = accessToken;
        RefreshToken = refreshToken;
    }

    public string UserId { get; }
    public string? AuthToken { get; }
    public string AccessToken { get; }
    public string? RefreshToken { get; }
}
