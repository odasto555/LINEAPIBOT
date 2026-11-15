using MongoDB.Bson.Serialization.Attributes;

namespace LineJobs.Storage;

internal sealed record UserDocument(
    [property: BsonId] string Id, string NormalizedUsername, string PasswordHash, string Role, DateTime CreatedAt);

internal sealed record SessionDocument([property: BsonId] string TokenHash, string UserId, DateTime ExpiresAt);
