using System.Text.Json;
using RM500U.Web.Models;

namespace RM500U.Web.Services;

public sealed class ConfigStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ModemConfig? _cached;

    public ConfigStore(RuntimeOptions runtime, OperationLog log)
    {
        Directory.CreateDirectory(runtime.DataDirectory);
        _path = Path.Combine(runtime.DataDirectory, "config.json");
        log.Add("info", "config", $"Configuration path: {_path}");
    }

    public async Task<ModemConfig> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (_cached is not null)
            return _cached;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_cached is not null)
                return _cached;

            if (!File.Exists(_path))
            {
                _cached = new ModemConfig();
                await WriteAsync(_cached, cancellationToken);
                return _cached;
            }

            ModemConfig config;
            await using (var stream = File.OpenRead(_path))
            {
                config = await JsonSerializer.DeserializeAsync(stream, ApiJsonSerializerContext.Default.ModemConfig, cancellationToken)
                         ?? new ModemConfig();
            }
            var migrated = config.ApnProfiles is not { Count: > 0 };
            _cached = ModemConfigValidator.Validate(config);
            if (migrated)
                await WriteAsync(_cached, cancellationToken);
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(ModemConfig config, CancellationToken cancellationToken = default)
    {
        config = ModemConfigValidator.Validate(config);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await WriteAsync(config, cancellationToken);
            _cached = config;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task WriteAsync(ModemConfig config, CancellationToken cancellationToken)
    {
        var temporaryPath = _path + ".tmp";
        await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, config, ApiJsonSerializerContext.Default.ModemConfig, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        File.Move(temporaryPath, _path, true);
    }
}
