using System.IO;
using System.Text.Json;

namespace OverTranslate.Services.Lan;

internal sealed record LanPreferences(string ReceiveFolder, bool AutoSync = true, bool AutoSyncLibrary = false)
{
    internal static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Downloads", "ScreenTranslate");
    internal static string DefaultPath => Path.Combine(AppDataPaths.Root, "lan-preferences.json");

    public static LanPreferences Load(string path)
    {
        if (!File.Exists(path)) return new(DefaultFolder);
        var value = JsonSerializer.Deserialize<LanPreferences>(File.ReadAllText(path))
            ?? throw new InvalidDataException("局域网设置无效。");
        LanFiles.ValidateFolder(value.ReceiveFolder);
        return value;
    }

    public void Save(string path)
    {
        LanFiles.ValidateFolder(ReceiveFolder);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(this)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
