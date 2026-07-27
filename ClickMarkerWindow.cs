using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace MacroFenetre;

internal sealed class ClickMarkerWindow : Window
{
    internal ClickMarkerWindow(double screenX, double screenY, string keyName, bool isExisting)
    {
        var accentColor = isExisting
            ? Color.FromRgb(91, 95, 239)
            : Color.FromRgb(239, 68, 68);

        Width = 112;
        Height = 46;
        Left = screenX - 21;
        Top = screenY - 21;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        IsHitTestVisible = false;

        var canvas = new Canvas { Width = 112, Height = 46, IsHitTestVisible = false };
        canvas.Children.Add(new Ellipse
        {
            Width = 36,
            Height = 36,
            Margin = new Thickness(3),
            Stroke = new SolidColorBrush(accentColor),
            StrokeThickness = 3,
            Fill = new SolidColorBrush(Color.FromArgb(45, accentColor.R, accentColor.G, accentColor.B))
        });
        canvas.Children.Add(new Line
        {
            X1 = 21,
            Y1 = 10,
            X2 = 21,
            Y2 = 32,
            Stroke = Brushes.White,
            StrokeThickness = 2
        });
        canvas.Children.Add(new Line
        {
            X1 = 10,
            Y1 = 21,
            X2 = 32,
            Y2 = 21,
            Stroke = Brushes.White,
            StrokeThickness = 2
        });

        var label = new Border
        {
            Padding = new Thickness(8, 4, 8, 5),
            Background = new SolidColorBrush(Color.FromArgb(235, accentColor.R, accentColor.G, accentColor.B)),
            CornerRadius = new CornerRadius(6),
            Child = new TextBlock
            {
                Text = keyName,
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold,
                FontSize = 12
            }
        };
        Canvas.SetLeft(label, 43);
        Canvas.SetTop(label, 8);
        canvas.Children.Add(label);
        Content = canvas;
    }

    internal void ShowPersistent() => Show();

    internal void ShowBriefly()
    {
        Show();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Close();
        };
        timer.Start();
    }
}
