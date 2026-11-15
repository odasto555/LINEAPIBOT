using LINEAPI.Protocol;
using LINEAPI.Transport;
using Thrift.Protocol.Entities;

namespace LINEAPI.Registration;

public sealed class RegistrationClient
{
    private const string Path = "/acct/pais/v1";
    private readonly LineRpcClient _rpc;

    internal RegistrationClient(LineRpcClient rpc) => _rpc = rpc;

    public async Task<string> OpenSessionAsync(LineRequestContext context, CancellationToken cancellationToken = default)
    {
        const string operation = "openSession";
        var args = new WireStruct((1, new WireStruct((1, new Dictionary<string, string>()))));
        return await CallAsync(operation, context, args, cancellationToken) is string { Length: > 0 } session
            ? session : throw new LineApiException(operation, LineFailure.Protocol);
    }

    public async Task<CountryInfo> GetCountryInfoAsync(
        LineRequestContext context, string sessionId, SimCard? sim = null, CancellationToken cancellationToken = default)
    {
        const string operation = "getCountryInfo";
        var fields = new WireStruct((1, Required(sessionId)));
        if (sim is not null)
            fields.TryAdd(2, new WireStruct((1, Country(sim.CountryCode)), (2, Required(sim.NetworkCode)), (3, Required(sim.CarrierName))));
        var result = await CallStructAsync(operation, context, fields, cancellationToken);
        return new CountryInfo(result.Require<string>(1, operation), result.Get(2) as bool?);
    }

    public async Task<RegistrationMethod> GetAllowedMethodAsync(
        LineRequestContext context, string sessionId, string countryCode, CancellationToken cancellationToken = default)
    {
        const string operation = "getAllowedRegistrationMethod";
        var result = await CallStructAsync(operation, context, new((1, Required(sessionId)), (2, Country(countryCode))), cancellationToken);
        return (RegistrationMethod)result.Require<int>(1, operation);
    }

    public async Task<PhoneVerificationInfo> GetPhoneVerificationAsync(
        LineRequestContext context, string sessionId, RegistrationDevice device, string phoneNumber, string countryCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        const string operation = "getPhoneVerifMethodForRegistration";
        var result = await CallStructAsync(operation, context, Wrap(
            (1, Required(sessionId)), (2, new WireStruct((1, Required(device.DeviceId)), (2, Required(device.Model)))),
            (3, Phone(phoneNumber, countryCode))), cancellationToken);
        return new PhoneVerificationInfo(result.Require<string>(2, operation), ReadMethods(result, operation));
    }

    public async Task<IReadOnlyList<PhoneVerificationMethod>> RequestCodeAsync(
        LineRequestContext context, string sessionId, string phoneNumber, string countryCode, PhoneVerificationMethod method,
        CancellationToken cancellationToken = default)
    {
        if (method is not (PhoneVerificationMethod.Sms or PhoneVerificationMethod.Voice))
            throw new ArgumentOutOfRangeException(nameof(method));

        const string operation = "requestToSendPhonePinCode";
        var result = await CallStructAsync(operation, context,
            Wrap((1, Required(sessionId)), (2, Phone(phoneNumber, countryCode)), (3, (int)method)), cancellationToken);
        return ReadMethods(result, operation);
    }

    public async Task<PhoneVerificationResult> VerifyCodeAsync(
        LineRequestContext context, string sessionId, string phoneNumber, string countryCode, string code,
        CancellationToken cancellationToken = default)
    {
        const string operation = "verifyPhonePinCode";
        if (string.IsNullOrWhiteSpace(code) || code.Length > 16 || !code.All(char.IsAsciiDigit))
            throw new ArgumentException("An OTP made of digits is required.", nameof(code));

        var result = await CallStructAsync(operation, context, Wrap(
            (1, Required(sessionId)), (2, Phone(phoneNumber, countryCode)), (3, code)), cancellationToken);
        return new PhoneVerificationResult(result.Get(1) as bool?, result.Get(2) as bool?, result.Get(3) as bool?);
    }

