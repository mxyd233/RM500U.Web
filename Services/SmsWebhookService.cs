using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RM500U.Web.Models;

namespace RM500U.Web.Services;

public sealed class SmsWebhookService(
    ConfigStore configStore,
    Rm500uService modem,
    RuntimeOptions runtime,
    OperationLog log) : BackgroundService
{
    private const int MaximumDelivered = 2000;
    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private readonly HttpClient _httpClient = new();
    private WebhookState? _state;

    private string StatePath => Path.Combine(runtime.DataDirectory, "sms-webhook-state.json");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await PollOnceAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            log.Add("error", "webhook", "SMS webhook worker stopped unexpectedly.", exception.Message);
        }
    }

    public async Task<SmsWebhookDeliveryState> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var config = await configStore.LoadAsync(cancellationToken);
        var state = await LoadStateAsync(cancellationToken);
        return new SmsWebhookDeliveryState(
            config.SmsWebhook.Enabled,
            config.SmsWebhook.Url,
            state.LastAttemptAt,
            state.LastSuccess,
            state.LastError,
            state.PendingCount);
    }

    public async Task<ActionResultModel> TestAsync(CancellationToken cancellationToken = default)
    {
        var config = await configStore.LoadAsync(cancellationToken);
        if (!config.SmsWebhook.Enabled || string.IsNullOrWhiteSpace(config.SmsWebhook.Url))
            return new ActionResultModel(false, "Enable the SMS webhook and set its URL before testing.");

        var testMessage = new
        {
            @event = "sms.test",
            source = "rm500u-web",
            sentAt = DateTimeOffset.UtcNow,
            message = new
            {
                direction = "incoming",
                peer = "+8613800138000",
                timestamp = DateTimeOffset.UtcNow,
                content = "RM500U webhook test",
                indices = Array.Empty<int>(),
                segmentCount = 1
            }
        };
        var result = await SendAsync(config.SmsWebhook, testMessage, "sms.test", cancellationToken);
        var state = await LoadStateAsync(cancellationToken);
        await SaveStateAsync(state with
        {
            LastAttemptAt = DateTimeOffset.UtcNow,
            LastSuccess = result.Success,
            LastError = result.Success ? null : result.Message
        }, cancellationToken);
        return result.Success
            ? new ActionResultModel(true, "Webhook test delivered.", result.Raw)
            : new ActionResultModel(false, result.Message, result.Raw);
    }

    public async Task PollOnceAsync(CancellationToken cancellationToken = default)
    {
        var config = await configStore.LoadAsync(cancellationToken);
        var webhook = config.SmsWebhook;
        if (!webhook.Enabled || string.IsNullOrWhiteSpace(webhook.Url))
        {
            var disabledState = await LoadStateAsync(cancellationToken);
            if (disabledState.BaselineEstablished)
            {
                disabledState = disabledState with { BaselineEstablished = false, PendingCount = 0 };
                await SaveStateAsync(disabledState, cancellationToken);
            }
            return;
        }

        SmsListResult list;
        try
        {
            list = await modem.ListSmsAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            log.Add("warning", "webhook", "SMS polling failed; webhook delivery will retry.", exception.Message);
            return;
        }

        var state = await LoadStateAsync(cancellationToken);
        var incoming = list.Conversations
            .SelectMany(conversation => conversation.Messages)
            .Where(message => message.Direction == SmsDirection.Incoming)
            .Where(message => !message.IsMultipart || message.Indices.Count >= message.SegmentCount)
            .ToArray();

        if (!state.BaselineEstablished)
        {
            var baseline = state with
            {
                BaselineEstablished = true,
                Delivered = state.Delivered.Union(incoming.Select(Fingerprint)).TakeLast(MaximumDelivered).ToArray(),
                PendingCount = 0,
                LastError = null
            };
            await SaveStateAsync(baseline, cancellationToken);
            log.Add("info", "webhook", "SMS webhook baseline established.", $"messages={incoming.Length}");
            return;
        }

        var delivered = state.Delivered.ToHashSet(StringComparer.Ordinal);
        foreach (var message in incoming)
        {
            var fingerprint = Fingerprint(message);
            if (delivered.Contains(fingerprint))
                continue;

            state = state with { PendingCount = state.PendingCount + 1 };
            await SaveStateAsync(state, cancellationToken);
            var payload = BuildPayload(message);
            var result = await SendAsync(webhook, payload, "sms.received", cancellationToken);
            state = state with
            {
                LastAttemptAt = DateTimeOffset.UtcNow,
                LastSuccess = result.Success,
                LastError = result.Success ? null : result.Message,
                PendingCount = Math.Max(0, state.PendingCount - 1),
                Delivered = result.Success
                    ? state.Delivered.Append(fingerprint).TakeLast(MaximumDelivered).ToArray()
                    : state.Delivered
            };
            await SaveStateAsync(state, cancellationToken);
            log.Add(result.Success ? "info" : "error", "webhook", result.Success ? "Incoming SMS forwarded to webhook." : "Incoming SMS webhook delivery failed.", result.Message);
        }
    }

    private async Task<SendResult> SendAsync(
        SmsWebhookConfig config,
        object payload,
        string eventName,
        CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var bytes = Encoding.UTF8.GetBytes(body);
        var signature = string.IsNullOrEmpty(config.Secret)
            ? null
            : "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(config.Secret), bytes)).ToLowerInvariant();
        Exception? lastException = null;
        HttpStatusCode? lastStatus = null;

        for (var attempt = 0; attempt <= config.MaxRetries; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, config.Url)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
                request.Headers.UserAgent.Add(new ProductInfoHeaderValue("RM500U-Web", "1.0"));
                request.Headers.Add("X-RM500U-Event", eventName);
                if (signature is not null)
                    request.Headers.Add("X-RM500U-Signature", signature);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(config.TimeoutSeconds));
                using var response = await _httpClient.SendAsync(request, timeout.Token);
                lastStatus = response.StatusCode;
                if ((int)response.StatusCode is >= 200 and < 300)
                    return new SendResult(true, $"HTTP {(int)response.StatusCode}", null);
                lastException = new HttpRequestException($"Webhook returned HTTP {(int)response.StatusCode}.");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                lastException = new TimeoutException("Webhook request timed out.");
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException)
            {
                lastException = exception;
            }

            if (attempt < config.MaxRetries)
                await Task.Delay(TimeSpan.FromMilliseconds(400 * Math.Pow(2, attempt)), cancellationToken);
        }

        var suffix = lastStatus is null ? string.Empty : $" (HTTP {(int)lastStatus.Value})";
        return new SendResult(false, (lastException?.Message ?? "Webhook request failed") + suffix, lastException?.ToString());
    }

    private async Task<WebhookState> LoadStateAsync(CancellationToken cancellationToken)
    {
        if (_state is not null)
            return _state;
        await _stateGate.WaitAsync(cancellationToken);
        try
        {
            if (_state is not null)
                return _state;
            if (File.Exists(StatePath))
            {
                await using var stream = File.OpenRead(StatePath);
                _state = await JsonSerializer.DeserializeAsync<WebhookState>(stream, cancellationToken: cancellationToken);
            }
            _state ??= new WebhookState();
            _state = _state with
            {
                Delivered = (_state.Delivered ?? []).TakeLast(MaximumDelivered).ToArray(),
                PendingCount = Math.Max(0, _state.PendingCount)
            };
            return _state;
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            log.Add("warning", "webhook", "Webhook state file could not be read; starting with an empty state.", exception.Message);
            _state = new WebhookState();
            return _state;
        }
        finally
        {
            _stateGate.Release();
        }
    }

    private async Task SaveStateAsync(WebhookState state, CancellationToken cancellationToken)
    {
        await _stateGate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(runtime.DataDirectory);
            var temporary = StatePath + ".tmp";
            await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, state, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporary, StatePath, true);
            _state = state;
        }
        finally
        {
            _stateGate.Release();
        }
    }

    private static object BuildPayload(SmsMessage message) => new
    {
        @event = "sms.received",
        source = "rm500u-web",
        sentAt = DateTimeOffset.UtcNow,
        message = new
        {
            direction = "incoming",
            peer = message.Peer,
            timestamp = message.Timestamp,
            content = message.Content,
            indices = message.Indices,
            segmentCount = message.SegmentCount,
            multipart = message.IsMultipart
        }
    };

    private static string Fingerprint(SmsMessage message)
    {
        var raw = $"{message.Direction}|{message.Peer}|{message.Timestamp:O}|{message.Content}|{string.Join(',', message.Indices)}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }

    private sealed record SendResult(bool Success, string Message, string? Raw);

    private sealed record WebhookState
    {
        public bool BaselineEstablished { get; init; }
        public IReadOnlyList<string> Delivered { get; init; } = [];
        public DateTimeOffset? LastAttemptAt { get; init; }
        public bool? LastSuccess { get; init; }
        public string? LastError { get; init; }
        public int PendingCount { get; init; }
    }
}
