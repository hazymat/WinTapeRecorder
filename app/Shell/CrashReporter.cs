using System.IO;
using System.Windows;
using System.Windows.Threading;
using WinTapeRecorder.Settings;
namespace WinTapeRecorder.Shell;

// Crash and hang detection: unhandled errors and UI freezes go to logs\crash.log; the next launch says so (SPEC.md › Diagnostics)
public static class CrashReporter
{
    static string CrashLogPath => Path.Combine(AppPaths.LogsFolder, "crash.log");
    static string MarkerPath => Path.Combine(AppPaths.LogsFolder, "crash.unseen");
    static readonly object writeLock = new();
    static string lastActivity = "";
    static DateTime lastActivityAt;

    // A breadcrumb of what the shell was doing, written with any crash or hang
    public static void Note(string activity) { lastActivity = activity; lastActivityAt = DateTime.Now; }

    public static void Install(Application application)
    {
        AppDomain.CurrentDomain.UnhandledException += (sender, args) => Write("Crash (unhandled)", args.ExceptionObject as Exception);
        application.DispatcherUnhandledException += (sender, args) => { Write("UI error (kept running)", args.Exception); args.Handled = true; };
        TaskScheduler.UnobservedTaskException += (sender, args) => { Write("Background task error", args.Exception); args.SetObserved(); };
        StartHangWatchdog(application.Dispatcher);
    }

    public static void Write(string kind, Exception? error, bool markUnseen = true)
    {
        try
        {
            lock (writeLock)
            {
                File.AppendAllText(CrashLogPath, $"[{DateTime.Now:O}] {kind} · version {typeof(App).Assembly.GetName().Version?.ToString(3)}\n  last activity: {lastActivity} ({lastActivityAt:HH:mm:ss.fff})\n{error}\n\n");
                if (markUnseen) File.WriteAllText(MarkerPath, kind);
            }
        }
        catch { }
    }

    // Pings the UI thread every 2 s from a background thread; 8 s without an answer is logged as a hang, and again when it recovers
    static void StartHangWatchdog(Dispatcher uiDispatcher)
    {
        var watchdog = new Thread(() =>
        {
            while (true)
            {
                var answered = new ManualResetEventSlim(false);
                var askedAt = DateTime.Now;
                uiDispatcher.BeginInvoke(DispatcherPriority.Send, () => answered.Set());
                if (!answered.Wait(TimeSpan.FromSeconds(8)))
                {
                    Write("UI thread not responding for 8 s", null);
                    answered.Wait();
                    Write($"UI thread recovered after {(DateTime.Now - askedAt).TotalSeconds:0.0} s", null, false);
                }
                Thread.Sleep(2000);
            }
        }) { IsBackground = true, Name = "Hang watchdog" };
        watchdog.Start();
    }

    // On launch: true (and clears the flag) when the last run crashed or hung
    public static bool TakeUnseenReport() { try { if (!File.Exists(MarkerPath)) return false; File.Delete(MarkerPath); return true; } catch { return false; } }
    public static string LogPath => CrashLogPath;
}
