using System.Reflection;
using System.Reflection.Emit;
using GrimDawnCompanion.App;

internal static class AppVersionRegressionTests
{
    public static void Run(Action<bool, string> require)
    {
        static Assembly Sample(string? informational, Version numeric)
        {
            var assembly = AssemblyBuilder.DefineDynamicAssembly(
                new AssemblyName(Guid.NewGuid().ToString("N")) { Version = numeric }, AssemblyBuilderAccess.Run);
            if (informational is not null)
                assembly.SetCustomAttribute(new CustomAttributeBuilder(
                    typeof(AssemblyInformationalVersionAttribute).GetConstructor([typeof(string)])!, [informational]));
            return assembly;
        }
        require(AppVersion.ForAssembly(Sample("3.7.12+abcdef", new(1, 0, 0, 0))) == "Version 3.7.12",
            "sidebar version follows build metadata and omits source hash");
        require(AppVersion.ForAssembly(Sample("4.0.0-beta.2+abcdef", new(1, 0, 0, 0))) == "Version 4.0.0-beta.2",
            "sidebar version preserves prerelease labels");
        require(AppVersion.ForAssembly(Sample(null, new(5, 6, 7, 0))) == "Version 5.6.7" &&
                AppVersion.ForAssembly(Sample("", new(5, 6, 7, 8))) == "Version 5.6.7.8",
            "sidebar version falls back to the assembly version when metadata is absent");
    }
}
