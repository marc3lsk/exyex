using System.Windows;
using System.Windows.Interop;
using Mavyex.Core;
using Mavyex.Native;
using Mavyex.UI;

namespace Mavyex;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        switch (args.FirstOrDefault())
        {
            case "--install":
                Installer.RunCli(false);
                return 0;
            case "--uninstall":
                Installer.RunCli(true);
                return 0;
        }
        // mavyex_<ver>_x64_setup.exe double-clicked: run the setup wizard
        if (Installer.IsSetupExe(args))
        {
            Installer.RunSetupWizard();
            return 0;
        }
        // one window only: a second `mavyex <path>` hands the path to the running instance and exits
        if (SingleInstance.Forwarded(args)) return 0;

        // tests / screenshots: software rendering makes PrintWindow capture WPF content (hardware rendering gives black)
        if (Environment.GetEnvironmentVariable("MAVYEX_SOFTWARE_RENDER") == "1")
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.DispatcherUnhandledException += (_, e) =>
        {
            LogError(e.Exception);
            e.Handled = true; // a file manager must not vanish because of one bad folder
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogError(e.ExceptionObject as Exception);
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/mavyex;component/UI/Styles.xaml"),
        });
        var store = Store.Load();
        Theme.Apply(store.Theme);
        var main = new MainWindow(store, args.FirstOrDefault());
        app.MainWindow = main;
        SingleInstance.Listen(p => app.Dispatcher.BeginInvoke(() => main.HandleExternalOpen(p)));
        main.Show();
        return app.Run();
    }

    public static void LogError(Exception? e)
    {
        try
        {
            Directory.CreateDirectory(Store.ConfigDir());
            File.AppendAllText(Path.Combine(Store.ConfigDir(), "crash.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {e}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { }
    }
}
