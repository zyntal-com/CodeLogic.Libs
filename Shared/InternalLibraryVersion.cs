// Auto-included into every CL.* library via Directory.Build.targets.
// Reads the current assembly's <InformationalVersion> (set by MSBuild from
// the csproj <Version>) so LibraryManifest.Version always tracks the
// build's actual NuGet version — no hand-edited duplicate strings to drift.

using System.Reflection;

namespace CL.Internal;

internal static class InternalLibraryVersion
{
    /// <summary>
    /// The current assembly's informational version (e.g. "5.0.142"), with any
    /// "+sha" build-metadata suffix stripped. Falls back to "0.0.0" only if
    /// the attribute is genuinely missing — which would mean the build pipeline
    /// is broken, not something a caller should be papering over.
    /// </summary>
    public static readonly string Current =
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion?.Split('+')[0]
        ?? "0.0.0";
}
