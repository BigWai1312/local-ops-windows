using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Xml.Linq;

namespace LocalOpsWindows.Preview;

// Render the repository's Lucide assets with native WPF geometries, without a web runtime.
public sealed class LucideIcon : FrameworkElement
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(string), typeof(LucideIcon), new FrameworkPropertyMetadata("server", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(typeof(LucideIcon), new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly Dictionary<string, Geometry[]> Shapes = [];
    public string Kind { get => (string)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        var pen = new Pen(Foreground, 1.8) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        drawing.PushTransform(new ScaleTransform(ActualWidth / 24, ActualHeight / 24));
        foreach (var shape in Load(Kind)) drawing.DrawGeometry(null, pen, shape);
        drawing.Pop();
    }

    internal static Geometry[] Load(string name)
    {
        if (Shapes.TryGetValue(name, out var cached)) return cached;
        var file = Path.Combine(AppContext.BaseDirectory, "Assets", "Icons", name + ".svg");
        if (!File.Exists(file)) throw new FileNotFoundException("Missing local Lucide asset", file);
        using var stream = File.OpenRead(file);
        var document = XDocument.Load(stream);
        var geometries = new List<Geometry>();
        foreach (var node in document.Root!.Elements())
        {
            double N(string key, double fallback = 0) => node.Attribute(key) is { } attribute ? double.Parse(attribute.Value, CultureInfo.InvariantCulture) : fallback;
            var shape = node.Name.LocalName switch
            {
                "path" => Geometry.Parse(node.Attribute("d")!.Value),
                "rect" => new RectangleGeometry(new Rect(N("x"), N("y"), N("width"), N("height")), N("rx"), N("ry", N("rx"))),
                "circle" => new EllipseGeometry(new Point(N("cx"), N("cy")), N("r"), N("r")),
                "ellipse" => new EllipseGeometry(new Point(N("cx"), N("cy")), N("rx"), N("ry")),
                "line" => new LineGeometry(new Point(N("x1"), N("y1")), new Point(N("x2"), N("y2"))),
                "polyline" or "polygon" => Geometry.Parse("M" + node.Attribute("points")!.Value + (node.Name.LocalName == "polygon" ? "Z" : "")),
                _ => throw new InvalidDataException($"Unsupported Lucide element: {node.Name}"),
            };
            shape.Freeze();
            geometries.Add(shape);
        }
        return Shapes[name] = geometries.ToArray();
    }
}
