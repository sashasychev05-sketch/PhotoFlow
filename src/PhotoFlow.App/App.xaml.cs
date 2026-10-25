using Microsoft.UI.Xaml;

namespace PhotoFlow.App;

public partial class App : Application
{
    public static MainWindow Window { get; private set; } = null!;
    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            var directory = Environment.GetEnvironmentVariable("PHOTOFLOW_DATA_DIR")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoFlow");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "last-error.log"), e.Exception.ToString());
        };
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Window = new MainWindow();
        Window.Activate();
    }
}
