using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Snaply.Core.Common;
using Snaply.Core.Interfaces;
using Snaply.Core.Models;
using Snaply.Core.Services;
using Snaply.Infrastructure.Persistence;

namespace Snaply.Infrastructure.Settings;

/// <summary>Stores each AppSettings property as a JSON value in the ApplicationSetting table, with a schema version row.</summary>
public sealed class ApplicationSettingsService : IApplicationSettingsService
{
    private const string SchemaKey = "SchemaVersion";
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };
    private static readonly PropertyInfo[] Props = typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p is { CanRead: true, CanWrite: true }).ToArray();

    private readonly IDbContextFactory<SnaplyDbContext> _factory;
    private readonly AppPaths _paths;
    private readonly ILogger<ApplicationSettingsService> _log;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private AppSettings _current = new();

    public ApplicationSettingsService(IDbContextFactory<SnaplyDbContext> factory, AppPaths paths, ILogger<ApplicationSettingsService> log)
    {
        _factory = factory; _paths = paths; _log = log;
    }

    public AppSettings Current => _current;
    public event EventHandler<AppSettings>? Changed;

    public string EffectiveSaveDirectory =>
        string.IsNullOrWhiteSpace(_current.SaveDirectory) ? _paths.DefaultScreenshotsDirectory : _current.SaveDirectory!;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.ApplicationSettings.AsNoTracking().ToDictionaryAsync(x => x.Key, x => x.Value, ct).ConfigureAwait(false);
        var s = new AppSettings();

        foreach (var p in Props)
        {
            if (!rows.TryGetValue(p.Name, out var raw)) continue;
            try
            {
                var value = JsonSerializer.Deserialize(raw, p.PropertyType, Json);
                if (value is not null) p.SetValue(s, value);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
            {
                _log.LogWarning("Setting {Key} has an invalid stored value; using default", p.Name);
            }
        }

        // Make sure every known hotkey action has an entry, then fall back wholesale if the stored set is invalid.
        foreach (var (action, def) in AppSettings.DefaultHotkeys)
            if (!s.Hotkeys.ContainsKey(action)) s.Hotkeys[action] = def;

        // Directory may have vanished (unplugged drive); validation would create it, so only check for hard failures.
        var check = SettingsValidator.Validate(s);
        if (!check.IsSuccess)
        {
            _log.LogWarning("Stored settings failed validation ({Reason}); falling back to defaults for the invalid parts", check.Error!.Message);
            s = Repair(s);
        }

        _current = s;
    }

    private static AppSettings Repair(AppSettings s)
    {
        var d = new AppSettings();
        var r = s.Clone();
        if (!Enum.IsDefined(r.Theme)) r.Theme = d.Theme;
        if (!Enum.IsDefined(r.DefaultFormat)) r.DefaultFormat = d.DefaultFormat;
        if (r.CaptureDelaySeconds is < 0 or > 60) r.CaptureDelaySeconds = 0;
        if (r.OcrLanguages.Count == 0) r.OcrLanguages = d.OcrLanguages;
        if (!string.IsNullOrWhiteSpace(r.SaveDirectory) && !SettingsValidator.CheckDirectory(r.SaveDirectory).IsSuccess) r.SaveDirectory = null;
        if (!SettingsValidator.Validate(r).IsSuccess) r.Hotkeys = new Dictionary<string, string>(AppSettings.DefaultHotkeys);
        if (!SettingsValidator.Validate(r).IsSuccess) r = d;
        return r;
    }

    public async Task<Result> UpdateAsync(Action<AppSettings> mutate, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var candidate = _current.Clone();
            mutate(candidate);

            var valid = SettingsValidator.Validate(candidate);
            if (!valid.IsSuccess) return valid;

            await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var existing = await db.ApplicationSettings.ToDictionaryAsync(x => x.Key, ct).ConfigureAwait(false);
            void Upsert(string key, string value)
            {
                if (existing.TryGetValue(key, out var row)) row.Value = value;
                else db.ApplicationSettings.Add(new ApplicationSetting { Key = key, Value = value });
            }

            foreach (var p in Props) Upsert(p.Name, JsonSerializer.Serialize(p.GetValue(candidate), p.PropertyType, Json));
            Upsert(SchemaKey, AppSettings.CurrentSchemaVersion.ToString());
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            _current = candidate;
        }
        catch (Exception ex) when (ex is DbUpdateException or Microsoft.Data.Sqlite.SqliteException)
        {
            _log.LogError(ex, "Failed to persist settings");
            return Result.Fail(ErrorCode.Failed, "Settings could not be saved.");
        }
        finally { _lock.Release(); }

        Changed?.Invoke(this, _current);
        return Result.Ok();
    }
}
