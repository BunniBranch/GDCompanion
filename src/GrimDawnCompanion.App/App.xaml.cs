using System.Windows;

namespace GrimDawnCompanion.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "GDCompanion", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
    }
}
