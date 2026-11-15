using System.Security.Cryptography;
using System.Text;
using LineJobs.Configuration;
using LineJobs.Storage;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using MongoDB.Driver;

namespace LineJobs.Security;

internal sealed record SignedInSession(string AccessToken, string TokenType, DateTime ExpiresAt, string Username, string Role);

internal sealed class UserSessions(Database database, ApiSettings settings, IPasswordHasher<UserDocument> passwords)
{
    internal const string AdminRole = "admin";
    internal const string MemberRole = "member";

    private static readonly UserDocument DummyUser = new("", "", "", MemberRole, DateTime.UnixEpoch);
    private readonly string _dummyPasswordHash = passwords.HashPassword(DummyUser, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));

    internal static bool ValidUsername(string? username) =>
        username is { Length: >= 1 and <= 64 } && username.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    internal static bool ValidNewPassword(string? password) =>
        password is { Length: >= 12 and <= 128 } && !string.IsNullOrWhiteSpace(password);

    internal async Task BootstrapAsync(CancellationToken ct)
    {
        if (await database.Users.Find(_ => true).AnyAsync(ct))
            return;

        var username = settings.BootstrapUsername;
        var password = settings.BootstrapPassword;
        if (!ValidUsername(username) || !ValidNewPassword(password))
        {
            throw new InvalidOperationException(
                "Set Auth__BootstrapUsername and Auth__BootstrapPassword (12 to 128 characters) to create the first admin.");
        }

        var admin = NewUser(username!, password!, AdminRole);
        await database.WithAdmissionLockAsync(async (session, token) =>
        {
            if (await database.Users.Find(session, _ => true).AnyAsync(token))
                return false;
            await database.Users.InsertOneAsync(session, admin, cancellationToken: token);
            return true;
        }, ct);
    }

    internal async Task<bool> CreateMemberAsync(string username, string password, CancellationToken ct)
    {
        try
        {
            await database.Users.InsertOneAsync(NewUser(username, password, MemberRole), cancellationToken: ct);
            return true;
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
    }

    internal async Task<SignedInSession?> LoginAsync(string username, string password, CancellationToken ct)
    {
        var normalized = username.ToUpperInvariant();
        var user = await database.Users.Find(x => x.NormalizedUsername == normalized).FirstOrDefaultAsync(ct);
        var account = user ?? DummyUser;
        var verdict = passwords.VerifyHashedPassword(account, user?.PasswordHash ?? _dummyPasswordHash, password);
        if (user is null || verdict == PasswordVerificationResult.Failed)
            return null;

        if (verdict == PasswordVerificationResult.SuccessRehashNeeded)
        {
            var replacement = passwords.HashPassword(user, password);
            await database.Users.UpdateOneAsync(x => x.Id == user.Id && x.PasswordHash == user.PasswordHash,
                Builders<UserDocument>.Update.Set(x => x.PasswordHash, replacement), cancellationToken: ct);
        }

        var accessToken = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var expiration = DateTime.UtcNow.AddHours(settings.TokenHours);
        await database.Sessions.InsertOneAsync(new SessionDocument(HashToken(accessToken), user.Id, expiration), cancellationToken: ct);
        return new SignedInSession(accessToken, "Bearer", expiration, user.Id, user.Role);
    }

    internal async Task<UserDocument?> FindAuthenticatedUserAsync(string tokenHash, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var session = await database.Sessions.Find(x => x.TokenHash == tokenHash && x.ExpiresAt > now).FirstOrDefaultAsync(ct);
        return session is null ? null : await database.Users.Find(x => x.Id == session.UserId).FirstOrDefaultAsync(ct);
    }

    internal Task LogoutAsync(string userId, string tokenHash, CancellationToken ct) =>
        database.Sessions.DeleteOneAsync(x => x.TokenHash == tokenHash && x.UserId == userId, ct);

    internal static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(token)));

    private UserDocument NewUser(string username, string password, string role)
    {
        var user = new UserDocument(username, username.ToUpperInvariant(), "", role, DateTime.UtcNow);
        return user with { PasswordHash = passwords.HashPassword(user, password) };
    }
}
