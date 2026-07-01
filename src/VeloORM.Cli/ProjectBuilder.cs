using System.Diagnostics;

namespace VeloORM.Cli;

/// <summary>
/// Builds a target <c>.csproj</c> and resolves the produced assembly path, so the user can point the
/// CLI at a project (<c>--project</c>) instead of hunting down a compiled DLL (<c>--assembly</c>).
/// Uses the .NET SDK's <c>--getProperty</c> to read the evaluated <c>TargetPath</c> — no MSBuild or
/// Buildalyzer package dependency required.
/// </summary>
public static class ProjectBuilder
{
    /// <summary>Builds <paramref name="projectPath"/> (unless <paramref name="noBuild"/>) and returns the
    /// absolute path of the output assembly. Progress/build output is written to <paramref name="log"/>.</summary>
    public static string BuildAndResolveAssembly(string projectPath, string configuration, bool noBuild, TextWriter log)
    {
        var fullProject = Path.GetFullPath(projectPath);
        if (!File.Exists(fullProject))
            throw new FileNotFoundException($"Project file not found: {fullProject}", fullProject);

        // The SDK's --getProperty prints just the evaluated value on success. `dotnet build
        // --getProperty` builds and returns TargetPath in one step; for --no-build use `dotnet msbuild`
        // (which needs MSBuild-style -property:Configuration, not the `dotnet build` -c alias).
        int exit;
        string stdout, stderr;
        if (noBuild)
        {
            log.WriteLine($"Resolving output assembly of {Path.GetFileName(fullProject)} ({configuration})...");
            (exit, stdout, stderr) = Run("dotnet",
                $"msbuild \"{fullProject}\" -nologo -getProperty:TargetPath -property:Configuration={configuration}");
        }
        else
        {
            log.WriteLine($"Building {Path.GetFileName(fullProject)} ({configuration})...");
            (exit, stdout, stderr) = Run("dotnet",
                $"build \"{fullProject}\" -c {configuration} --nologo --getProperty:TargetPath");
        }

        // On success stdout is just the path; on failure it carries diagnostics — take the last
        // non-empty line and verify it is a real file.
        var targetPath = stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();

        if (exit != 0 || string.IsNullOrWhiteSpace(targetPath) || !File.Exists(targetPath))
            throw new InvalidOperationException(
                $"Could not build or resolve the output assembly of '{fullProject}' (exit {exit}). " +
                "Run 'dotnet build' manually to see the errors." +
                (string.IsNullOrWhiteSpace(stdout) ? "" : Environment.NewLine + stdout.Trim()) +
                (string.IsNullOrWhiteSpace(stderr) ? "" : Environment.NewLine + stderr.Trim()));

        return targetPath;
    }

    private static (int ExitCode, string StdOut, string StdErr) Run(string fileName, string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start '{fileName}'. Is the .NET SDK on PATH?");

        // Read both streams to avoid deadlock on full pipe buffers.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();

        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();
        return (process.ExitCode, stdout, stderr);
    }
}
