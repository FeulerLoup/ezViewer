using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace EzViewer.Core;

/// <summary>
/// 设置默认保存在 exe 同目录的 ezViewer.json（便携）；该目录不可写时改存到 %AppData%\ezViewer。
/// </summary>
public sealed class AppSettings
{
    public const string FileName = "ezViewer.json";

    public List<string> Favorites { get; set; } = new();

    public string? SelectedFavorite { get; set; }

    public bool RandomOrder { get; set; }

    public string? LastOpenFolder { get; set; }

    public bool SidebarVisible { get; set; } = true;

    public double SidebarWidth { get; set; } = 280;

    public double WindowLeft { get; set; } = double.NaN;

    public double WindowTop { get; set; } = double.NaN;

    public double WindowWidth { get; set; } = 1280;

    public double WindowHeight { get; set; } = 820;

    public bool WindowMaximized { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    private static string ExeDirectory =>
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    private static string PortablePath => Path.Combine(ExeDirectory, FileName);

    private static string AppDataPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ezViewer", FileName);

    private string _path = PortablePath;

    [System.Text.Json.Serialization.JsonIgnore]
    public string StoragePath => _path;

    public static AppSettings Load()
    {
        foreach (var candidate in new[] { PortablePath, AppDataPath })
        {
            try
            {
                if (!File.Exists(candidate)) continue;
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(candidate), JsonOptions) ?? new AppSettings();
                settings._path = candidate;
                settings.Favorites ??= new List<string>();
                return settings;
            }
            catch (Exception)
            {
                // 文件损坏时使用默认设置，保存时会覆盖
            }
        }
        return new AppSettings();
    }

    public void Save()
    {
        if (TrySave(_path)) return;
        if (!PathUtil.Equal(_path, AppDataPath) && TrySave(AppDataPath))
            _path = AppDataPath;
    }

    private bool TrySave(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
