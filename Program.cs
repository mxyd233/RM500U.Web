using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using RM500U.Web.Models;
using RM500U.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

builder.Services.AddSingleton<RuntimeOptions>();
builder.Services.AddSingleton<OperationLog>();
builder.Services.AddSingleton<ConfigStore>();
builder.Services.AddSingleton<CommandRunner>();
builder.Services.AddSingleton<LinuxAtTransport>();
builder.Services.AddSingleton<WindowsAtTransport>();
builder.Services.AddSingleton<MockAtTransport>();
builder.Services.AddSingleton<LinuxDeviceScanner>();
builder.Services.AddSingleton<WindowsDeviceScanner>();
builder.Services.AddSingleton<AtGateway>();
builder.Services.AddSingleton<NetworkInterfaceService>();
builder.Services.AddSingleton<Rm500uService>();
builder.Services.AddSingleton<DialService>();
builder.Services.AddSingleton<SmsWebhookService>();
builder.Services.AddHostedService<AutoConnectService>();
builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<SmsWebhookService>());

var app = builder.Build();

app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
    {
        context.Response.StatusCode = 499;
    }
    catch (ArgumentException exception)
    {
        app.Logger.LogWarning(exception, "Invalid API request");
        if (!context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new ApiError("invalid_request", exception.Message));
        }
    }
    catch (Exception exception)
    {
        app.Logger.LogError(exception, "Unhandled request error");
        if (!context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsJsonAsync(new ApiError("internal_error", exception.Message));
        }
    }
});

app.Use(async (context, next) =>
{
    var expectedUser = Environment.GetEnvironmentVariable("RM500U_WEB_USER");
    var expectedPassword = Environment.GetEnvironmentVariable("RM500U_WEB_PASSWORD");

    if (string.IsNullOrEmpty(expectedUser) || string.IsNullOrEmpty(expectedPassword))
    {
        await next();
        return;
    }

    if (TryReadBasicCredentials(context.Request.Headers.Authorization.ToString(), out var user, out var password) &&
        FixedTimeEquals(user, expectedUser) && FixedTimeEquals(password, expectedPassword))
    {
        await next();
        return;
    }

    context.Response.Headers.WWWAuthenticate = "Basic realm=\"RM500U Console\", charset=\"UTF-8\"";
    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
});

app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api") &&
        !HttpMethods.IsGet(context.Request.Method) &&
        !HttpMethods.IsHead(context.Request.Method) &&
        context.Request.Headers.Origin.Count > 0)
    {
        var origin = context.Request.Headers.Origin.ToString();
        var expectedOrigin = $"{context.Request.Scheme}://{context.Request.Host}";
        if (!string.Equals(origin, expectedOrigin, StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new ApiError("origin_rejected", "Cross-origin write requests are not allowed."));
            return;
        }
    }

    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/health", (RuntimeOptions runtime) => Results.Ok(new
{
    status = "ok",
    service = "rm500u-web",
    version = "1.0.0",
    simulation = runtime.UseSimulation,
    timestamp = DateTimeOffset.UtcNow
}));

app.MapGet("/api/config", async (ConfigStore store, CancellationToken cancellationToken) =>
    Results.Ok(await store.LoadAsync(cancellationToken)));

app.MapPut("/api/config", async (ModemConfig config, ConfigStore store, Rm500uService modem, CancellationToken cancellationToken) =>
{
    var validated = ModemConfigValidator.Validate(config);
    await store.SaveAsync(validated, cancellationToken);
    modem.InvalidateCache();
    return Results.Ok(validated);
});

app.MapGet("/api/device/candidates", async (AtGateway gateway, CancellationToken cancellationToken) =>
    Results.Ok(await gateway.GetCandidatesAsync(cancellationToken)));

app.MapPost("/api/device/scan", async (AtGateway gateway, Rm500uService modem, CancellationToken cancellationToken) =>
{
    var result = await gateway.ScanAsync(cancellationToken);
    modem.InvalidateCache();
    return result is null
        ? Results.NotFound(new ApiError("rm500u_not_found", "No RM500U modem responded on the available AT ports."))
        : Results.Ok(result);
});

app.MapGet("/api/status", async (Rm500uService modem, bool? refresh, CancellationToken cancellationToken) =>
    Results.Ok(await modem.GetStatusAsync(refresh == true, cancellationToken)));

