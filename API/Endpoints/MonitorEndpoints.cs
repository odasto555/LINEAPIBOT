using LineJobs.Security;
using LineJobs.Storage;

namespace LineJobs.Endpoints;

internal static class MonitorEndpoints
{
    internal static void MapMonitor(this RouteGroupBuilder api) =>
        api.MapGet("/admin/jobs", ListAsync).RequireAuthorization(policy => policy.RequireRole(UserSessions.AdminRole));

    private static async Task<IResult> ListAsync(bool? active, int? limit, JobStore jobs, CancellationToken ct)
    {
        if (limit is < 1 or > 100)
            return JobEndpoints.Invalid("limit", "Use a limit from 1 to 100.");

        return Results.Ok(await jobs.MonitorAsync(active, limit ?? 50, ct));
    }
}

