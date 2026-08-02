using System.Diagnostics;

namespace RM500U.Web.Services;

public sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut)
{
    public bool Success => ExitCode == 0 && !TimedOut;
}

public sealed class CommandRunner(OperationLog log)
{
    public async Task<CommandResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var argumentList = arguments.ToArray();
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in argumentList)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                return new CommandResult(-1, string.Empty, "Process did not start.", false);

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try { process.Kill(true); } catch { }
                return new CommandResult(-1, await stdoutTask, await stderrTask, true);
            }

            return new CommandResult(process.ExitCode, await stdoutTask, await stderrTask, false);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            log.Add("debug", "system", $"Command unavailable: {executable}", exception.Message);
            return new CommandResult(-1, string.Empty, exception.Message, false);
        }
    }

    public static bool Exists(string executable)
    {
        if (Path.IsPathRooted(executable))
            return File.Exists(executable);

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(directory => File.Exists(Path.Combine(directory, executable)));
    }
}
