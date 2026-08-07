using System.Text.Json.Serialization;

namespace RM500U.Web.Models;

internal sealed record SmsWebhookState
{
    public bool BaselineEstablished { get; init; }
    public IReadOnlyList<string> Delivered { get; init; } = [];
    public DateTimeOffset? LastAttemptAt { get; init; }
    public bool? LastSuccess { get; init; }
    public string? LastError { get; init; }
    public int PendingCount { get; init; }
}

internal sealed record SmsWebhookPayload(
    string Event,
    string Source,
    DateTimeOffset SentAt,
    SmsWebhookMessage Message);

internal sealed record SmsWebhookMessage(
    string Direction,
    string Peer,
    DateTimeOffset? Timestamp,
    string Content,
    IReadOnlyList<int> Indices,
    int SegmentCount,
    bool? Multipart = null);

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ApiError))]
[JsonSerializable(typeof(HealthResponse))]
[JsonSerializable(typeof(SuccessResponse))]
[JsonSerializable(typeof(ApnApplyResponse))]
[JsonSerializable(typeof(UsbModeRequest))]
[JsonSerializable(typeof(NatModeRequest))]
[JsonSerializable(typeof(SimSlotRequest))]
[JsonSerializable(typeof(NetworkPreferenceRequest))]
[JsonSerializable(typeof(BandLockRequest))]
[JsonSerializable(typeof(CellLockRequest))]
[JsonSerializable(typeof(CellUnlockRequest))]
[JsonSerializable(typeof(AtCommandRequest))]
[JsonSerializable(typeof(SmsSendRequest))]
[JsonSerializable(typeof(SmsDeleteRequest))]
[JsonSerializable(typeof(SmsReadRequest))]
[JsonSerializable(typeof(ApnProfilesRequest))]
[JsonSerializable(typeof(ApnApplyRequest))]
[JsonSerializable(typeof(ApnProfile))]
[JsonSerializable(typeof(SmsWebhookConfig))]
[JsonSerializable(typeof(ModemConfig))]
[JsonSerializable(typeof(ActionResultModel))]
[JsonSerializable(typeof(NatModeState))]
[JsonSerializable(typeof(AtCommandResult))]
[JsonSerializable(typeof(AtPortCandidate))]
[JsonSerializable(typeof(DeviceCandidate))]
[JsonSerializable(typeof(DeviceInfo))]
[JsonSerializable(typeof(SimInfo))]
[JsonSerializable(typeof(SignalInfo))]
[JsonSerializable(typeof(QosInfo))]
[JsonSerializable(typeof(CellInfo))]
[JsonSerializable(typeof(ConnectionInfo))]
[JsonSerializable(typeof(ModemStatus))]
[JsonSerializable(typeof(BandGroup))]
[JsonSerializable(typeof(BandState))]
[JsonSerializable(typeof(NeighborCell))]
[JsonSerializable(typeof(CellLockState))]
[JsonSerializable(typeof(NeighborCellState))]
[JsonSerializable(typeof(SmsDirection))]
[JsonSerializable(typeof(SmsMessage))]
[JsonSerializable(typeof(SmsConversation))]
[JsonSerializable(typeof(SmsListResult))]
[JsonSerializable(typeof(ApnControlState))]
[JsonSerializable(typeof(SmsWebhookDeliveryState))]
[JsonSerializable(typeof(OperationLogEntry))]
[JsonSerializable(typeof(IReadOnlyList<AtPortCandidate>))]
[JsonSerializable(typeof(IReadOnlyList<OperationLogEntry>))]
[JsonSerializable(typeof(SmsWebhookState))]
[JsonSerializable(typeof(SmsWebhookPayload))]
[JsonSerializable(typeof(SmsWebhookMessage))]
internal partial class ApiJsonSerializerContext : JsonSerializerContext
{
}
