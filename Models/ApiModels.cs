namespace RM500U.Web.Models;

public sealed record ApiError(string Code, string Message);

public sealed record UsbModeRequest(string Mode, bool Reboot = false);

public sealed record NatModeRequest(string Mode, bool Reboot = false);

public sealed record SimSlotRequest(int Slot);

public sealed record NetworkPreferenceRequest(IReadOnlyList<string> Modes);

public sealed record BandLockRequest(string Rat, IReadOnlyList<int> Bands);

public sealed record CellLockRequest(string Rat, int Arfcn, int Pci);

public sealed record CellUnlockRequest(string Rat);

public sealed record AtCommandRequest(string Command, int TimeoutSeconds = 8);

public sealed record SmsSendRequest(string Recipient, string Content);

public sealed record SmsDeleteRequest(IReadOnlyList<int> Indices);

public sealed record SmsReadRequest(IReadOnlyList<int> Indices);

public sealed record ApnProfilesRequest(IReadOnlyList<ApnProfile> Profiles, string ActiveProfileId);

public sealed record ApnApplyRequest(string ProfileId, bool Reconnect = false);

public sealed record ActionResultModel(bool Success, string Message, string? Raw = null);

public sealed record NatModeState(string Mode, int Value, bool RequiresReboot = true);

public sealed record AtCommandResult(string Command, bool Success, bool TimedOut, string Raw, long ElapsedMilliseconds);

public sealed record AtPortCandidate(
    string Port,
    string? StablePort,
    string? VendorId,
    string? ProductId,
    string? UsbInterface,
    bool LikelyQuectel,
    string? DisplayName = null);

public sealed record DeviceCandidate(
    string Port,
    bool IsRm500u,
    string Model,
    string Manufacturer,
    string Response,
    string? StablePort = null,
    string? VendorId = null,
    string? ProductId = null,
    string? UsbInterface = null,
    string? Imei = null);

public sealed record DeviceInfo(
    string Model,
    string Variant,
    string Manufacturer,
    string Firmware,
    string Imei,
    string AtPort,
    string UsbMode,
    string NatMode,
    int SimSlot,
    double? TemperatureCelsius,
    int? VoltageMillivolts);

public sealed record SimInfo(
    string Status,
    string Operator,
    string PhoneNumber,
    string Imsi,
    string Iccid);

public sealed record SignalInfo(
    int Percent,
    int? Csq,
    double? Rssi,
    double? Rsrp,
    double? Rsrq,
    double? Sinr);

public sealed record QosInfo(
    int? Qci,
    int? FiveQi,
    long? DownlinkSubscribedKbps,
    long? UplinkSubscribedKbps,
    long? DownlinkGuaranteedKbps,
    long? UplinkGuaranteedKbps,
    long? DownlinkMaxKbps,
    long? UplinkMaxKbps,
    string Source);

public sealed record CellInfo(
    string Rat,
    string Duplex,
    string Mcc,
    string Mnc,
    string CellId,
    int? Pci,
    int? Arfcn,
    int? Band,
    double? UplinkBandwidthMhz,
    double? DownlinkBandwidthMhz,
    double? Rsrp,
    double? Rsrq,
    double? Rssi,
    double? Sinr,
    int? Cqi,
    int? ScsKhz,
    string Tac);

public sealed record ConnectionInfo(
    bool Connected,
    string NetworkType,
    string IpAddress,
    string InterfaceName,
    string InterfaceState,
    long RxBytes,
    long TxBytes,
    double RxBytesPerSecond,
    double TxBytesPerSecond,
    string NetworkPreference);

public sealed record ModemStatus(
    bool Simulation,
    bool DevicePresent,
    string? Error,
    DateTimeOffset UpdatedAt,
    DeviceInfo Device,
    SimInfo Sim,
    SignalInfo Signal,
    ConnectionInfo Connection,
    IReadOnlyList<CellInfo> Cells,
    QosInfo Qos);

public sealed record BandGroup(string Rat, IReadOnlyList<int> Available, IReadOnlyList<int> Selected);

public sealed record BandState(string Variant, IReadOnlyList<BandGroup> Groups);

public sealed record NeighborCell(string Rat, string Kind, int? Arfcn, int? Pci, double? Rsrp, double? Rsrq, bool Locked);

public sealed record CellLockState(string Rat, bool Locked, int? Arfcn, int? Pci);

public sealed record NeighborCellState(IReadOnlyList<NeighborCell> Cells, IReadOnlyList<CellLockState> Locks);

public enum SmsDirection
{
    Incoming,
    Outgoing
}

public sealed record SmsMessage(
    int Index,
    IReadOnlyList<int> Indices,
    string Status,
    SmsDirection Direction,
    string Peer,
    DateTimeOffset? Timestamp,
    string Content,
    bool IsMultipart,
    int SegmentCount,
    bool Unread);

public sealed record SmsConversation(
    string Peer,
    IReadOnlyList<SmsMessage> Messages,
    DateTimeOffset? LastTimestamp,
    int UnreadCount);

public sealed record SmsListResult(
    IReadOnlyList<SmsConversation> Conversations,
    string Storage,
    int Used,
    int Total,
    bool IsRefreshing = false);

public sealed record ApnControlState(
    IReadOnlyList<ApnProfile> Profiles,
    string ActiveProfileId,
    ApnProfile? ModemProfile,
    bool Connected,
    string? Error = null);

public sealed record SmsWebhookDeliveryState(
    bool Enabled,
    string Url,
    DateTimeOffset? LastAttemptAt,
    bool? LastSuccess,
    string? LastError,
    int PendingCount);

public sealed record OperationLogEntry(DateTimeOffset Timestamp, string Level, string Source, string Message, string? Detail = null);
