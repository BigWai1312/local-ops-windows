using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LocalOpsWindows.Client;

internal static class UiLayoutTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static Task RunAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { Run(); completion.SetResult(); }
            catch (Exception ex) { completion.SetException(ex); }
            finally { Application.Current?.Shutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static void Run()
    {
        var app = new App();
        app.InitializeComponent();
        // No product startup, native window, tray, backend or desktop interaction.
        app.Startup -= (StartupEventHandler)Delegate.CreateDelegate(typeof(StartupEventHandler), app,
            typeof(App).GetMethod("OnStartup", Private | BindingFlags.DeclaredOnly)!);
        var window = new MainWindow(["--demo"]);
        Invoke(window, "OnLoaded", window, new RoutedEventArgs());
        var root = (FrameworkElement)window.Content;
        var list = (ListView)window.FindName("AppsList");
        var evidence = Path.GetFullPath(Path.Combine("tmp", "ui-h5"));
        Directory.CreateDirectory(evidence);
        foreach (var theme in new[] { "dark", "light" })
        {
            Invoke(window, "ApplyTheme", theme);
            CheckContrast(window, theme);
            CheckButtonStates(window);
            Require(((TextBlock)window.FindName("BackendStatusText")).Foreground.ToString()
                == window.FindResource("AccentBrush").ToString(), "backend status follows theme changes");
            foreach (var width in new[] { 960, 1080, 1440, 1920 })
            {
                window.Width = width;
                var height = width switch { 960 => 700, 1080 => 720, 1440 => 900, _ => 1080 };
                Invoke(window, "UpdateResponsiveLayout");
                Layout(root, width, height);
                var panels = Descendants<ProjectCardPanel>(list).ToList();
                Render(root, Path.Combine(evidence, $"launchpad-{theme}-{width}.png"));
                Require(panels.Count == 2 && panels.All(p => double.IsFinite(p.ItemWidth) && p.ItemWidth >= 310),
                    $"{theme}/{width}: two responsive grids with usable card widths; actual {panels.Count}: "
                    + string.Join(", ", panels.Select(p => $"{p.ItemWidth}/{p.ActualWidth}/{p.Children.Count}")));
                var groups = ((ListCollectionView)list.ItemsSource).Groups!;
                Require((string)((CollectionViewGroup)groups[0]).Name == "service"
                    && (string)((CollectionViewGroup)groups[1]).Name == "task", "service section precedes tasks");
                var cards = Descendants<Button>(list).Where(b => b.Width == 96).ToList();
                Require(cards.Count == 6, $"{theme}/{width}: every project has an action");
                var names = Descendants<TextBlock>(list).Where(t => t.FontSize == 15).ToList();
                Require(names.Count == 6 && names.All(t => t.Foreground.ToString()
                    == window.FindResource("TextBrush").ToString()), "project names inherit theme text color");
                foreach (var button in cards)
                {
                    var footer = (Grid)button.Parent;
                    var actions = Descendants<Button>(footer).ToList();
                    Require(actions.Count == 4 && actions.All(b => Math.Abs(b.ActualHeight - 36) < 1),
                        "card actions have uniform height");
                    var rects = actions.Select(b => Bounds(b, root)).ToList();
                    Require(rects.All(r => Math.Abs(r.Top - rects[0].Top) < 1)
                        && rects.Zip(rects.Skip(1)).All(pair => pair.First.Right <= pair.Second.Left + 1),
                        $"{theme}/{width}: footer buttons align without overlap");
                    Require(rects.Last().Right <= Bounds(footer, root).Right + 1, "actions fit their card");
                }
                var firstTwo = cards.Take(2).Select(b => Bounds(b, root)).ToList();
                Require(Math.Abs(firstTwo[0].Top - firstTwo[1].Top) < 1,
                    $"{theme}/{width}: cards wrap into multiple columns");
                Require(((FrameworkElement)window.FindName("LaunchDetailPanel")).Visibility
                    == (width < 1250 ? Visibility.Collapsed : Visibility.Visible), "auxiliary area responds to width");
            }
            CheckSelectionAndFilter(window, root, list);
            ((Button)window.FindName("ServicesNav")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Layout(root, 1440, 1000);
            Require(((ListView)window.FindName("ServicesGrid")).Items.Count > 0, "monitor remains a populated list");
            Render(root, Path.Combine(evidence, $"services-{theme}.png"));
            ((Button)window.FindName("SettingsNav")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Layout(root, 1080, 900);
            var disabled = (Button)window.FindName("ApplyUpdateButton");
            var label = (TextBlock)window.FindName("ApplyUpdateLabel");
            Require(!disabled.IsEnabled && label.Foreground.ToString() == disabled.Foreground.ToString(),
                "disabled primary label inherits readable foreground");
            Render(root, Path.Combine(evidence, $"settings-{theme}.png"));
            CheckEditor(window, theme, evidence);
            CheckOtherDialogs(window, theme, evidence);
            ((Button)window.FindName("LaunchpadNav")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        Require(new WindowInteropHelper(window).Handle == IntPtr.Zero, "no native desktop window created");
        Console.WriteLine($"Offscreen WPF layout/contrast passed. Evidence: {evidence}");
    }

    private static void CheckSelectionAndFilter(MainWindow window, FrameworkElement root, ListView list)
    {
        var selected = list.Items.OfType<AppSnapshot>().First();
        list.SelectedItems.Add(selected);
        Invoke(window, "RefreshCollections");
        Require(list.SelectedItems.OfType<AppSnapshot>().Any(a => a.Id == selected.Id),
            "polling preserves selection across grouped rebuilds");
        Layout(root, 1440, 1000);
        var item = (ListViewItem)list.ItemContainerGenerator.ContainerFromItem(selected);
        var card = Descendants<Border>(item).First(b => b.Height == 244);
        Require(card.BorderBrush.ToString() == window.FindResource("AccentBrush").ToString(),
            "selected card has a visible accent outline");
        var scroll = Descendants<ScrollViewer>(list).First();
        scroll.ScrollToEnd();
        Layout(root, 1440, 700);
        Require(scroll.VerticalOffset > 0 && Descendants<ScrollBar>(scroll)
            .Any(b => b.Orientation == Orientation.Vertical && b.Value > 0), "scrollbar follows grouped list scrolling");
        scroll.ScrollToHome();
        var search = (TextBox)window.FindName("SearchBox");
        search.Text = "每日备份";
        Require(list.Items.Count == 1 && list.Items[0] is AppSnapshot { Kind: "task" },
            "Chinese search filters grouped projects");
        search.Clear();
        selected.Name = new string('W', 100) + "中文服务";
        selected.Command = "dotnet run -- " + new string('x', 200);
        selected.Cwd = @"C:\Projects\" + new string('p', 200);
        Invoke(window, "RefreshCollections");
        Layout(root, 960, 1000);
        var toggle = Descendants<Button>(list).First(b => b.DataContext == selected && b.Width == 96);
        Require(Bounds(toggle, root).Right <= Bounds((Grid)toggle.Parent, root).Right,
            "long project text leaves actions inside their footer");
        var input = (TextBox)window.FindName("SearchBox");
        input.Text = "Atlas 中文 5180";
        Layout(root, 960, 1000);
        Require(!input.GetRectFromCharacterIndex(0).IsEmpty, "search input text has a rendered viewport");
        input.Clear();
        // Restore the virtual fixture for subsequent themes.
        selected.Name = "Atlas API";
        selected.Command = "dotnet run --project Atlas.Api";
        selected.Cwd = @"C:\Projects\Atlas";
        Invoke(window, "RefreshCollections");
        list.SelectedItems.Clear();
    }

    private static void CheckEditor(MainWindow window, string theme, string evidence)
    {
        var fixture = new AppSnapshot { Id = "fixture", Name = "后台服务", Kind = "service", Running = true };
        var editor = new ProjectEditor(null, fixture, "service", CancellationToken.None);
        var content = (FrameworkElement)editor.Content;
        foreach (var width in new[] { 540, 680 })
        {
            Layout(content, width, 770);
            var cancel = (Button)editor.FindName("CancelButton");
            var save = (Button)editor.FindName("SaveButton");
            var stop = (Button)editor.FindName("StopButton");
            var left = Bounds(cancel, content);
            var right = Bounds(save, content);
            Require(Math.Abs(left.Top - right.Top) < 1 && Math.Abs(left.Height - right.Height) < 1
                && Math.Abs(right.Left - left.Right - 8) < 1 && right.Right <= width - 24 + 1,
                "editor save/cancel share a baseline, spacing and right edge");
            Require(Bounds(stop, content).Right + 20 < left.Left, "danger action is separate from save");
            var label = Descendants<TextBlock>(save).First(t => t.Text == "保存项目");
            Require(label.Foreground.ToString() == "#FFFFFFFF", "primary button label is white");
            Render(content, Path.Combine(evidence, $"editor-{theme}-{width}.png"));
        }
        Require(new WindowInteropHelper(editor).Handle == IntPtr.Zero, "editor stays off desktop");
        editor.Close();
    }

    private static void CheckOtherDialogs(MainWindow window, string theme, string evidence)
    {
        var palette = new CommandPalette([new("启动台", "", () => { }), new("添加服务", "", () => { })]);
        var diagnostic = new DiagnosticWindow(new() { Name = "虚构服务" },
            new() { Summary = "没有运行中的受控进程", Issues = [new() { Title = "命令检查", Detail = "未发现运行记录", Fix = "检查执行命令" }] });
        foreach (var dialog in new Window[] { palette, diagnostic })
        {
            var content = (FrameworkElement)dialog.Content;
            Layout(content, 620, 480);
            Require(dialog.Foreground.ToString() == window.FindResource("TextBrush").ToString(),
                "secondary windows use the current text theme");
            var themeText = new[] { "TextBrush", "MutedTextBrush", "DimTextBrush", "AccentBrush" }
                .Select(key => window.FindResource(key).ToString()).ToHashSet();
            Require(Descendants<TextBlock>(content).All(t => themeText.Contains(t.Foreground.ToString())),
                "dialog labels inherit readable foreground: " + string.Join(", ", Descendants<TextBlock>(content)
                    .Where(t => !themeText.Contains(t.Foreground.ToString())).Select(t => $"{t.Text}:{t.Foreground}")));
            Render(content, Path.Combine(evidence, $"{dialog.GetType().Name}-{theme}.png"), dialog.Background);
            Require(new WindowInteropHelper(dialog).Handle == IntPtr.Zero, "secondary dialogs remain off desktop");
            dialog.Close();
        }
    }

    private static void CheckButtonStates(MainWindow window)
    {
        var button = new Button { Style = (Style)window.FindResource("AccentButton"), Content = "保存" };
        Layout(button, 100, 36);
        var background = (Border)button.Template.FindName("Root", button);
        Require(background.Effect is not null && Descendants<TextBlock>(button).All(t => t.Effect is null),
            "shadow stays on button surface and leaves text sharp");
        var hoverKey = (DependencyPropertyKey)typeof(UIElement).GetField("IsMouseOverPropertyKey",
            BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        button.SetValue(hoverKey, true);
        Require(background.Background.ToString() == window.FindResource("GlassHoverBrush").ToString(),
            "hover has a distinct surface");
        var pressedKey = (DependencyPropertyKey)typeof(ButtonBase).GetField("IsPressedPropertyKey",
            BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        button.SetValue(pressedKey, true);
        var surface = (Grid)button.Template.FindName("ButtonSurface", button);
        Require(surface.RenderTransform is TranslateTransform { Y: 1 } && background.Effect is null,
            "pressed button sinks and loses its shadow");
        button.SetValue(pressedKey, false);
        button.SetValue(hoverKey, false);
        button.SetValue(pressedKey, true);
        Require(button.Foreground.ToString() == window.FindResource("AccentStrongBrush").ToString(),
            "keyboard press keeps primary label readable without hover");
        button.SetValue(pressedKey, false);
        button.IsEnabled = false;
        Require(button.Opacity == 1 && background.Background.ToString()
            == window.FindResource("DisabledSurfaceBrush").ToString(), "disabled state uses an explicit surface");
        var check = new CheckBox { Content = "任务通知", IsChecked = true };
        Layout(check, 180, 24);
        Require(((TextBlock)check.Template.FindName("CheckMark", check)).Visibility == Visibility.Visible,
            "themed checkbox shows checked state");
        check.IsChecked = false;
        Require(((TextBlock)check.Template.FindName("CheckMark", check)).Visibility == Visibility.Collapsed,
            "themed checkbox shows unchecked state");
    }

    private static void CheckContrast(MainWindow window, string theme)
    {
        Color Brush(string key) => ((SolidColorBrush)window.FindResource(key)).Color;
        double Linear(byte value) => value / 255.0 <= .04045 ? value / 255.0 / 12.92
            : Math.Pow((value / 255.0 + .055) / 1.055, 2.4);
        double Luminance(Color color) => .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
        double Ratio(Color a, Color b) => (Math.Max(Luminance(a), Luminance(b)) + .05)
            / (Math.Min(Luminance(a), Luminance(b)) + .05);
        var minimum = double.MaxValue;
        foreach (var surface in new[] { "CanvasBrush", "InsetBrush", "InputBrush", "PopupBrush",
                     "GlassHoverBrush", "GlassPressedBrush", "AccentSoftBrush" })
        foreach (var text in new[] { "TextBrush", "MutedTextBrush", "DimTextBrush", "AccentBrush",
                     "InfoBrush", "WarningBrush", "DangerBrush" })
        {
            var ratio = Ratio(Brush(text), Brush(surface));
            minimum = Math.Min(minimum, ratio);
            Require(ratio >= 4.5, $"{theme}: {text} on {surface} contrast {ratio:0.00} must reach 4.5");
        }
        Require(Ratio(Colors.White, Brush("AccentFillBrush")) >= 4.5, "primary white label contrast");
        Require(Ratio(Brush("DisabledTextBrush"), Brush("DisabledSurfaceBrush")) >= 4.5,
            "disabled text remains legible");
        Console.WriteLine($"{theme} normal text minimum contrast: {minimum:0.00}:1");
    }

    private static void Layout(FrameworkElement root, double width, double height)
    {
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
    }

    private static void Render(FrameworkElement root, string path, Brush? background = null)
    {
        var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        if (background is not null)
        {
            var underlay = new DrawingVisual();
            using (var drawing = underlay.RenderOpen()) drawing.DrawRectangle(background, null, new Rect(root.RenderSize));
            bitmap.Render(underlay);
        }
        bitmap.Render(root);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        Require(pixels.Any(b => b != 0) && pixels.Distinct().Take(20).Count() == 20, "render is nonblank");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static Rect Bounds(FrameworkElement element, Visual root) =>
        element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static void Invoke(MainWindow window, string method, params object[] args) =>
        typeof(MainWindow).GetMethod(method, Private | BindingFlags.DeclaredOnly)!.Invoke(window, args);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
