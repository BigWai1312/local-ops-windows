using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LocalOpsWindows.Client;

internal sealed record PaletteCommand(string Label, string Detail, Action Run);

internal sealed class CommandPalette : Window
{
    internal CommandPalette(IEnumerable<PaletteCommand> commands)
    {
        Title = "命令面板";
        Style = (Style)FindResource("AppWindow");
        Width = 620;
        Height = Math.Min(500, SystemParameters.WorkArea.Height - 40);
        MinWidth = 480;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "PopupBrush");
        var root = new DockPanel { Margin = new Thickness(18) };
        var search = new TextBox { Style = (Style)FindResource("TextInput"), Tag = "搜索项目或操作", Margin = new Thickness(0,0,0,12) };
        System.Windows.Automation.AutomationProperties.SetName(search, "搜索命令");
        DockPanel.SetDock(search, Dock.Top);
        root.Children.Add(search);
        var list = new ListBox { DisplayMemberPath = "Label", Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(0) };
        var all = commands.ToList();
        void Filter()
        {
            list.ItemsSource = all.Where(c => $"{c.Label} {c.Detail}".Contains(search.Text.Trim(), StringComparison.OrdinalIgnoreCase)).Take(80).ToList();
            list.SelectedIndex = 0;
        }
        void Execute()
        {
            if (list.SelectedItem is not PaletteCommand command) return;
            Close();
            command.Run();
        }
        search.TextChanged += (_, _) => Filter();
        list.MouseDoubleClick += (_, _) => Execute();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            else if (e.Key == Key.Enter) { Execute(); e.Handled = true; }
            else if (e.Key is Key.Down or Key.Up)
            {
                list.SelectedIndex = Math.Clamp(list.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, Math.Max(0, list.Items.Count - 1));
                if (list.SelectedItem is { } selected) list.ScrollIntoView(selected);
                e.Handled = true;
            }
        };
        root.Children.Add(list);
        Content = root;
        Loaded += (_, _) => search.Focus();
        Filter();
    }
}
