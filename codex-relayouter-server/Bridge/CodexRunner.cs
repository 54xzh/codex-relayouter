// CodexRunner：以子进程方式运行 codex exec --json，并将 JSONL 输出逐行回调给上层。
using System.Text;
using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace codex_bridge_server.Bridge;

public sealed class CodexRunner
{
    private readonly IOptions<CodexOptions> _options;
    private readonly ILogger<CodexRunner> _logger;

    public CodexRunner(IOptions<CodexOptions> options, ILogger<CodexRunner> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task<int> RunAsync(
        CodexRunRequest request,
        Func<CodexOutputLine, Task> onLine,
        CancellationToken cancellationToken)
    {
        var options = _options.Value;

        var invocation = ResolveCodexInvocation(options.Executable);
        var startInfo = new ProcessStartInfo
        {
            FileName = invocation.FileName,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            CreateNoWindow = true,
        };

        foreach (var arg in invocation.PrefixArgs)
        {
            startInfo.ArgumentList.Add(arg);
        }

        startInfo.ArgumentList.Add("exec");

        startInfo.ArgumentList.Add("--json");

        if (options.SkipGitRepoCheck || request.SkipGitRepoCheck)
        {
            startInfo.ArgumentList.Add("--skip-git-repo-check");
        }

        if (!string.IsNullOrWhiteSpace(request.Model))
        {
            startInfo.ArgumentList.Add("--model");
            startInfo.ArgumentList.Add(request.Model);
        }

        if (!string.IsNullOrWhiteSpace(request.Sandbox))
        {
            startInfo.ArgumentList.Add("--sandbox");
            startInfo.ArgumentList.Add(request.Sandbox);
        }

        if (!string.IsNullOrWhiteSpace(request.SessionId))
        {
            startInfo.ArgumentList.Add("resume");
            startInfo.ArgumentList.Add(request.SessionId);
        }

        startInfo.ArgumentList.Add("-");

        if (!string.IsNullOrWhiteSpace(request.WorkingDirectory))
        {
            var workingDirectory = request.WorkingDirectory.Trim();
            if (!Directory.Exists(workingDirectory))
            {
                throw new InvalidOperationException($"工作区目录不存在或不可访问: {workingDirectory}");
            }

            startInfo.WorkingDirectory = workingDirectory;
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "启动 codex 进程失败：{Executable}", options.Executable);
            throw;
        }

        await process.StandardInput.WriteLineAsync(request.Prompt);
        process.StandardInput.Close();

        var readStdOut = ReadLinesAsync(process.StandardOutput, "stdout", onLine, cancellationToken);
        var readStdErr = ReadLinesAsync(process.StandardError, "stderr", onLine, cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        finally
        {
            await Task.WhenAll(readStdOut, readStdErr);
        }

        return process.ExitCode;
    }

    internal sealed record CodexInvocation(string FileName, IReadOnlyList<string> PrefixArgs, string DisplayName);

    internal static CodexInvocation ResolveCodexInvocation(string configuredExecutable)
    {
        var executable = string.IsNullOrWhiteSpace(configuredExecutable) ? "codex" : configuredExecutable.Trim();

        if (!OperatingSystem.IsWindows())
        {
            return new CodexInvocation(executable, Array.Empty<string>(), executable);
        }

        if (LooksPathLike(executable))
        {
            if (!File.Exists(executable))
            {
                throw new FileNotFoundException($"未找到 codex 可执行文件: {executable}");
            }

            return CreateWindowsInvocationForExistingFile(executable);
        }

        var baseName = Path.GetFileNameWithoutExtension(executable);
        foreach (var candidate in EnumerateWindowsCodexCandidates(baseName))
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            return CreateWindowsInvocationForExistingFile(candidate);
        }

        return new CodexInvocation(executable, Array.Empty<string>(), executable);
    }

