using System.Reflection;

namespace EnableBankingUploader.Cli.Web;

internal static class AppVersion
{
    private const string DevVersion = "0.0.0-dev";
    private const string RepoUrl = "https://github.com/magnusakselvoll/enable-banking-firefly-iii-uploader";

    public static readonly bool IsRelease;
    public static readonly string Display;
    public static readonly string? ReleaseUrl;

    static AppVersion()
    {
        var raw = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        (IsRelease, Display, ReleaseUrl) = Parse(raw);
    }

    internal static (bool IsRelease, string Display, string? ReleaseUrl) Parse(string? raw)
    {
        var version = raw?.Split('+', 2)[0];
        if (string.IsNullOrEmpty(version) || version == DevVersion)
            return (false, "dev", null);

        return (true, $"v{version}", $"{RepoUrl}/releases/tag/v{version}");
    }
}