app.MapPost("/api/connection/connect", async (DialService dial, CancellationToken cancellationToken) =>
    Results.Ok(await dial.ConnectAsync(cancellationToken)));

app.MapPost("/api/connection/disconnect", async (DialService dial, CancellationToken cancellationToken) =>
    Results.Ok(await dial.DisconnectAsync(cancellationToken)));

app.MapPost("/api/modem/reboot", async (Rm500uService modem, CancellationToken cancellationToken) =>
    Results.Ok(await modem.RebootAsync(cancellationToken)));

app.MapPut("/api/modem/usb-mode", async (UsbModeRequest request, Rm500uService modem, CancellationToken cancellationToken) =>
    Results.Ok(await modem.SetUsbModeAsync(request.Mode, request.Reboot, cancellationToken)));

app.MapGet("/api/modem/nat-mode", async (Rm500uService modem, CancellationToken cancellationToken) =>
    Results.Ok(await modem.GetNatModeAsync(cancellationToken)));

app.MapPut("/api/modem/nat-mode", async (NatModeRequest request, Rm500uService modem, CancellationToken cancellationToken) =>
    Results.Ok(await modem.SetNatModeAsync(request.Mode, request.Reboot, cancellationToken)));

app.MapPut("/api/modem/sim-slot", async (SimSlotRequest request, Rm500uService modem, CancellationToken cancellationToken) =>
    Results.Ok(await modem.SetSimSlotAsync(request.Slot, cancellationToken)));

app.MapPut("/api/radio/preference", async (NetworkPreferenceRequest request, Rm500uService modem, CancellationToken cancellationToken) =>
    Results.Ok(await modem.SetNetworkPreferenceAsync(request.Modes, cancellationToken)));

app.MapGet("/api/radio/bands", async (Rm500uService modem, CancellationToken cancellationToken) =>
    Results.Ok(await modem.GetBandsAsync(cancellationToken)));

app.MapPut("/api/radio/bands", async (BandLockRequest request, Rm500uService modem, CancellationToken cancellationToken) =>
    Results.Ok(await modem.SetBandsAsync(request, cancellationToken)));

app.MapGet("/api/cells", async (Rm500uService modem, CancellationToken cancellationToken) =>
    Results.Ok(await modem.GetNeighborCellsAsync(cancellationToken)));

app.MapPost("/api/cells/lock", async (CellLockRequest request, Rm500uService modem, CancellationToken cancellationToken) =>
    Results.Ok(await modem.LockCellAsync(request, cancellationToken)));

app.MapPost("/api/cells/unlock", async (CellUnlockRequest request, Rm500uService modem, CancellationToken cancellationToken) =>
    Results.Ok(await modem.UnlockCellAsync(request.Rat, cancellationToken)));

app.MapPost("/api/at", async (AtCommandRequest request, Rm500uService modem, CancellationToken cancellationToken) =>
    Results.Ok(await modem.SendRawAtAsync(request.Command, request.TimeoutSeconds, cancellationToken)));

app.MapGet("/api/sms", async (bool? refresh, Rm500uService modem) =>
    Results.Ok(await modem.ListSmsSnapshotAsync(refresh == true)));

app.MapPost("/api/sms", async (SmsSendRequest request, Rm500uService modem, CancellationToken cancellationToken) =>
    Results.Ok(await modem.SendSmsAsync(request, cancellationToken)));

app.MapPost("/api/sms/delete", async (SmsDeleteRequest request, Rm500uService modem, CancellationToken cancellationToken) =>
    Results.Ok(await modem.DeleteSmsAsync(request.Indices, cancellationToken)));

app.MapPost("/api/sms/read", async (SmsReadRequest request, Rm500uService modem, CancellationToken cancellationToken) =>
    Results.Ok(await modem.MarkSmsReadAsync(request.Indices, cancellationToken)));

app.MapDelete("/api/sms", async ([FromBody] SmsDeleteRequest request, Rm500uService modem, CancellationToken cancellationToken) =>
    Results.Ok(await modem.DeleteSmsAsync(request.Indices, cancellationToken)));

app.MapDelete("/api/sms/{index:int}", async (int index, Rm500uService modem, CancellationToken cancellationToken) =>
    Results.Ok(await modem.DeleteSmsAsync(index, cancellationToken)));

