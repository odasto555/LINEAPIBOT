namespace LineJobs.Configuration;

internal sealed record LineClientProfile(string UserAgent, string Application);

internal static class AndroidRandomizer
{
    private static readonly string[] AndroidVersions = ["11", "12", "13", "14", "15", "16"];

    internal static LineClientProfile Default(string lineVersion) => Create(lineVersion, "14");

    internal static LineClientProfile Sample(string lineVersion) =>
        Create(lineVersion, AndroidVersions[Random.Shared.Next(AndroidVersions.Length)]);

    internal static LineClientProfile WithLineVersion(LineClientProfile profile, string lineVersion) =>
        profile.Application.Split('\t') is ["ANDROID", _, "Android OS", var androidVersion]
            ? Create(lineVersion, androidVersion)
            : profile;

    private static LineClientProfile Create(string lineVersion, string androidVersion) =>
        new($"Line/{lineVersion}", $"ANDROID\t{lineVersion}\tAndroid OS\t{androidVersion}");
}

