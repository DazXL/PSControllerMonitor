using System.Diagnostics;
using System.Reflection;

namespace PSControllerMonitor;

internal static class AppMetadata
{
    internal const string ApplicationName = "PS Controller Monitor";
    internal const string RepositoryUrl = "https://github.com/DazXL/PSControllerMonitor";

    internal static string GetDisplayVersion()
    {
        Assembly assembly = typeof(AppMetadata).Assembly;
        string? informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            int plusIndex = informationalVersion.IndexOf('+');
            return plusIndex >= 0
                ? informationalVersion[..plusIndex]
                : informationalVersion;
        }

        Version? assemblyVersion = assembly.GetName().Version;
        if (assemblyVersion != null)
        {
            return assemblyVersion.Build >= 0
                ? $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}"
                : $"{assemblyVersion.Major}.{assemblyVersion.Minor}";
        }

        return Application.ProductVersion;
    }

    internal static void OpenRepository()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = RepositoryUrl,
            UseShellExecute = true
        });
    }
}