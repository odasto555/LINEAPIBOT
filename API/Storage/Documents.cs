using System.Text.Json;
using LineJobs.Jobs;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace LineJobs.Storage;

internal sealed class JobDocument
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [BsonId]
    public required string Id { get; init; }
    public required string Owner { get; init; }
    [BsonRepresentation(BsonType.String)]
    public JobKind Kind { get; init; }
    [BsonRepresentation(BsonType.String)]
    public JobState State { get; init; }
    public required string ResourceKey { get; init; }
    public bool Active { get; init; }
    public required string Stage { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public DateTime? OtpExpiresAt { get; init; }
    public DateTime? LeaseUntil { get; init; }
    public string? LeaseId { get; init; }
    public string? Payload { get; init; }
    public string? Result { get; init; }
    public string? Error { get; init; }
    [BsonIgnoreIfNull]
    public string? RequestId { get; init; }
    public required string Fingerprint { get; init; }

    internal JobStatus Status() => new(
        Guid.Parse(Id), Kind, State, Stage, new DateTimeOffset(CreatedAt), new DateTimeOffset(UpdatedAt),
        OtpExpiresAt is { } expires ? new DateTimeOffset(expires) : null,
        Result is null ? null : JsonSerializer.Deserialize<JobResult>(Result, Json),
        Error is null ? null : JsonSerializer.Deserialize<JobError>(Error, Json));

    internal static string Encode<T>(T value) => JsonSerializer.Serialize(value, Json);
}

internal sealed record AccountDocument([property: BsonId] string Id, string Owner, string Secret);
internal sealed record QueueControl([property: BsonId] string Id, long Version);
