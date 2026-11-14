using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LocalOpsWindows.Preview;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var app = new App();
        app.InitializeComponent();
        var window = new MainWindow(false);
        var root = (FrameworkElement)window.Content;
        System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.Add(new BindingFailureListener());
        var evidence = Path.GetFullPath(Path.Combine("tmp", "wpf-preview-size-monitor"));
        Directory.CreateDirectory(evidence);
        var layouts = 0;
        foreach (var theme in new[] { "light", "dark" })
        {
            window.ApplyTheme(theme, false);
            foreach (var (width, height) in new[] { (1440, 900), (1920, 1080), (1280, 720), (960, 700), (820, 560) })
            {
                foreach (var view in new[] { "launch", "monitor", "logs", "settings" })
                {
                    window.ShowPage(view);
                    Layout(root, width, height);
                    Render(root, width, height, Path.Combine(evidence, $"{theme}-{width}-{view}.png"));
                    Check(window, root, width, height, view);
                    layouts++;
                    if (view == "launch")
                    {
                        SwitchLayout(root, false);
                        Layout(root, width, height);
                        Render(root, width, height, Path.Combine(evidence, $"{theme}-{width}-launch-list.png"));
                        Check(window, root, width, height, view);
                        layouts++;
                        SwitchLayout(root, true);
                    }
                }
            }
        }
        window.ShowPage("launch");
        Layout(root, 1440, 900);
        var search = Descendants<TextBox>(root).First(t => t.Name == "ProjectSearch");
        search.Text = "Atlas";
        Layout(root, 1440, 900);
        Require(Surfaces(root).Count() == 1, "search filters cards");
        SwitchLayout(root, false); Layout(root, 1440, 900);
        Require(Surfaces(root).Count() == 1 && search.Text == "Atlas", "layout switch preserves search");
        SwitchLayout(root, true); Layout(root, 1440, 900);
        search.Clear();
        var kind = Descendants<ComboBox>(root).First(t => AutomationProperties.GetName(t) == "项目类型");
        kind.SelectedItem = "批处理任务"; Layout(root, 1440, 900);
        Require(Surfaces(root).Count() == 6, "type filter");
        SwitchLayout(root, false); Layout(root, 1440, 900);
        Require(Surfaces(root).Count() == 6 && Equals(kind.SelectedItem, "批处理任务"), "layout switch preserves type filter");
        SwitchLayout(root, true); Layout(root, 1440, 900);
        kind.SelectedItem = "全部类型"; Layout(root, 1440, 900);
        var slider = Descendants<Slider>(root).First();
        foreach (var cards in new[] { true, false })
        {
            SwitchLayout(root, cards);
            foreach (var size in new[] { 300d, 480d })
            {
                slider.Value = size;
                foreach (var (width, height) in new[] { (1440, 900), (820, 560) })
                {
                    Layout(root, width, height);
                    Require(Surfaces(root).All(b => Math.Abs(b.ActualHeight - (cards ? 252 : 148)) < 1), "size changes width, not height");
                    Check(window, root, width, height, "launch");
                    Render(root, width, height, Path.Combine(evidence, $"dark-{width}-{(cards ? "cards" : "list")}-size-{size}.png"));
                }
            }
        }
        SwitchLayout(root, true);
        TestLaunchSelection(window, root, evidence);
        window.CardSize = double.NaN; Require(window.CardSize == 320, "nonfinite card size uses default");
        window.CardSize = 200; Require(window.CardSize == 300, "card size lower bound");
        window.CardSize = 999; Require(window.CardSize == 480, "card size upper bound");
        window.CardSize = 320;
        window.ShowPage("launch"); Layout(root, 1440, 900);
        var toggle = Descendants<Button>(root).First(b => b.DataContext is DemoProject p && p.Id == "orchard" && b.ToolTip?.ToString() == "启动");
        ((DemoProject)toggle.DataContext).Cpu = .7;
        toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(((DemoProject)toggle.DataContext).Status == "运行中", "simulated start updates row");
        Require(Math.Abs(Descendants<DonutGauge>(root).First(g => g.ColorKey == "Accent").Value - 3.6) < .001, "start updates aggregate gauge");
        toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(((DemoProject)toggle.DataContext).Status == "未运行", "simulated stop updates row");
        var add = Descendants<Button>(root).First(b => AutomationProperties.GetName(b) == "添加服务");
        add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Layout(root, 1440, 900);
        Require(((UIElement)window.FindName("Overlay")).Visibility == Visibility.Visible, "native editor overlay");
        var fields = Descendants<TextBox>((DependencyObject)window.FindName("DialogBody")).ToList();
        fields[0].Text = "新建演示项目和用于检查卡片截断的超长名称LongProjectNameWithoutSpaces";
        fields[1].Text = "echo " + new string('x', 250); fields[2].Text = @"D:\Demo\" + new string('y', 180); fields[3].Text = "12345";
        fields[4].Text = "服务备注：先启动缓存，再启动工作台。" + new string('备', 150);
        var save = Descendants<Button>((DependencyObject)window.FindName("DialogActions")).Single();
        Render(root, 1440, 900, Path.Combine(evidence, "dark-editor.png"));
        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Layout(root, 1440, 900);
        Require(Surfaces(root).Count() == 18, "editor saves in-memory project");
        Descendants<Slider>(root).Single().Value = 480; Layout(root, 820, 560);
        var added = Surfaces(root).Single(b => b.DataContext is DemoProject p && p.Port == 12345);
        var addedProject = (DemoProject)added.DataContext;
        Require(((DemoProject)added.DataContext).Notes == fields[4].Text, "editor preserves notes");
        CheckSurface(added, root, window.FindResource("TextBrush").ToString()!);
        var projectScroll = Descendants<ScrollViewer>(root).Single(s => s.Name == "LaunchProjects");
        projectScroll.ScrollToVerticalOffset(projectScroll.VerticalOffset + Bounds(added, root).Top - Bounds(projectScroll, root).Top); Layout(root, 820, 560);
        Render(root, 820, 560, Path.Combine(evidence, "dark-820-long-project.png"));
        var noteSearch = Descendants<TextBox>(root).Single(t => t.Name == "ProjectSearch"); noteSearch.Text = "先启动缓存"; Layout(root, 820, 560);
        Require(Surfaces(root).Count() == 1, "search includes notes");
        window.SelectLaunchProject(addedProject, false); Layout(root, 1440, 900);
        var fullInfo = Descendants<Button>(root).Single(b => AutomationProperties.GetName(b) == "完整信息"); fullInfo.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(Descendants<TextBox>((DependencyObject)window.FindName("DialogBody")).Single().Text.Contains(fields[4].Text), "full info includes notes");
        Descendants<Button>(root).Single(b => AutomationProperties.GetName(b) == "关闭对话框").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.ShowPage("logs"); Layout(root, 1440, 900);
        Require(!Descendants<TextBlock>(root).Any(t => t.Text.Contains("GET /api/health")), "noise hidden by default");
        var technical = Descendants<CheckBox>(root).First(c => Equals(c.Content, "显示技术日志"));
        technical.IsChecked = true; Layout(root, 1440, 900);
        Require(Descendants<TextBlock>(root).Any(t => t.Text.Contains("GET /api/health")), "noise toggle works");
        foreach (var name in new[] { "rocket", "server", "activity", "file-text", "sun", "moon", "check", "play", "square", "database", "folder-git-2", "github" }) Require(LucideIcon.Load(name).Length > 0, "Lucide asset " + name);
        Require(new WindowInteropHelper(window).Handle == IntPtr.Zero, "no desktop window");
        File.WriteAllText(Path.Combine(evidence, "result.json"), System.Text.Json.JsonSerializer.Serialize(new { passed = true, layouts, nativeWindowCreated = false }));
        Console.WriteLine($"PASS: {layouts} native WPF layouts, card width/notes/selection/gauges/comparison/filter/log/theme checks, no desktop window. Evidence: {evidence}");
        window.Close();
    }
    private static void Check(MainWindow window, FrameworkElement root, int width, int height, string view)
    {
        var content = (ContentControl)window.FindName("PageContent");
        var expectedText = window.FindResource("TextBrush").ToString();
        Require(((TextBlock)window.FindName("PageTitle")).Foreground.ToString() == expectedText, "page heading follows theme");
        Require(Bounds(content, root).Right <= width + 1 && Bounds(content, root).Bottom <= height + 1, "workspace fits");
        if (view == "launch")
        {
            var rows = Surfaces(root).ToList();
            Require(rows.Count == 17, "17 projects");
            var bounds = rows.Select(r => Bounds(r, root)).ToList();
            if (rows[0].Name == "CardSurface")
            {
                var firstLine = bounds.Where(b => Math.Abs(b.Top - bounds[0].Top) < 1).ToList();
                var cardPanel = Descendants<ProjectCardPanel>(root).First();
                Require(firstLine.Count == Math.Max(1, (int)(cardPanel.ActualWidth / window.CardSize)), "adaptive column count");
                Require(bounds.All(b => Math.Abs(b.Width - bounds[0].Width) < 1), "equal card widths");
            }
            else Require(bounds.All(b => Math.Abs(b.Left - bounds[0].Left) < 1), "single-column list");
            for (var i = 0; i < bounds.Count; i++)
                for (var j = i + 1; j < bounds.Count; j++)
                    Require(!Overlaps(bounds[i], bounds[j]), "projects do not overlap");
            Require(bounds.All(b => b.Left >= Bounds(content, root).Left && b.Right <= Bounds(content, root).Right + 1), "projects fit workspace");
            var headers = Descendants<TextBlock>(root).Where(t => t.DataContext is System.Windows.Data.CollectionViewGroup && t.FontWeight == FontWeights.SemiBold).ToList();
            Require(headers.Count == 2 && headers.All(h => !bounds.Any(b => Overlaps(Bounds(h, root), b))), "group headings stay above cards");
            foreach (var row in rows)
            {
                CheckSurface(row, root, expectedText!);
            }
            var monitor = Descendants<Border>(root).Single(b => b.Name == "LaunchMonitor");
            var scroll = Descendants<ScrollViewer>(root).Single(b => b.Name == "LaunchProjects");
            Require(!Overlaps(Bounds(monitor, root), Bounds(scroll, root)), "monitor does not cover projects");
            Require(Bounds(monitor, root).Bottom <= Bounds(content, root).Bottom + 1, "launch details fit without paging");
            Require(scroll.ActualHeight >= 100, "compact monitor leaves room for projects");
            Require(Descendants<DonutGauge>(monitor).Count() == 2 && Descendants<TrendChart>(monitor).Count() == 1, "compact performance dashboard");
            foreach (var block in Descendants<TextBlock>(monitor)) Require(Bounds(block, root).Right <= Bounds(monitor, root).Right + 1, "monitor text fits");
        }
        if (view == "monitor")
        {
            var list = Descendants<ListBox>(root).Single();
            Require(list.Items.Count == 8, "eight services");
            var grid = (Grid)((FrameworkElement)list.Parent).Parent;
            var detail = (Border)grid.Children[1];
            var show = width >= 1200 && height >= 720;
            var panel = (StackPanel)detail.Child;
            Require(panel.Visibility == (show ? Visibility.Visible : Visibility.Collapsed), "detail visibility");
            if (show)
            {
                Require(Grid.GetColumn(detail) == 2 && Grid.GetRow(detail) == 0, "detail stays right");
                var visible = panel.Children.OfType<FrameworkElement>().Where(c => c.Visibility == Visibility.Visible).ToList();
                Require(visible.All(c => Bounds(c, root).Bottom <= Bounds(content, root).Bottom + 1), "detail fits without paging");
            }
        }
    }
    private static void CheckSurface(Border row, FrameworkElement root, string expectedText)
    {
        var rect = Bounds(row, root);
        var name = Descendants<TextBlock>(row).First(t => t.FontSize == 13 && t.FontWeight == FontWeights.SemiBold);
        Require(name.Foreground.ToString() == expectedText, "project name follows theme");
        var actions = Descendants<Button>(row).ToList();
        Require(actions.Count == 3 && actions.All(b => Math.Abs(b.ActualHeight - 32) < 1), "compact aligned actions");
        var buttons = actions.Select(b => Bounds(b, root)).ToList();
        Require(buttons.All(b => b.Left >= rect.Left && b.Right <= rect.Right + 1 && b.Top >= rect.Top && b.Bottom <= rect.Bottom + 1), "actions fit project");
        Require(buttons.All(b => Math.Abs(b.Top - buttons[0].Top) < 1), "action baselines");
        if (row.Name != "CardSurface") return;
        Require(buttons.All(b => Math.Abs(b.Bottom - (rect.Bottom - 15)) < 1), "actions anchored to card footer");
        var texts = Descendants<TextBlock>(row).Where(t => t.Visibility == Visibility.Visible && t.ActualWidth > 0 && t.ActualHeight > 0).ToList();
        Require(texts.All(t => Bounds(t, root).Right <= rect.Right + 1 && Bounds(t, root).Bottom <= rect.Bottom + 1), "card text fits");
        Require(texts.Where(t => !actions.Any(a => a.IsAncestorOf(t))).All(t => !buttons.Any(b => Overlaps(Bounds(t, root), b))), "text and actions do not overlap");
        var command = texts.Single(t => t.Name == "CardCommand");
        Require(Bounds(command, root).Bottom <= buttons[0].Top, "command stays above footer");
        Require(Bounds(name, root).Width > 60, "project name has usable width");
    }
    private static bool Overlaps(Rect first, Rect second) => Rect.Intersect(first, second) is { Width: > 0, Height: > 0 };
    private static void TestLaunchSelection(MainWindow window, FrameworkElement root, string evidence)
    {
        window.CardSize = 320; window.ShowPage("launch"); Layout(root, 1440, 900);
        var projects = Surfaces(root).Select(b => (DemoProject)b.DataContext).ToList();
        var atlas = projects.Single(p => p.Id == "atlas"); var harbor = projects.Single(p => p.Id == "harbor");
        Require(Math.Abs(Descendants<DonutGauge>(root).First(g => g.ColorKey == "Accent").Value - 2.9) < .001, "overview sums running registered projects only");
        var originalWidth = Surfaces(root).First().ActualWidth;
        Descendants<Slider>(root).Single().Value = 480; Layout(root, 1440, 900);
        Require(Surfaces(root).First().ActualWidth > originalWidth + 50, "size slider changes card width and columns");
        window.CardSize = 320; window.ShowPage("launch"); Layout(root, 1440, 900);
        var atlasCard = Surfaces(root).First();
        atlasCard.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent, Source = atlasCard }); Layout(root, 1440, 900);
        Require(atlas.Selected && projects.Count(p => p.Selected) == 1, "single card selection");
        Require(Descendants<TextBlock>(root).Single(t => t.Name == "LaunchMonitorTitle").Text == atlas.Name, "launch service details");
        Require(Descendants<TrendChart>(root).Single().Series[0].Values.SequenceEqual(PreviewHistory.Values(atlas, false)), "single project changes trend data");
        Render(root, 1440, 900, Path.Combine(evidence, "dark-1440-launch-single.png"));
        window.SelectLaunchProject(harbor, true); Layout(root, 1440, 900);
        Require(projects.Count(p => p.Selected) == 2, "additive card selection");
        Require(Math.Abs(Descendants<DonutGauge>(root).First(g => g.ColorKey == "Accent").Value - 1.8) < .001, "selected CPU sum");
        Require(Descendants<TrendChart>(root).Single().Series.Select(s => s.Name).SequenceEqual(new[] { atlas.Name, harbor.Name }), "multi project separate series");
        var cpu = Descendants<TrendChart>(root).Single().Series[0].Values.ToArray();
        Descendants<RadioButton>(root).Single(b => AutomationProperties.GetName(b) == "对比内存").RaiseEvent(new RoutedEventArgs(RadioButton.ClickEvent)); Layout(root, 1440, 900);
        Require(!Descendants<TrendChart>(root).Single().Series[0].Values.SequenceEqual(cpu), "comparison metric switch changes samples");
        Render(root, 1440, 900, Path.Combine(evidence, "dark-1440-launch-multi.png"));
        foreach (var p in projects.Skip(2)) window.SelectLaunchProject(p, true); Layout(root, 1440, 900);
        var series = Descendants<TrendChart>(root).Single().Series;
        Require(series.Count == 4 && series[3].Name.Contains("14"), "many selections bounded to four series");
        Require(Math.Abs(series.Sum(s => s.Values.Last()) - 7.2) < .001, "remainder series includes all selected memory");
        foreach (var theme in new[] { "light", "dark" })
        {
            window.ApplyTheme(theme, false);
            foreach (var (width, height) in new[] { (1440, 900), (1280, 720), (960, 700), (820, 560) })
            {
                Render(root, width, height, Path.Combine(evidence, $"{theme}-{width}-launch-many.png")); Check(window, root, width, height, "launch");
            }
        }
        var clear = Descendants<Button>(root).Single(b => AutomationProperties.GetName(b) == "清除监控选择"); clear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Layout(root, 1440, 900);
        Require(projects.All(p => !p.Selected), "clear returns overview");
        var checkbox = Descendants<CheckBox>(Surfaces(root).First()).Single(); checkbox.IsChecked = true; Layout(root, 1440, 900);
        Require(atlas.Selected, "checkbox selects project");
        SwitchLayout(root, false); Layout(root, 1440, 900);
        Require(Descendants<CheckBox>(Surfaces(root).First()).Single().IsChecked == true, "selection survives layout change");
        Descendants<TextBox>(root).Single(t => t.Name == "ProjectSearch").Text = "no-match"; Layout(root, 1440, 900);
        Require(!Surfaces(root).Any() && atlas.Selected, "filtering preserves selection");
        Require(Descendants<TextBlock>(root).Single(t => t.Name == "LaunchMonitorTitle").Text == atlas.Name, "filtered selection keeps monitor context");
        window.SelectLaunchProject(atlas, false); window.ShowPage("launch"); Layout(root, 1440, 900);
        SwitchLayout(root, true);
        window.SelectLaunchProject(projects.Single(p => p.Id == "orchard"), false); Layout(root, 1440, 900);
        Require(Descendants<DonutGauge>(root).All(g => g.Value == 0) && Descendants<TrendChart>(root).Single().Series.All(s => s.Values.All(v => v == 0)), "stopped project has zero telemetry");
        window.SelectLaunchProject(projects.Single(p => p.Id == "orchard"), false);
        var gaugeCases = new StackPanel { Width = 68 };
        foreach (var value in new[] { 0d, .1, 100, 150, double.NaN }) gaugeCases.Children.Add(new DonutGauge { Width = 68, Height = 68, Value = value });
        Render(gaugeCases, 68, 340, Path.Combine(evidence, "donut-boundaries.png"));
    }
    private static IEnumerable<Border> Surfaces(DependencyObject root) => Descendants<Border>(root).Where(b => b.Name is "RowSurface" or "CardSurface");
    private static void SwitchLayout(DependencyObject root, bool cards) => Descendants<RadioButton>(root).Single(b => AutomationProperties.GetName(b) == (cards ? "卡片视图" : "列表视图")).RaiseEvent(new RoutedEventArgs(RadioButton.ClickEvent));
    private static void Layout(FrameworkElement root, int width, int height)
    {
        root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
        root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
    }
    private static void Render(FrameworkElement root, int width, int height, string file)
    {
        Layout(root, width, height);
        var frame = new System.Windows.Threading.DispatcherFrame();
        root.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        root.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
        var pixels = new byte[width * height * 4]; bitmap.CopyPixels(pixels, width * 4, 0);
        Require(pixels.Distinct().Take(20).Count() == 20, "nonblank render");
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(file); encoder.Save(stream);
    }
    private static Rect Bounds(FrameworkElement element, Visual root) => element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); if (child is T typed) yield return typed;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class BindingFailureListener : System.Diagnostics.TraceListener
    {
        public override void Write(string? message) { }
        public override void WriteLine(string? message) { if (message?.Contains("Error:") == true) throw new InvalidOperationException(message); }
    }
}