    private static bool LooksPathLike(string executable)
    {
        if (Path.IsPathRooted(executable))
        {
            return true;
        }

        return executable.Contains(Path.DirectorySeparatorChar) || executable.Contains(Path.AltDirectorySeparatorChar);
    }

    private static IEnumerable<string> EnumerateWindowsCodexCandidates(string baseName)
    {
        var candidates = new List<string>();

        foreach (var dir in EnumerateWindowsSearchDirectories())
        {
            candidates.Add(Path.Combine(dir, baseName + ".exe"));
            candidates.Add(Path.Combine(dir, baseName + ".cmd"));
            candidates.Add(Path.Combine(dir, baseName + ".bat"));
            candidates.Add(Path.Combine(dir, baseName + ".ps1"));
        }

        return candidates;
    }

    private static IEnumerable<string> EnumerateWindowsSearchDirectories()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in EnumeratePathDirectories(EnvironmentVariableTarget.Process))
        {
            if (seen.Add(dir))
            {
                yield return dir;
            }
        }

        foreach (var dir in EnumeratePathDirectories(EnvironmentVariableTarget.User))
        {
            if (seen.Add(dir))
            {
                yield return dir;
            }
        }

        foreach (var dir in EnumeratePathDirectories(EnvironmentVariableTarget.Machine))
        {
            if (seen.Add(dir))
            {
                yield return dir;
            }
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            var roamingAppData = Environment.GetEnvironmentVariable("APPDATA")?.Trim();
            var npmDir = string.IsNullOrWhiteSpace(roamingAppData)
                ? Path.Combine(userProfile, "AppData", "Roaming", "npm")
                : Path.Combine(roamingAppData, "npm");
            if (seen.Add(npmDir))
            {
                yield return npmDir;
            }

            var cargoDir = Path.Combine(userProfile, ".cargo", "bin");
            if (seen.Add(cargoDir))
            {
                yield return cargoDir;
            }
        }

        var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA")?.Trim();
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            var windowsApps = Path.Combine(localAppData, "Microsoft", "WindowsApps");
            if (seen.Add(windowsApps))
            {
                yield return windowsApps;
            }
        }
    }

    private static IEnumerable<string> EnumeratePathDirectories(EnvironmentVariableTarget target)
    {
        var path = Environment.GetEnvironmentVariable("Path", target);
        if (string.IsNullOrWhiteSpace(path))
        {
            yield break;
        }

        foreach (var part in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.IsNullOrWhiteSpace(part))
            {
                continue;
            }

            yield return part;
        }
    }

    private static CodexInvocation CreateWindowsInvocationForExistingFile(string executablePath)
    {
        var ext = Path.GetExtension(executablePath);
        if (string.Equals(ext, ".cmd", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".bat", StringComparison.OrdinalIgnoreCase))
        {
            var systemDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var cmdExe = string.IsNullOrWhiteSpace(systemDir) ? "cmd.exe" : Path.Combine(systemDir, "cmd.exe");
            return new CodexInvocation(cmdExe, new[] { "/c", executablePath }, executablePath);
        }

        if (string.Equals(ext, ".ps1", StringComparison.OrdinalIgnoreCase))
        {
            var systemDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var powershellExe = string.IsNullOrWhiteSpace(systemDir) ? "powershell.exe" : Path.Combine(systemDir, "WindowsPowerShell", "v1.0", "powershell.exe");
            return new CodexInvocation(powershellExe, new[] { "-ExecutionPolicy", "Bypass", "-File", executablePath }, executablePath);
        }

        return new CodexInvocation(executablePath, Array.Empty<string>(), executablePath);
    }

    private static async Task ReadLinesAsync(
        StreamReader reader,
        string stream,
        Func<CodexOutputLine, Task> onLine,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await reader.ReadLineAsync();
            if (line is null)
            {
                return;
            }

            await onLine(new CodexOutputLine(stream, line));

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private void TryKill(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return;
            }

            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "取消时终止 codex 进程失败");
        }
    }
}
