using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Media.Animation;

namespace RussiaOnlineOverlay;

public sealed class PanelWindow : Window
{
    private static readonly object PositionLock = new();
    private readonly PanelDefinition _panel;
    private readonly string? _positionsPath;
    private readonly TextBlock _heading;
    private readonly RichTextBox _body;
    private readonly DispatcherTimer _positionTimer;
    private bool _isClosing, _skipSave;

    public bool MoveMode { get; set; }
    public string PanelId => _panel.Id;
    public bool IsLawMemo { get; }

    public PanelWindow(PanelDefinition panel, string? positionsPath = null, bool isLawMemo = false)
    {
        _panel = panel;
        _positionsPath = positionsPath;
        IsLawMemo = isLawMemo;

        Title = panel.Title;
        Width = Math.Clamp(panel.Width, 220, 1200);
        Height = Math.Clamp(panel.Height, 160, 1200);
        Left = panel.X;
        Top = panel.Y;
        WindowStyle = WindowStyle.None;
        ResizeMode = WindowResizeMode(panel);
        AllowsTransparency = true;
        Background = MakeBackground(panel.Opacity);
        Topmost = panel.Topmost;
        ShowInTaskbar = false;
        Opacity = 0;

        var root = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromArgb(220, 201, 166, 107)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(11),
            Padding = new Thickness(12),
            Background = Background
        };
        var layout = new DockPanel();

        _heading = new TextBlock
        {
            Text = panel.Title,
            FontWeight = FontWeights.Bold,
            FontSize = 15 * panel.Scale,
            Foreground = new SolidColorBrush(Color.FromRgb(238, 207, 157)),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var header = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(32, 201, 166, 107)),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(8, 6, 8, 8),
            Cursor = Cursors.SizeAll,
            Child = _heading
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

        _body = new RichTextBox
        {
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = Brushes.White,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 14 * panel.Scale,
            Padding = new Thickness(2),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            IsDocumentEnabled = false
        };
        _body.Document.PagePadding = new Thickness(0);
        _body.Document.Background = Brushes.Transparent;
        _body.Document.Foreground = Brushes.White;
        LoadBody(panel);
        layout.Children.Add(_body);

        root.Child = layout;
        Content = root;

        _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _positionTimer.Tick += (_, _) =>
        {
            _positionTimer.Stop();
            SavePosition();
        };
        LocationChanged += (_, _) => SchedulePositionSave();
        SizeChanged += (_, _) => SchedulePositionSave();
        Loaded += (_, _) =>
        {
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });
        };
        Closing += (_, _) =>
        {
            _isClosing = true;
            _positionTimer.Stop();
            if (!_skipSave) SavePosition();
        };

        PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (!MoveMode || e.OriginalSource is not DependencyObject source) return;
            if (FindAncestor<ScrollViewer>(source) is null)
            {
                try { DragMove(); } catch { }
                e.Handled = true;
            }
        };
    }

    private static ResizeMode WindowResizeMode(PanelDefinition panel) => ResizeMode.CanResizeWithGrip;

    private static Brush MakeBackground(double opacity) =>
        new SolidColorBrush(Color.FromArgb((byte)(Math.Clamp(opacity, .2, 1) * 255), 18, 23, 30));

    private void LoadBody(PanelDefinition panel)
    {
        _body.Document.Blocks.Clear();
        var range = new TextRange(_body.Document.ContentStart, _body.Document.ContentEnd);
        if (!string.IsNullOrWhiteSpace(panel.RichTextRtf))
        {
            try
            {
                using var ms = new MemoryStream(Encoding.UTF8.GetBytes(panel.RichTextRtf));
                range.Load(ms, DataFormats.Rtf);
                return;
            }
            catch { _body.Document.Blocks.Clear(); }
        }

        var text = panel.Text ?? "";
        foreach (var line in text.Replace("\r", "").Split('\n'))
            _body.Document.Blocks.Add(new Paragraph(new Run(line)) { Margin = new Thickness(0, 0, 0, 5) });
    }

    public void UpdateFromDefinition(PanelDefinition panel)
    {
        _panel.Title = panel.Title;
        _panel.Text = panel.Text;
        _panel.RichTextRtf = panel.RichTextRtf;
        _panel.Width = panel.Width;
        _panel.Height = panel.Height;
        _panel.Scale = panel.Scale;
        _panel.Opacity = panel.Opacity;
        _panel.Topmost = panel.Topmost;

        Title = panel.Title;
        _heading.Text = panel.Title;
        _heading.FontSize = 15 * panel.Scale;
        _body.FontSize = 14 * panel.Scale;
        Width = Math.Clamp(panel.Width, 220, 1200);
        Height = Math.Clamp(panel.Height, 160, 1200);
        Left = panel.X;
        Top = panel.Y;
        Topmost = panel.Topmost;
        Background = MakeBackground(panel.Opacity);
        if (Content is Border border) border.Background = Background;
        LoadBody(panel);
    }

    public void CloseWithoutSaving()
    {
        _skipSave = true;
        Close();
    }

    private void SchedulePositionSave()
    {
        if (_isClosing || _skipSave || string.IsNullOrWhiteSpace(_positionsPath)) return;
        _positionTimer.Stop();
        _positionTimer.Start();
    }

    private void SavePosition()
    {
        if (_skipSave || string.IsNullOrWhiteSpace(_positionsPath)) return;
        lock (PositionLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_positionsPath)!);
                JsonObject positions = new();
                if (File.Exists(_positionsPath))
                    positions = JsonNode.Parse(File.ReadAllText(_positionsPath)) as JsonObject ?? new JsonObject();

                positions[_panel.Id] = new JsonObject
                {
                    ["X"] = Left,
                    ["Y"] = Top,
                    ["Width"] = Width,
                    ["Height"] = Height
                };

                var temp = _positionsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temp, positions.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temp, _positionsPath, true);

                _panel.X = Left;
                _panel.Y = Top;
                _panel.Width = Width;
                _panel.Height = Height;
            }
            catch { }
        }
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T found) return found;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }
}