using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Interop;

namespace RussiaOnlineOverlay;

public partial class MainWindow : Window
{
    private const int WM_HOTKEY = 0x0312;
    private const int MOD_NOREPEAT = 0x4000;
    private const uint VK_F8 = 0x77, VK_F9 = 0x78, VK_F10 = 0x79, VK_F11 = 0x7A;
    private readonly List<PanelWindow> _windows = new();
    private HwndSource? _source;
    private bool _moveMode;
    private readonly string _dataDir = Path.Combine(AppContext.BaseDirectory, "Data");

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadPanels();
        SourceInitialized += (_, _) => RegisterKeys();
        Closed += (_, _) =>
        {
            foreach (var w in _windows) w.Close();
            if (_source is not null)
            {
                for (int i = 1; i <= 4; i++) UnregisterHotKey(_source.Handle, i);
                _source.RemoveHook(Hook);
            }
        };
    }

    private void RegisterKeys()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(hwnd);
        _source.AddHook(Hook);
        RegisterHotKey(hwnd, 1, MOD_NOREPEAT, VK_F8);
        RegisterHotKey(hwnd, 2, MOD_NOREPEAT, VK_F9);
        RegisterHotKey(hwnd, 3, MOD_NOREPEAT, VK_F10);
        RegisterHotKey(hwnd, 4, MOD_NOREPEAT, VK_F11);
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            switch (wParam.ToInt32())
            {
                case 1: ExitOverlay(); break;
                case 2: ToggleAll(); break;
                case 3: ShowControl(); break;
                case 4:
                    _moveMode = !_moveMode;
                    foreach (var w in _windows) w.MoveMode = _moveMode;
                    Status.Text = _moveMode ? "Режим перемещения включён (F11)" : "Режим перемещения выключен";
                    break;
            }
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void LoadPanels()
    {
        try
        {
            var profile = "main";
            var profilePath = Path.Combine(_dataDir, "active_profile.txt");
            if (File.Exists(profilePath))
            {
                var value = File.ReadAllText(profilePath).Trim();
                if (value is "main" or "deputy" or "personal") profile = value;
            }
            var path = Path.Combine(_dataDir, "Profiles", profile, "panels.json");
            if (!File.Exists(path)) path = Path.Combine(_dataDir, "panels.json");
            if (!File.Exists(path))
            {
                Status.Text = "Data/panels.json не найден. Создай панели в Studio и сохрани их.";
                return;
            }
            var arr = JsonNode.Parse(File.ReadAllText(path)) as JsonArray;
            if (arr is null) { Status.Text = "Ошибка формата panels.json"; return; }
            foreach (var n in arr)
            {
                if (n is not JsonObject o || o["Visible"]?.GetValue<bool>() == false) continue;
                var p = PanelDefinition.Read(o);
                var w = new PanelWindow(p);
                _windows.Add(w);
                w.Show();
            }
            Status.Text = $"Загружено независимых окон: {_windows.Count}. F8 — выход, F9 — показать/скрыть, F10 — панель, F11 — двигать окна.";
        }
        catch (Exception ex) { Status.Text = "Ошибка загрузки: " + ex.Message; }
    }

    private void ToggleAll()
    {
        bool show = _windows.Any(w => !w.IsVisible);
        foreach (var w in _windows) { if (show) w.Show(); else w.Hide(); }
    }

    private void ShowControl()
    {
        if (IsVisible) Hide();
        else { Show(); WindowState = WindowState.Normal; Activate(); }
    }

    private void Show_Click(object sender, RoutedEventArgs e) { foreach (var w in _windows) w.Show(); }
    private void Hide_Click(object sender, RoutedEventArgs e) { foreach (var w in _windows) w.Hide(); }
    private void Exit_Click(object sender, RoutedEventArgs e) => ExitOverlay();
    private void ExitOverlay() => Application.Current.Shutdown();
}

public sealed class PanelDefinition
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Text { get; set; } = "";
    public double Width { get; set; } = 360;
    public double Scale { get; set; } = 1;
    public double X { get; set; } = 80;
    public double Y { get; set; } = 100;
    public double Opacity { get; set; } = .94;
    public bool Topmost { get; set; } = true;

    public static PanelDefinition Read(JsonObject o) => new()
    {
        Id = o["Id"]?.ToString() ?? "",
        Title = o["Title"]?.ToString() ?? "Памятка",
        Text = o["Text"]?.ToString() ?? o["Body"]?.ToString() ?? "",
        Width = N(o, "Width", 360),
        Scale = N(o, "Scale", 1),
        X = N(o, "X", 80),
        Y = N(o, "Y", 100),
        Opacity = N(o, "Opacity", .94),
        Topmost = o["Topmost"]?.GetValue<bool>() ?? true
    };

    private static double N(JsonObject o, string k, double d) =>
        double.TryParse(o[k]?.ToString(), System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : d;
}