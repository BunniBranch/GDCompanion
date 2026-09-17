using GrimDawnCompanion.App;
using GrimDawnCompanion.Core;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;

internal static class CatalogFilterUiRegression
{
    public static void Run(string root, Action<bool, string> require)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
                var resources = XDocument.Load(Path.Combine(root, "src", "GrimDawnCompanion.App", "App.xaml"))
                    .Root!.Element(ns + "Application.Resources")!;
                var dictionary = new XElement(ns + "ResourceDictionary",
                    new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"), resources.Elements());
                var vm = new MainViewModel();
                var host = new StackPanel { Resources = (ResourceDictionary)XamlReader.Parse(dictionary.ToString()), DataContext = vm };
                var category = Create("Categories", "SelectedCategory");
                var source = Create("Sources", "SelectedSource");
                host.Children.Add(category);
                host.Children.Add(source);
                void Check(string stage)
                {
                    host.Measure(new Size(500, 200)); host.Arrange(new Rect(0, 0, 500, 200)); host.UpdateLayout();
                    Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                    host.UpdateLayout();
                    require(Equals(category.SelectedItem, "All Categories") && Equals(source.SelectedItem, "All Content"),
                        stage + ": catalog selects both All defaults");
                    require(Texts(category).Contains("All Categories") && Texts(source).Contains("All Content"),
                        stage + ": closed dropdowns actually display both default labels");
                }
                Check("Startup");
                var catalog = new CatalogDocument { Items = [
                    new ItemRecord("Sword", "sword.dbr", "Base Game", "Weapons", "Common", 1, "", ""),
                    new ItemRecord("Hat", "hat.dbr", "DLC", "Armor", "Common", 1, "", "")
                ] };
                vm.SetCatalog(catalog);
                Check("Catalog reload");
                category.SelectedItem = "Weapons";
                source.SelectedItem = "Base Game";
                host.UpdateLayout();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                require(vm.SelectedCategory == "Weapons" && Texts(category).Contains("Weapons") &&
                    vm.SelectedSource == "Base Game" && Texts(source).Contains("Base Game") && vm.ItemsView.Cast<ItemRecord>().Count() == 1,
                    "chosen filters replace visible defaults and filter catalog items");
                vm.SetCatalog(catalog);
                Check("Reload after selecting filters");
                require(vm.ItemsView.Cast<ItemRecord>().Count() == 2, "reset defaults show all catalog items");
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (failure is not null) throw new InvalidOperationException("Catalog filter UI regression failed.", failure);
    }

    private static ComboBox Create(string items, string selection)
    {
        var box = new ComboBox { Width = 190 };
        box.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(items));
        box.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, new Binding(selection) { Mode = BindingMode.TwoWay });
        return box;
    }

    private static IEnumerable<string> Texts(DependencyObject parent)
    {
        if (parent is TextBlock text) yield return text.Text;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            foreach (var value in Texts(VisualTreeHelper.GetChild(parent, i))) yield return value;
    }
}
