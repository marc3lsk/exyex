using System.Windows;

namespace Mavyex;

// SKELETON – the real entry point (install / setup wizard / single instance / main window) comes with the UI port.
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/mavyex;component/UI/Styles.xaml"),
        });
        var store = Core.Store.Load();
        UI.Theme.Apply(store.Theme);
        var w = new Window { Title = "mavyex", Width = 800, Height = 500, Style = (Style)app.FindResource("BaseWindow") };
        app.MainWindow = w;
        w.Show();
        return app.Run();
    }
}
