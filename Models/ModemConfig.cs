using System.Text.Json.Serialization;

namespace RM500U.Web.Models;

public sealed record ApnProfile
{
    public string Id { get; init; } = "default";
    public string Name { get; init; } = "Default";
    public string Apn { get; init; } = string.Empty;
    public int PdpContext { get; init; } = 1;
    public string PdpType { get; init; } = "IPV4V6";
    public string Authentication { get; init; } = "none";
    public string Username { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
}

public sealed record SmsWebhookConfig
{
    public bool Enabled { get; init; }
    public string Url { get; init; } = string.Empty;
    public string Secret { get; init; } = string.Empty;
    public int TimeoutSeconds { get; init; } = 10;
    public int MaxRetries { get; init; } = 3;
}

public sealed record ModemConfig
{
    public string Variant { get; init; } = "RM500U-CN";
    public string AtPort { get; init; } = "auto";
    public int BaudRate { get; init; } = 115200;
    public string InterfaceName { get; init; } = "auto";
    public string Apn { get; init; } = string.Empty;
    public int PdpContext { get; init; } = 1;
    public string PdpType { get; init; } = "IPV4V6";
    public string Authentication { get; init; } = "none";
    public string Username { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
    public IReadOnlyList<ApnProfile> ApnProfiles { get; init; } = [];
    public string ActiveApnProfileId { get; init; } = "default";
    public SmsWebhookConfig SmsWebhook { get; init; } = new();

    [JsonIgnore]
    public ApnProfile ActiveApnProfile =>
        ApnProfiles.FirstOrDefault(profile => profile.Id.Equals(ActiveApnProfileId, StringComparison.OrdinalIgnoreCase))
        ?? new ApnProfile
        {
            Id = "default",
            Name = "Default",
            Apn = Apn,
            PdpContext = PdpContext,
            PdpType = PdpType,
            Authentication = Authentication,
            Username = Username,
            Password = Password
        };
    public bool AutoConnect { get; init; }
    public bool ManageInterface { get; init; } = true;
    public string DhcpClient { get; init; } = "auto";
}

public static class ModemConfigValidator
{
    private static readonly HashSet<string> Variants = new(StringComparer.OrdinalIgnoreCase)
    {
        "RM500U-CN", "RM500U-CNV", "RM500U-EA"
    };

    private static readonly HashSet<string> PdpTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "IP", "IPV6", "IPV4V6"
    };

    private static readonly HashSet<string> AuthTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "none", "pap", "chap", "pap-or-chap"
    };

    private static readonly HashSet<string> DhcpClients = new(StringComparer.OrdinalIgnoreCase)
    {
        "auto", "networkmanager", "udhcpc", "dhclient", "networkd", "none"
    };

    public static ApnProfile DefaultProfile(ModemConfig config) => new()
    {
        Id = "default",
        Name = "Default",
        Apn = config.Apn,
        PdpContext = config.PdpContext,
        PdpType = config.PdpType,
        Authentication = config.Authentication,
        Username = config.Username,
        Password = config.Password
    };

