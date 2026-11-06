using Velopack;
namespace WinTapeRecorder;

public static class Program
{
    [STAThread]
    public static void Main(string[] commandLineArgs)
    {
        VelopackApp.Build().Run(); // must run first: handles install, update and uninstall hooks
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
