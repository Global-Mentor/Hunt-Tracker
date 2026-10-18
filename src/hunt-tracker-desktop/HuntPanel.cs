using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HuntTracker.Desktop;

public sealed class HuntPanel : Border
{
    public static readonly DependencyProperty TextureProperty = DependencyProperty.RegisterAttached(
        nameof(Texture), typeof(BitmapSource), typeof(HuntPanel),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, TextureChanged));

    private ImageBrush[]? slices;
    private const double Edge = 4;

    public BitmapSource? Texture
    {
        get => (BitmapSource?)GetValue(TextureProperty);
        set => SetValue(TextureProperty, value);
    }

    public static BitmapSource? GetTexture(DependencyObject element) => (BitmapSource?)element.GetValue(TextureProperty);

    public static void SetTexture(DependencyObject element, BitmapSource? value) => element.SetValue(TextureProperty, value);

    private static void TextureChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not HuntPanel panel)
            return;
        panel.slices = null;
        if (args.NewValue is not BitmapSource source || source.PixelWidth <= Edge * 2 || source.PixelHeight <= Edge * 2)
            return;

        double[] x = [0, Edge, source.PixelWidth - Edge, source.PixelWidth];
        double[] y = [0, Edge, source.PixelHeight - Edge, source.PixelHeight];
        panel.slices = new ImageBrush[9];
        for (var row = 0; row < 3; row++)
            for (var column = 0; column < 3; column++)
            {
                var brush = new ImageBrush(source)
                {
                    ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
                    Viewbox = new Rect(x[column] / source.PixelWidth, y[row] / source.PixelHeight,
                        (x[column + 1] - x[column]) / source.PixelWidth,
                        (y[row + 1] - y[row]) / source.PixelHeight),
                    Stretch = row == 1 && column == 1 ? Stretch.UniformToFill : Stretch.Fill
                };
                brush.Freeze();
                panel.slices[row * 3 + column] = brush;
            }
    }

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        if (slices is null || ActualWidth <= Edge * 2 || ActualHeight <= Edge * 2)
            return;

        double[] x = [0, Edge, ActualWidth - Edge, ActualWidth];
        double[] y = [0, Edge, ActualHeight - Edge, ActualHeight];
        for (var row = 0; row < 3; row++)
            for (var column = 0; column < 3; column++)
                context.DrawRectangle(slices[row * 3 + column], null,
                    new Rect(x[column], y[row], x[column + 1] - x[column], y[row + 1] - y[row]));
    }
}
