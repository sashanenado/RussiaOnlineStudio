using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace RussiaOnlineOverlay;

public sealed class PanelWindow : Window
{
    private readonly PanelDefinition _panel;
    private readonly string? _sourcePath;
    private readonly DispatcherTimer _saveTimer;
    private bool _isClosing;
    public bool MoveMode { get; set; }

    public PanelWindow(PanelDefinition panel, string? sourcePath = null)
    {
        _panel = panel;
        _sourcePath = sourcePath;
        Title = panel.Title;
        Width = Math.Clamp(panel.Width, 220, 1200);
        Height = Math.Clamp(panel.Height, 160, 1000);
        Left = panel.X;
        Top = panel.Y;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromArgb((byte)(Math.Clamp(panel.Opacity, .2, 1) * 255), 18, 23, 30));
        Topmost = panel.Topmost;
        ShowInTaskbar = false;

        var root = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromArgb(210, 201, 166, 107)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12),
            Background = Background
        };
        var layout = new DockPanel();

        var header = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(25, 201, 166, 107)),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(7, 4, 7, 7),
            Cursor = Cursors.SizeAll,
            Child = new TextBlock
            {
                Text = panel.Title,
                FontWeight = FontWeights.Bold,
                FontSize = 15 * panel.Scale,
                Foreground = new SolidColorBrush(Color.FromRgb(235, 204, 151)),
                TextTrimming = TextTrimming.CharacterEllipsis
            }
        };
        DockPanel.SetDock(header, Dock.Top);
        layout.Children.Add(header);
        header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                try { DragMove(); } catch { }
                e.Handled = true;
            }
        };

        var body = new TextBlock
        {
            Text = panel.Text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14 * panel.Scale,
            Foreground = Brushes.White
        };
        var scroll = new ScrollViewer
        {
            Content = body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        layout.Children.Add(scroll);
        root.Child = layout;
        Content = root;

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SavePosition();
        };
        LocationChanged += (_, _) => ScheduleSave();
        SizeChanged += (_, _) => ScheduleSave();
        Closing += (_, _) =>
        {
            _isClosing = true;
            _saveTimer.Stop();
            SavePosition();
        };
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (MoveMode && e.OriginalSource is not DependencyObject source)
                return;
            if (MoveMode && e.OriginalSource is DependencyObject d &&
                FindAncestor<ScrollViewer>(d) is null &&
                e.OriginalSource is not TextBlock)
            {
                try { DragMove(); } catch { }
                e.Handled = true;
            }
        };
    }

    private void ScheduleSave()
    {
        if (_isClosing || string.IsNullOrWhiteSpace(_sourcePath)) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SavePosition()
    {
        if (string.IsNullOrWhiteSpace(_sourcePath) || !File.Exists(_sourcePath)) return;
        try
        {
            _panel.X = Left;
            _panel.Y = Top;
            _panel.Width = Width;
            _panel.Height = Height;
            var root = JsonNode.Parse(File.ReadAllText(_sourcePath)) as JsonArray;
            if (root is null) return;
            foreach (var node in root)
            {
                if (node is not JsonObject item ||
                    !string.Equals(item["Id"]?.ToString(), _panel.Id, StringComparison.OrdinalIgnoreCase))
                    continue;
                item["X"] = _panel.X;
                item["Y"] = _panel.Y;
                item["Width"] = _panel.Width;
                item["Height"] = _panel.Height;
                item["Opacity"] = _panel.Opacity;
                break;
            }
            var temp = _sourcePath + ".tmp";
            File.WriteAllText(temp, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, _sourcePath, true);
        }
        catch { }
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T target) return target;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }
}