using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace GrimDawnCompanion.App;

public partial class MainWindow
{
    private void OpenLicenseNotice(object sender, RoutedEventArgs e)
    {
        var file = (sender as Button)?.Tag as string;
        if (file is not ("LICENSE" or "THIRD-PARTY-NOTICES.md")) return;
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file));
        if (file == "THIRD-PARTY-NOTICES.md")
        {
            text += "\n\n" + File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "MINHOOK-LICENSE.txt"));
            text += "\n\n" + File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", "OFL.txt"));
        }
        new Window
        {
            Owner = this, Title = file == "LICENSE" ? "GNU General Public License v3.0" : "Third-Party Notices",
            Width = 800, Height = 600, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(16) }
        }.ShowDialog();
    }
}
