using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using RM500U.Web.Models;

namespace RM500U.Web.Services;

public interface IAtTransport
{
    Task<AtCommandResult> ExecuteAsync(string port, int baudRate, string command, TimeSpan timeout, CancellationToken cancellationToken);
    Task<AtCommandResult> ExecuteInteractiveAsync(
        string port,
        int baudRate,
        string command,
        string payload,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

public sealed partial class LinuxAtTransport(CommandRunner runner, OperationLog log) : IAtTransport
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _serialGates = new(StringComparer.Ordinal);

    public async Task<AtCommandResult> ExecuteAsync(
        string port,
        int baudRate,
        string command,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var serialGate = GetSerialGate(port);
        await serialGate.WaitAsync(cancellationToken);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await PreparePortAsync(port, baudRate, cancellationToken);
            await using var stream = OpenPort(port);
            await WriteAsync(stream, command + "\r", cancellationToken);
            var (raw, timedOut) = await ReadUntilAsync(stream, IsTerminalResponse, timeout, cancellationToken);
            var safeCommand = RedactCommand(command);
            raw = RedactRaw(raw, command, safeCommand);
            var result = new AtCommandResult(safeCommand, IsSuccessful(raw), timedOut, raw.Trim(), stopwatch.ElapsedMilliseconds);
            LogResult(result);
            return result;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log.Add("error", "at", $"Unable to access {port}", exception.Message);
            return new AtCommandResult(command, false, false, exception.Message, stopwatch.ElapsedMilliseconds);
        }
        finally
        {
            serialGate.Release();
        }
    }

    public async Task<AtCommandResult> ExecuteInteractiveAsync(
        string port,
        int baudRate,
        string command,
        string payload,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var serialGate = GetSerialGate(port);
        await serialGate.WaitAsync(cancellationToken);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await PreparePortAsync(port, baudRate, cancellationToken);
            await using var stream = OpenPort(port);
            await WriteAsync(stream, command + "\r", cancellationToken);
            var (promptResponse, promptTimedOut) = await ReadUntilAsync(stream, value => value.Contains('>'), TimeSpan.FromSeconds(5), cancellationToken);
            if (promptTimedOut || !promptResponse.Contains('>'))
            {
                var failed = new AtCommandResult(command, false, true, promptResponse.Trim(), stopwatch.ElapsedMilliseconds);
                LogResult(failed);
                return failed;
            }

            var bytes = Encoding.UTF8.GetBytes(payload);
            await stream.WriteAsync(bytes, cancellationToken);
            await stream.WriteAsync(new byte[] { 0x1A }, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            var (raw, timedOut) = await ReadUntilAsync(stream, IsTerminalResponse, timeout, cancellationToken);
            raw = promptResponse + raw;
            var safeCommand = RedactCommand(command);
            raw = RedactRaw(raw, command, safeCommand);
            var result = new AtCommandResult(safeCommand, IsSuccessful(raw), timedOut, raw.Trim(), stopwatch.ElapsedMilliseconds);
            LogResult(result);
            return result;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log.Add("error", "at", $"Unable to access {port}", exception.Message);
            return new AtCommandResult(command, false, false, exception.Message, stopwatch.ElapsedMilliseconds);
        }
        finally
        {
            serialGate.Release();
        }
    }

    private SemaphoreSlim GetSerialGate(string port) =>
        _serialGates.GetOrAdd(Path.GetFullPath(port), static _ => new SemaphoreSlim(1, 1));

    private async Task PreparePortAsync(string port, int baudRate, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(
            "stty",
            ["-F", port, baudRate.ToString(), "cs8", "-cstopb", "-parenb", "raw", "-echo", "-ixon", "-ixoff", "min", "0", "time", "10"],
            TimeSpan.FromSeconds(3),
            cancellationToken);

        if (!result.Success)
            throw new IOException($"stty failed for {port}: {result.StandardError.Trim()}");
    }

    private static FileStream OpenPort(string port) => new(
        port,
        FileMode.Open,
        FileAccess.ReadWrite,
        FileShare.ReadWrite,
        4096,
        FileOptions.Asynchronous);

    private static async Task WriteAsync(FileStream stream, string value, CancellationToken cancellationToken)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task<(string Raw, bool TimedOut)> ReadUntilAsync(
        FileStream stream,
        Func<string, bool> completion,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        var buffer = new byte[1024];
        var response = new StringBuilder();

        try
        {
            while (!completion(response.ToString()))
            {
                var read = await stream.ReadAsync(buffer, timeoutCts.Token);
                if (read == 0)
                    continue;
                response.Append(Encoding.UTF8.GetString(buffer, 0, read));
            }

            return (response.ToString(), false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (response.ToString(), true);
        }
    }

    private static bool IsSuccessful(string raw) =>
        OkRegex().IsMatch(raw) && !ErrorRegex().IsMatch(raw);

    private static bool IsTerminalResponse(string raw) => OkRegex().IsMatch(raw) || ErrorRegex().IsMatch(raw);

    private void LogResult(AtCommandResult result)
    {
        var level = result.Success ? "info" : result.TimedOut ? "warning" : "error";
        log.Add(level, "at", $"{result.Command} ({result.ElapsedMilliseconds} ms)", result.Raw);
    }

    private static string RedactCommand(string command) =>
        command.StartsWith("AT+QICSGP=", StringComparison.OrdinalIgnoreCase)
            ? QicsgpSecretRegex().Replace(command, "$1\"***\",\"***\"$2")
            : command;

    private static string RedactRaw(string raw, string command, string safeCommand) =>
        command == safeCommand ? raw : raw.Replace(command, safeCommand, StringComparison.Ordinal);

    [GeneratedRegex(@"(?:^|\r?\n)OK(?:\r?\n|$)", RegexOptions.IgnoreCase)]
    private static partial Regex OkRegex();

    [GeneratedRegex(@"(?:^|\r?\n)(?:ERROR|\+CM[ES] ERROR:.*)(?:\r?\n|$)", RegexOptions.IgnoreCase)]
    private static partial Regex ErrorRegex();

    [GeneratedRegex("^(AT\\+QICSGP=\\d+,\\d+,\"[^\"]*\",)\"[^\"]*\",\"[^\"]*\"(,\\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex QicsgpSecretRegex();
}