    public static ModemConfig Validate(ModemConfig config)
    {
        if (!Variants.Contains(config.Variant))
            throw new ArgumentException("Variant must be RM500U-CN, RM500U-CNV, or RM500U-EA.");
        if (config.BaudRate is < 9600 or > 921600)
            throw new ArgumentException("Baud rate is outside the supported range.");
        if (!DhcpClients.Contains(config.DhcpClient))
            throw new ArgumentException("Unsupported DHCP client.");

        ValidateToken(config.AtPort, "AT port", 160, allowSlash: true);
        ValidateToken(config.InterfaceName, "Interface name", 64, allowSlash: false);

        var profiles = config.ApnProfiles is { Count: > 0 }
            ? config.ApnProfiles
            : [DefaultProfile(config)];
        var normalizedProfiles = profiles.Select(ValidateProfile).ToArray();
        if (normalizedProfiles.Select(profile => profile.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != normalizedProfiles.Length)
            throw new ArgumentException("APN profile IDs must be unique.");

        var activeId = normalizedProfiles.Any(profile => profile.Id.Equals(config.ActiveApnProfileId, StringComparison.OrdinalIgnoreCase))
            ? normalizedProfiles.First(profile => profile.Id.Equals(config.ActiveApnProfileId, StringComparison.OrdinalIgnoreCase)).Id
            : normalizedProfiles[0].Id;
        var active = normalizedProfiles.First(profile => profile.Id.Equals(activeId, StringComparison.OrdinalIgnoreCase));

        var webhook = ValidateWebhook(config.SmsWebhook ?? new());

        return config with
        {
            Variant = config.Variant.ToUpperInvariant(),
            AtPort = string.IsNullOrWhiteSpace(config.AtPort) ? "auto" : config.AtPort.Trim(),
            InterfaceName = string.IsNullOrWhiteSpace(config.InterfaceName) ? "auto" : config.InterfaceName.Trim(),
            Apn = active.Apn,
            PdpContext = active.PdpContext,
            PdpType = active.PdpType,
            Authentication = active.Authentication,
            Username = active.Username,
            Password = active.Password,
            ApnProfiles = normalizedProfiles,
            ActiveApnProfileId = active.Id,
            SmsWebhook = webhook,
            DhcpClient = config.DhcpClient.ToLowerInvariant()
        };
    }

    private static ApnProfile ValidateProfile(ApnProfile profile)
    {
        var id = string.IsNullOrWhiteSpace(profile.Id) ? "default" : profile.Id.Trim().ToLowerInvariant();
        ValidateToken(id, "APN profile ID", 48, allowSlash: false);
        if (string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Length > 80)
            throw new ArgumentException("APN profile name must contain 1 to 80 characters.");
        if (profile.PdpContext != 1)
            throw new ArgumentException("RM500U support is intentionally fixed to PDP context 1.");
        if (!PdpTypes.Contains(profile.PdpType))
            throw new ArgumentException("PDP type must be IP, IPV6, or IPV4V6.");
        if (!AuthTypes.Contains(profile.Authentication))
            throw new ArgumentException("Unsupported authentication type.");
        ValidateAtValue(profile.Apn, "APN", 100);
        ValidateAtValue(profile.Username, "Username", 100);
        ValidateAtValue(profile.Password, "Password", 100);

        return profile with
        {
            Id = id,
            Name = profile.Name.Trim(),
            Apn = profile.Apn.Trim(),
            PdpType = profile.PdpType.ToUpperInvariant(),
            Authentication = profile.Authentication.ToLowerInvariant(),
            Username = profile.Username.Trim(),
            Password = profile.Password
        };
    }

    private static SmsWebhookConfig ValidateWebhook(SmsWebhookConfig webhook)
    {
        // Older config files and JSON clients may explicitly store null even
        // though the public model uses non-nullable strings.
        var url = (webhook.Url ?? string.Empty).Trim();
        var secret = webhook.Secret ?? string.Empty;
        if (!string.IsNullOrEmpty(url) &&
            (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) ||
             parsed.Scheme is not ("http" or "https")))
            throw new ArgumentException("Webhook URL must be an absolute HTTP or HTTPS URL.");
        if (secret.Length > 256)
            throw new ArgumentException("Webhook secret is too long.");
        return webhook with
        {
            Url = url,
            Secret = secret.Trim(),
            TimeoutSeconds = Math.Clamp(webhook.TimeoutSeconds, 1, 30),
            MaxRetries = Math.Clamp(webhook.MaxRetries, 0, 5)
        };
    }

    private static void ValidateAtValue(string value, string name, int maxLength)
    {
        if (value.Length > maxLength || value.Any(character => character is '\r' or '\n' or '"' or ','))
            throw new ArgumentException($"{name} contains unsupported characters or is too long.");
    }

    private static void ValidateToken(string value, string name, int maxLength, bool allowSlash)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;
        if (value.Length > maxLength || value.Any(character =>
                !(char.IsLetterOrDigit(character) || character is '_' or '-' or '.' || character == ':' ||
                  (allowSlash && character == '/'))))
            throw new ArgumentException($"{name} contains unsupported characters.");
    }
}
