using GrimDawnCompanion.App;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using System.Xml.Linq;

internal static class RadarPillUiRegression
{
    public static void Run(string root, Action<bool, string> require)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
                XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
                var markup = XDocument.Load(Path.Combine(root, "src", "GrimDawnCompanion.App", "MainWindow.xaml"));
                var pill = new XElement(markup.Descendants(ns + "Button").Single(e => (string?)e.Attribute(x + "Name") == "RadarStatusButton"));
                require((string?)pill.Attribute("Click") == "ToggleNavigationMarkers" &&
                    (string?)pill.Attribute("IsEnabled") == "{Binding IsEnabled, ElementName=NavigationToggleButton}",
                    "radar pill shares the existing navigation toggle and availability");
                // Load the production control offscreen; never invoke the live-game handler.
                pill.Attribute("Click")!.Remove();
                var resources = XDocument.Load(Path.Combine(root, "src", "GrimDawnCompanion.App", "App.xaml"))
                    .Root!.Element(ns + "Application.Resources")!;
                var hostMarkup = new XElement(ns + "StackPanel",
                    new XAttribute(XNamespace.Xmlns + "x", x),
                    new XAttribute(XNamespace.Xmlns + "shell", "clr-namespace:System.Windows.Shell;assembly=PresentationFramework"),
                    new XAttribute(XNamespace.Xmlns + "automation", "clr-namespace:System.Windows.Automation;assembly=PresentationCore"),
                    new XElement(ns + "StackPanel.Resources", resources.Elements()),
                    new XElement(ns + "Button", new XAttribute(x + "Name", "NavigationToggleButton"), new XAttribute("IsEnabled", "False")), pill);
                var host = (StackPanel)XamlReader.Parse(hostMarkup.ToString());
                var vm = new MainViewModel();
                host.DataContext = vm;
                var source = (Button)host.FindName("NavigationToggleButton");
                var button = (Button)host.FindName("RadarStatusButton");
                var label = (TextBlock)host.FindName("RadarIndicatorText");
                void Layout()
                {
                    host.Measure(new Size(500, 100)); host.Arrange(new Rect(0, 0, 500, 100)); host.UpdateLayout();
                    Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                }
                Layout();
                require(label.Text == "Radar Inactive" && !button.IsEnabled && WindowChrome.GetIsHitTestVisibleInChrome(button),
                    "radar starts inactive, unavailable and configured for title-bar clicks");
                source.IsEnabled = true;
                Layout();
                require(button.IsEnabled && Equals(button.ToolTip, "Turn radar on"), "ready radar pill offers activation");
                var clicks = 0;
                button.Click += (_, _) => { clicks++; vm.NavigationActive = !vm.NavigationActive; };
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Layout();
                require(clicks == 1 && label.Text == "Radar Active" && Equals(button.ToolTip, "Turn radar off") &&
                    ((SolidColorBrush)button.Background).Color == ((SolidColorBrush)host.FindResource("AccentDark")).Color,
                    "radar pill shows active text, color and off action after successful activation");
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Layout();
                require(clicks == 2 && label.Text == "Radar Inactive", "radar pill supports repeated on/off clicks");
                vm.NavigationActive = true;
                source.IsEnabled = false;
                Layout();
                require(label.Text == "Radar Active" && !button.IsEnabled, "busy radar keeps confirmed state while disabling duplicate clicks");
                vm.NavigationActive = false;
                Layout();
                require(label.Text == "Radar Inactive" && !button.IsEnabled, "disconnect or radar failure updates the pill to inactive");
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (failure is not null) throw new InvalidOperationException("Radar pill UI regression failed.", failure);
    }
}
