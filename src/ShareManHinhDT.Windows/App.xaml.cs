using System.Windows;
using System.Windows.Threading;

namespace ShareManHinhDT.Windows;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool smokeTest = e.Args.Contains("--smoke-test", StringComparer.Ordinal);
        if (smokeTest) { using var decoder = new VideoDecoder(); }
        MainWindow = new MainWindow();
        if (smokeTest)
        {
            MainWindow.ShowActivated = false;
            MainWindow.Left = -20000;
            MainWindow.Top = -20000;
        }
        MainWindow.Show();
        if (!smokeTest) return;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) => { timer.Stop(); MainWindow.Close(); };
        timer.Start();
    }
}
