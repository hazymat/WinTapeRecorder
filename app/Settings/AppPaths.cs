using System.IO;
namespace WinTapeRecorder.Settings;

public static class AppPaths
{
    public static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "WinTapeRecorder.exe");
    public static string ExeFolder => Path.GetDirectoryName(ExePath) ?? AppContext.BaseDirectory;
#if PORTABLE
    public const bool IsPortable = true;
#else
    // portable.txt next to the exe also forces portable data, for a hand-copied folder
    public static readonly bool IsPortable = File.Exists(Path.Combine(ExeFolder, "portable.txt"));
#endif
    // Portable: data next to the exe. Installed: outside Velopack's own folder, so an update or reinstall never touches it
    public static readonly string DataFolder = IsPortable ? Path.Combine(ExeFolder, "WinTapeRecorder-data") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinTapeRecorder-data");
    public static string SettingsFile => Path.Combine(DataFolder, "settings.json");
    public static string LogsFolder => Directory.CreateDirectory(Path.Combine(DataFolder, "logs")).FullName;
    public static string DefaultSessionsFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "WinTapeRecorder sessions");
    public static string DefaultOutputFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "WinTapeRecorder");
}
