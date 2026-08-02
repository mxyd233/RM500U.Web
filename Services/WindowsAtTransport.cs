using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Ports;
using System.Text;
using System.Text.RegularExpressions;
using RM500U.Web.Models;

namespace RM500U.Web.Services;

/// <summary>
/// AT transport for the COM ports exposed by Windows. Each COM port is
/// serialized independently because the modem can have several AT-capable
/// interfaces but a single interface must never receive interleaved commands.
/// </summary>
public sealed partial class WindowsAtTransport(OperationLog log) : IAtTransport
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _serialGates = new(StringComparer.OrdinalIgnoreCase);

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
        var safeCommand = RedactCommand(command);
        try
        {
            using var serial = OpenPort(port, baudRate);
            serial.DiscardInBuffer();
            serial.DiscardOutBuffer();
            await WriteAsync(serial.BaseStream, command + "\r", cancellationToken);
            var (raw, timedOut) = await ReadUntilAsync(serial.BaseStream, IsTerminalResponse, timeout, cancellationToken);
            raw = RedactRaw(raw, command, safeCommand);
            var result = new AtCommandResult(safeCommand, IsSuccessful(raw), timedOut, raw.Trim(), stopwatch.ElapsedMilliseconds);
            LogResult(result);
            return result;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or TimeoutException)
        {
            log.Add("error", "at", $"Unable to access {port}", exception.Message);
            return new AtCommandResult(safeCommand, false, false, exception.Message, stopwatch.ElapsedMilliseconds);
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
        var safeCommand = RedactCommand(command);
        try
        {
            using var serial = OpenPort(port, baudRate);
            serial.DiscardInBuffer();
            serial.DiscardOutBuffer();
            await WriteAsync(serial.BaseStream, command + "\r", cancellationToken);
            var (promptResponse, promptTimedOut) = await ReadUntilAsync(
                serial.BaseStream,
                value => value.Contains('>'),
                TimeSpan.FromSeconds(5),
                cancellationToken);
            if (promptTimedOut || !promptResponse.Contains('>'))
            {
                var failedRaw = RedactRaw(promptResponse, command, safeCommand);
                var failed = new AtCommandResult(safeCommand, false, true, failedRaw.Trim(), stopwatch.ElapsedMilliseconds);
                LogResult(failed);
                return failed;
            }

            await serial.BaseStream.WriteAsync(Encoding.UTF8.GetBytes(payload), cancellationToken);
            await serial.BaseStream.WriteAsync(new byte[] { 0x1A }, cancellationToken);
            await serial.BaseStream.FlushAsync(cancellationToken);
            var (raw, timedOut) = await ReadUntilAsync(serial.BaseStream, IsTerminalResponse, timeout, cancellationToken);
            raw = RedactRaw(promptResponse + raw, command, safeCommand);
            var result = new AtCommandResult(safeCommand, IsSuccessful(raw), timedOut, raw.Trim(), stopwatch.ElapsedMilliseconds);
            LogResult(result);
            return result;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or TimeoutException)
        {
            log.Add("error", "at", $"Unable to access {port}", exception.Message);
            return new AtCommandResult(safeCommand, false, false, exception.Message, stopwatch.ElapsedMilliseconds);
        }
        finally
        {
            serialGate.Release();
        }
    }

    private SemaphoreSlim GetSerialGate(string port) =>
        _serialGates.GetOrAdd(port.Trim(), static _ => new SemaphoreSlim(1, 1));

    private static SerialPort OpenPort(string port, int baudRate)
    {
        var serial = new SerialPort
        {
            PortName = port,
            BaudRate = baudRate,
            Parity = Parity.None,
            DataBits = 8,
            StopBits = StopBits.One,
            Handshake = Handshake.None,
            DtrEnable = false,
            RtsEnable = false,
            ReadTimeout = 1000,
            WriteTimeout = 3000,
            Encoding = Encoding.UTF8,
            NewLine = "\r\n"
        };
        serial.Open();
        return serial;
    }

    private static async Task WriteAsync(Stream stream, string value, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes(value), cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task<(string Raw, bool TimedOut)> ReadUntilAsync(
        Stream stream,
        Func<string, bool> completion,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[1024];
        var response = new StringBuilder();
        var stopwatch = Stopwatch.StartNew();
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        while (!completion(response.ToString()))
        {
            var remaining = timeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
                return (response.ToString(), true);

            var readTask = stream.ReadAsync(buffer, 0, buffer.Length, readCts.Token);
            var timeoutTask = Task.Delay(remaining, cancellationToken);
            var completed = await Task.WhenAny(readTask, timeoutTask);
            if (completed != readTask)
            {
                cancellationToken.ThrowIfCancellationRequested();
                readCts.Cancel();
                ObserveReadTask(readTask);
                return (response.ToString(), true);
            }

            var read = await readTask;
            if (read > 0)
                response.Append(Encoding.UTF8.GetString(buffer, 0, read));
        }

        return (response.ToString(), false);
    }

    private static void ObserveReadTask(Task<int> readTask) =>
        _ = readTask.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private static bool IsSuccessful(string raw) =>
        OkRegex().IsMatch(raw) && !ErrorRegex().IsMatch(raw);

    private static bool IsTerminalResponse(string raw) =>
        OkRegex().IsMatch(raw) || ErrorRegex().IsMatch(raw);

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
