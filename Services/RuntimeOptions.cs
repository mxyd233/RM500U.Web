namespace RM500U.Web.Services;

public sealed class RuntimeOptions
{
    public RuntimeOptions(IHostEnvironment environment)
    {
        ContentRoot = environment.ContentRootPath;
        IsLinux = OperatingSystem.IsLinux();
        IsWindows = OperatingSystem.IsWindows();
        UseSimulation = ReadBoolean("RM500U_SIMULATE") ?? !(IsLinux || IsWindows);
        DataDirectory = Environment.GetEnvironmentVariable("RM500U_DATA_DIR") ??
                        (IsLinux
                            ? "/var/lib/rm500u-web"
                            : Path.Combine(ContentRoot, "data"));
    }

    public bool UseSimulation { get; }
    public bool IsLinux { get; }
    public bool IsWindows { get; }
    public string ContentRoot { get; }
    public string DataDirectory { get; }

    private static bool? ReadBoolean(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return bool.TryParse(value, out var parsed) ? parsed : null;
    }
}