    public Task ValidateProfileAsync(
        LineRequestContext context, string sessionId, string displayName, CancellationToken cancellationToken = default)
        => CallAsync("validateProfile", context, new((1, Required(sessionId)), (2, Required(displayName))), cancellationToken, true);

    public async Task SetPasswordAsync(
        LineRequestContext context, string sessionId, string password, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        if (password.Length > 1024)
            throw new ArgumentOutOfRangeException(nameof(password));

        const string operation = "getPasswordHashingParametersForPwdReg";
        var result = await CallStructAsync(operation, context, Wrap((1, Required(sessionId))), cancellationToken);
        var parameters = result.Require<WireStruct>(1, operation);
        var scrypt = parameters.Require<WireStruct>(2, operation);
        var key = parameters.Require<string>(1, operation);
        var salt = scrypt.Require<string>(1, operation);
        var nrp = scrypt.Require<string>(2, operation);
        var length = scrypt.Get(3) switch
        {
            int n => n,
            long n when n is > 0 and <= int.MaxValue => (int)n,
            _ => throw new LineApiException(operation, LineFailure.Protocol)
        };

        var hash = await RegistrationPasswordHasher.HashAsync(password, key, salt, nrp, length, cancellationToken);
        await CallAsync("setHashedPassword", context, Wrap((1, sessionId), (2, hash)), cancellationToken, true);
    }

    public async Task<RegisteredAccount> RegisterAsync(
        LineRequestContext context, string sessionId, CancellationToken cancellationToken = default)
    {
        const string operation = "registerPrimaryUsingPhoneWithTokenV3";
        var result = await CallStructAsync(operation, context, new((1, Required(sessionId))), cancellationToken);
        var tokens = result.Require<WireStruct>(2, operation);
        return new RegisteredAccount(result.Require<string>(3, operation), result.Get(1) as string,
            tokens.Require<string>(1, operation), tokens.Get(2) as string);
    }

    private Task<object?> CallAsync(string operation, LineRequestContext context, WireStruct args, CancellationToken ct, bool empty = false)
    {
        ArgumentNullException.ThrowIfNull(context);
        return _rpc.CallAsync(Path, operation, context.RequireAnonymous(), args, ct, voidResult: empty);
    }

    private async Task<WireStruct> CallStructAsync(string operation, LineRequestContext context, WireStruct args, CancellationToken ct)
        => WireStruct.From(await CallAsync(operation, context, args, ct), operation);

    private static IReadOnlyList<PhoneVerificationMethod> ReadMethods(WireStruct result, string operation)
    {
        var list = result.Require<WireCollection>(1, operation);
        if (list.Type != TType.List || list.ElementType != TType.I32)
            throw new LineApiException(operation, LineFailure.Protocol);
        return Array.AsReadOnly(list.Items.Cast<int>().Select(x => (PhoneVerificationMethod)x).ToArray());
    }

    private static WireStruct Wrap(params (short Id, object Value)[] fields) => new((1, new WireStruct(fields)));

    private static WireStruct Phone(string number, string country)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(number);
        if (number.Length > 32 || number.Any(c => !char.IsAsciiDigit(c) && c is not ('+' or ' ' or '-' or '(' or ')')) ||
            !number.Any(char.IsAsciiDigit))
            throw new ArgumentException("Invalid phone number.", nameof(number));
        return new WireStruct((1, number), (2, Country(country)));
    }

    private static string Country(string value)
    {
        if (value is not { Length: 2 } || !value.All(char.IsAsciiLetter))
            throw new ArgumentException("A two-letter country code is required.", nameof(value));
        return value.ToUpperInvariant();
    }

    private static string Required(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 1024)
            throw new ArgumentOutOfRangeException(nameof(value));
        return value;
    }
}
