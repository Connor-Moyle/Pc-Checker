namespace PcChecker.App;

/// <summary>Entry point.</summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