app.MapGet("/api/apn", async (Rm500uService modem, CancellationToken cancellationToken) =>
    Results.Ok(await modem.GetApnStateAsync(cancellationToken)));

app.MapPut("/api/apn/profiles", async (ApnProfilesRequest request, ConfigStore store, Rm500uService modem, CancellationToken cancellationToken) =>
{
    var current = await store.LoadAsync(cancellationToken);
    var profiles = request.Profiles ?? [];
    if (profiles.Count == 0)
        throw new ArgumentException("At least one APN profile is required.");
    var validated = ModemConfigValidator.Validate(current with
    {
        ApnProfiles = profiles,
        ActiveApnProfileId = request.ActiveProfileId
    });
    await store.SaveAsync(validated, cancellationToken);
    modem.InvalidateCache();
    return Results.Ok(await modem.GetApnStateAsync(cancellationToken));
});

app.MapPost("/api/apn/apply", async (ApnApplyRequest request, ConfigStore store, Rm500uService modem, DialService dial, CancellationToken cancellationToken) =>
{
    var current = await store.LoadAsync(cancellationToken);
    var profile = current.ApnProfiles.FirstOrDefault(item => item.Id.Equals(request.ProfileId, StringComparison.OrdinalIgnoreCase));
    if (profile is null)
        throw new ArgumentException("The selected APN profile does not exist.");

    var validated = ModemConfigValidator.Validate(current with { ActiveApnProfileId = profile.Id });
    await store.SaveAsync(validated, cancellationToken);
    var apply = await modem.ApplyApnAsync(validated.ActiveApnProfile, cancellationToken);
    if (!apply.Success)
        return Results.Ok(new { apply.Success, apply.Message, apply.Raw, state = await modem.GetApnStateAsync(cancellationToken) });

    ActionResultModel? disconnect = null;
    ActionResultModel? connect = null;
    if (request.Reconnect)
    {
        disconnect = await dial.DisconnectAsync(cancellationToken);
        connect = await dial.ConnectAsync(cancellationToken);
    }
    return Results.Ok(new
    {
        success = connect?.Success ?? true,
        message = connect is null ? apply.Message : connect.Message,
        raw = connect?.Raw ?? apply.Raw,
        apply,
        disconnect,
        connect,
        state = await modem.GetApnStateAsync(cancellationToken)
    });
});

app.MapGet("/api/sms/webhook", async (SmsWebhookService webhook, CancellationToken cancellationToken) =>
    Results.Ok(await webhook.GetStatusAsync(cancellationToken)));

app.MapGet("/api/sms/webhook/status", async (SmsWebhookService webhook, CancellationToken cancellationToken) =>
    Results.Ok(await webhook.GetStatusAsync(cancellationToken)));

app.MapPut("/api/sms/webhook", async (SmsWebhookConfig request, ConfigStore store, SmsWebhookService webhook, CancellationToken cancellationToken) =>
{
    var current = await store.LoadAsync(cancellationToken);
    var validated = ModemConfigValidator.Validate(current with { SmsWebhook = request });
    await store.SaveAsync(validated, cancellationToken);
    return Results.Ok(await webhook.GetStatusAsync(cancellationToken));
});

app.MapPost("/api/sms/webhook/test", async (SmsWebhookService webhook, CancellationToken cancellationToken) =>
    Results.Ok(await webhook.TestAsync(cancellationToken)));

app.MapGet("/api/logs", (OperationLog log) => Results.Ok(log.Snapshot()));
app.MapDelete("/api/logs", (OperationLog log) =>
{
    log.Clear();
    return Results.Ok(new { success = true });
});

app.MapFallbackToFile("index.html");
app.Run();

static bool TryReadBasicCredentials(string header, out string user, out string password)
{
    user = string.Empty;
    password = string.Empty;
    if (!header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    try
    {
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim()));
        var separator = decoded.IndexOf(':');
        if (separator < 0)
        {
            return false;
        }

        user = decoded[..separator];
        password = decoded[(separator + 1)..];
        return true;
    }
    catch (FormatException)
    {
        return false;
    }
}

static bool FixedTimeEquals(string left, string right)
{
    var leftBytes = Encoding.UTF8.GetBytes(left);
    var rightBytes = Encoding.UTF8.GetBytes(right);
    return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
}

public partial class Program;
