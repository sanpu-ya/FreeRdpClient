using System.Text.Json;
using FreeRdpClient.Models;

namespace FreeRdpClient.Services;

/// <summary>Recent connections, persisted in %LOCALAPPDATA%\FreeRdpClient\connections.json.</summary>
public static class ConnectionStore
{
    private const int MaxEntries = 30;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string FilePath = Path.Combine(App.DataDirectory, "connections.json");
    private static List<ConnectionProfile>? _profiles;

    public static event EventHandler? Changed;

    public static IReadOnlyList<ConnectionProfile> Profiles => Load();

    public static void Save(ConnectionProfile profile)
    {
        if (profile.RdpFilePath != null)
            return;

        var list = Load();
        list.RemoveAll(p => p.Id == profile.Id || SameTarget(p, profile));
        profile.LastConnected = DateTimeOffset.Now;
        list.Insert(0, profile.Clone());
        if (list.Count > MaxEntries)
            list.RemoveRange(MaxEntries, list.Count - MaxEntries);
        Persist(list);
    }

    public static void Remove(ConnectionProfile profile)
    {
        var list = Load();
        list.RemoveAll(p => p.Id == profile.Id);
        CredentialStore.Delete(profile);
        Persist(list);
    }

    private static bool SameTarget(ConnectionProfile a, ConnectionProfile b) =>
        string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase) &&
        a.Port == b.Port &&
        string.Equals(a.UserName ?? "", b.UserName ?? "", StringComparison.OrdinalIgnoreCase);

    private static List<ConnectionProfile> Load()
    {
        if (_profiles != null)
            return _profiles;

        try
        {
            if (File.Exists(FilePath))
                _profiles = JsonSerializer.Deserialize<List<ConnectionProfile>>(File.ReadAllText(FilePath), JsonOptions);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to read {FilePath}: {ex.Message}");
        }

        return _profiles ??= [];
    }

    private static void Persist(List<ConnectionProfile> list)
    {
        _profiles = list;
        try
        {
            Directory.CreateDirectory(App.DataDirectory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(list, JsonOptions));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to write {FilePath}: {ex.Message}");
        }
        Changed?.Invoke(null, EventArgs.Empty);
    }
}
