using Velopack;

namespace PcChecker.App;

/// <summary>Entry point. Velopack must run first so install/update hooks can exit early.</summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
