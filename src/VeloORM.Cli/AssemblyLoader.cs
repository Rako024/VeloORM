using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

namespace VeloORM.Cli;

/// <summary>
/// Loads a user assembly (and its dependencies) into an isolated context. Unlike a bare
/// <c>LoadFromAssemblyPath</c>, this:
/// <list type="bullet">
///   <item>resolves NuGet/project dependencies through the target's <c>.deps.json</c> via
///   <see cref="AssemblyDependencyResolver"/>, and</item>
///   <item>probes the ASP.NET Core (and other) shared frameworks so contexts defined in
///   <c>Microsoft.NET.Sdk.Web</c> projects load without being extracted to a class library —
///   <c>Microsoft.AspNetCore.*</c> assemblies live in the shared framework, not the app's bin.</item>
/// </list>
/// This mirrors how <c>dotnet-ef</c> is able to load Web SDK apps.
/// </summary>
public sealed class UserAssemblyLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly string _appDirectory;
    private readonly IReadOnlyList<string> _sharedFrameworkDirs;

    public UserAssemblyLoadContext(string mainAssemblyPath)
        : base(name: "VeloOrmUserContext", isCollectible: false)
    {
        var fullPath = Path.GetFullPath(mainAssemblyPath);
        _resolver = new AssemblyDependencyResolver(fullPath);
        _appDirectory = Path.GetDirectoryName(fullPath)!;
        _sharedFrameworkDirs = ResolveSharedFrameworkDirs(fullPath);
    }

    /// <summary>Loads the main user assembly.</summary>
    public Assembly LoadMain(string mainAssemblyPath) =>
        LoadFromAssemblyPath(Path.GetFullPath(mainAssemblyPath));

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // 0. Share host/contract assemblies already loaded by the CLI (VeloORM.*, Npgsql, BCL). Loading
        // a private copy here would give types a *different* identity, so e.g. the user's
        // `AppDbContext : VeloDbContext` would not appear assignable to the CLI's VeloDbContext.
        var fromDefault = Default.Assemblies.FirstOrDefault(a => a.GetName().Name == assemblyName.Name);
        if (fromDefault is not null)
            return fromDefault;

        // 1. deps.json-resolved dependencies (NuGet packages, project references).
        var resolved = _resolver.ResolveAssemblyToPath(assemblyName);
        if (resolved is not null)
            return LoadFromAssemblyPath(resolved);

        // 2. Alongside the app (rare, but cheap to check).
        if (assemblyName.Name is { } name)
        {
            var beside = Path.Combine(_appDirectory, name + ".dll");
            if (File.Exists(beside))
                return LoadFromAssemblyPath(beside);

            // 3. Shared frameworks (Microsoft.AspNetCore.App, Microsoft.NETCore.App, ...).
            foreach (var dir in _sharedFrameworkDirs)
            {
                var candidate = Path.Combine(dir, name + ".dll");
                if (File.Exists(candidate))
                    return LoadFromAssemblyPath(candidate);
            }
        }

        // Fall through to the default context (base runtime assemblies already loaded there).
        return null;
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is not null ? LoadUnmanagedDllFromPath(path) : nint.Zero;
    }

    /// <summary>
    /// Reads the app's <c>.runtimeconfig.json</c> to learn which shared frameworks it targets, then
    /// maps each to a concrete shared-framework directory reported by <c>dotnet --list-runtimes</c>,
    /// choosing the newest installed version with a matching major (or newest overall as a fallback).
    /// </summary>
    private static IReadOnlyList<string> ResolveSharedFrameworkDirs(string mainAssemblyPath)
    {
        var installed = ListInstalledRuntimes(); // framework name -> [(version, path)]
        if (installed.Count == 0)
            return Array.Empty<string>();

        var requested = ReadRequestedFrameworks(mainAssemblyPath); // framework name -> requested version (may be empty)

        // If runtimeconfig gave us nothing, fall back to probing ASP.NET Core + base at newest.
        if (requested.Count == 0)
            requested["Microsoft.AspNetCore.App"] = "";

        var dirs = new List<string>();
        foreach (var (name, version) in requested)
        {
            if (!installed.TryGetValue(name, out var candidates) || candidates.Count == 0)
                continue;
            var chosen = ChooseVersion(candidates, version);
            if (chosen is not null)
                dirs.Add(chosen);
        }
        return dirs;
    }

    private static string? ChooseVersion(List<(Version Version, string Path)> candidates, string requestedVersion)
    {
        Version? want = null;
        if (!string.IsNullOrEmpty(requestedVersion))
            Version.TryParse(StripPrerelease(requestedVersion), out want);

        IEnumerable<(Version Version, string Path)> ordered = candidates.OrderByDescending(c => c.Version);
        if (want is not null)
        {
            // Prefer the newest installed version with the same major that is >= requested.
            var match = ordered.FirstOrDefault(c => c.Version.Major == want.Major && c.Version >= want);
            if (match.Path is not null)
                return match.Path;
        }
        return ordered.First().Path;
    }

    private static Dictionary<string, string> ReadRequestedFrameworks(string mainAssemblyPath)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var runtimeConfig = Path.ChangeExtension(mainAssemblyPath, ".runtimeconfig.json");
        if (!File.Exists(runtimeConfig))
            return result;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(runtimeConfig));
            if (!doc.RootElement.TryGetProperty("runtimeOptions", out var options))
                return result;

            void Add(JsonElement fw)
            {
                if (fw.TryGetProperty("name", out var n) && n.GetString() is { } fwName)
                    result[fwName] = fw.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";
            }

            if (options.TryGetProperty("framework", out var single) && single.ValueKind == JsonValueKind.Object)
                Add(single);
            if (options.TryGetProperty("frameworks", out var many) && many.ValueKind == JsonValueKind.Array)
                foreach (var fw in many.EnumerateArray())
                    Add(fw);
        }
        catch (JsonException)
        {
            // Malformed runtimeconfig: fall back to probing defaults.
        }
        return result;
    }

    private static Dictionary<string, List<(Version Version, string Path)>> ListInstalledRuntimes()
    {
        var map = new Dictionary<string, List<(Version, string)>>(StringComparer.OrdinalIgnoreCase);
        string output;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = "--list-runtimes",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null)
                return map;
            output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
        }
        catch
        {
            return map;
        }

        // Each line: "Microsoft.AspNetCore.App 8.0.11 [C:\Program Files\dotnet\shared\Microsoft.AspNetCore.App]"
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var open = line.IndexOf('[');
            var close = line.LastIndexOf(']');
            if (open < 0 || close <= open)
                continue;

            var head = line[..open].Trim();
            var firstSpace = head.IndexOf(' ');
            if (firstSpace <= 0)
                continue;

            var name = head[..firstSpace];
            var versionText = StripPrerelease(head[(firstSpace + 1)..].Trim());
            if (!Version.TryParse(versionText, out var version))
                continue;

            var basePath = line[(open + 1)..close].Trim();
            var versionDir = Path.Combine(basePath, head[(firstSpace + 1)..].Trim());
            if (!Directory.Exists(versionDir))
                continue;

            if (!map.TryGetValue(name, out var list))
                map[name] = list = new List<(Version, string)>();
            list.Add((version, versionDir));
        }
        return map;
    }

    private static string StripPrerelease(string version)
    {
        var dash = version.IndexOf('-');
        return dash < 0 ? version : version[..dash];
    }
}
