using System.Reflection;

namespace GrimDawnCompanion.App;

public static class AppVersion
{
    public static string Display { get; } = ForAssembly(typeof(AppVersion).Assembly);

    public static string ForAssembly(Assembly assembly)
    {
        // The SDK generates this from the project's Version. Omit the optional
        // source revision suffix, but retain release labels such as "-beta.1".
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0];
        if (string.IsNullOrWhiteSpace(version))
        {
            var numeric = assembly.GetName().Version;
            version = numeric is null ? null : numeric.ToString(numeric.Revision > 0 ? 4 : 3);
        }
        return string.IsNullOrWhiteSpace(version) ? "Version unavailable" : $"Version {version}";
    }
}
