using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Threading;

namespace RussiaOnlineOverlay;

public partial class MainWindow : Window
{
    private const int WM_HOTKEY = 0x0312;
    private const int MOD_NOREPEAT = 0x4000;
    private const uint VK_F8 = 0x77, VK_F9 = 0x78, VK_F10 = 0x79, VK_F11 = 0x7A;

    private readonly List<PanelWindow> _windows = new();
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly string _dataDir = Path.Combine(AppContext.BaseDirectory, "Data");
    private readonly string _positionsPath;
    private HwndSource? _source;
    private FileSystemWatcher? _watcher;
    private bool _moveMode, _panelsChanged, _lawsChanged, _profileChanged;
    private string _activeProfile = "main";
    private PanelWindow? _lawMemoWindow;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public MainWindow()
    {
        InitializeComponent();
        _positionsPath = Path.Combine(_dataDir, "overlay_positions.json");
        _refreshTimer.Tick += (_, _) =>
        {
            _refreshTimer.Stop();
            ProcessPendingRefresh();
        };
        Loaded += (_, _) =>
        {
            _activeProfile = ReadActiveProfile();
            LoadPanelsAndLaws();
            StartWatcher();
            var fade = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260));
            BeginAnimation(OpacityProperty, fade);
        };
        SourceInitialized += (_, _) => RegisterKeys();
        Closed += (_, _) =>
        {
            _watcher?.Dispose();
            foreach (var window in _windows.ToArray()) window.CloseWithoutSaving();
            if (_source is not null)
            {
                for (int i = 1; i <= 4; i++) UnregisterHotKey(_source.Handle, i);
                _source.RemoveHook(Hook);
            }
        };
    }

    private void StartWatcher()
    {
        try
        {
            Directory.CreateDirectory(_dataDir);
            _watcher = new FileSystemWatcher(_dataDir)
            {
                IncludeSubdirectories = true,
                Filter = "*",
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
            };
            _watcher.Changed += OnDataChanged;
            _watcher.Created += OnDataChanged;
            _watcher.Renamed += OnDataRenamed;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) { Status.Text = "Автообновление файлов недоступно: " + ex.Message; }
    }

    private void OnDataChanged(object sender, FileSystemEventArgs e) => QueueRefresh(e.FullPath);
    private void OnDataRenamed(object sender, RenamedEventArgs e) => QueueRefresh(e.FullPath);

    private void QueueRefresh(string path)
    {
        var name = Path.GetFileName(path);
        if (name.Equals("active_profile.txt", StringComparison.OrdinalIgnoreCase))
            _profileChanged = true;
        else if (name.Equals("laws_database.json", StringComparison.OrdinalIgnoreCase))
            _lawsChanged = true;
        else if (name.Equals("panels.json", StringComparison.OrdinalIgnoreCase))
            _panelsChanged = true;
        else
            return;

        try
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _refreshTimer.Stop();
                _refreshTimer.Start();
            }), DispatcherPriority.Background);
        }
        catch { }
    }

    private void ProcessPendingRefresh()
    {
        if (_profileChanged)
        {
            _profileChanged = false;
            var next = ReadActiveProfile();
            if (!string.Equals(next, _activeProfile, StringComparison.OrdinalIgnoreCase))
            {
                _activeProfile = next;
                CloseAllWindows();
                LoadPanelsAndLaws();
                return;
            }
        }
        if (_panelsChanged) { _panelsChanged = false; RefreshPanelWindows(); }
        if (_lawsChanged) { _lawsChanged = false; RefreshLawMemo(); }
    }

    private string ReadActiveProfile()
    {
        var path = Path.Combine(_dataDir, "active_profile.txt");
        if (!File.Exists(path)) return "main";
        var value = File.ReadAllText(path).Trim();
        return value is "main" or "deputy" or "personal" ? value : "main";
    }

    private string ProfileFolder => Path.Combine(_dataDir, "Profiles", _activeProfile);
    private string PanelsPath
    {
        get
        {
            var candidate = Path.Combine(ProfileFolder, "panels.json");
            return File.Exists(candidate) ? candidate : Path.Combine(_dataDir, "panels.json");
        }
    }

    private static string NormalizeCategory(string? value)
    {
        var key = (value ?? "").Trim();
        if (key.Equals("ук", StringComparison.OrdinalIgnoreCase)) return "УК";
        if (key.Equals("коап", StringComparison.OrdinalIgnoreCase)) return "КоАП";
        if (key.Equals("пго", StringComparison.OrdinalIgnoreCase)) return "ПГО";
        if (key.Equals("фз о фсо", StringComparison.OrdinalIgnoreCase)) return "ФЗ о ФСО";
        return key;
    }

    private static void MergeArticleArray(JsonObject bases, string category, JsonArray sourceArticles)
    {
        var targetKey = bases.Select(p => p.Key)
            .FirstOrDefault(k => NormalizeCategory(k) == NormalizeCategory(category)) ?? category;
        if (bases[targetKey] is not JsonArray target)
        {
            target = new JsonArray();
            bases[targetKey] = target;
        }

        foreach (var sourceNode in sourceArticles)
        {
            if (sourceNode is not JsonObject article) continue;
            var id = article["Id"]?.ToString()?.Trim() ?? "";
            var number = article["Number"]?.ToString()?.Trim() ?? "";
            JsonObject? existingArticle = null;

            if (id.Length > 0)
            {
                foreach (var otherCategory in bases)
                {
                    if (otherCategory.Value is not JsonArray otherArray) continue;
                    for (var i = 0; i < otherArray.Count; i++)
                    {
                        if (otherArray[i] is JsonObject existing &&
                            string.Equals(existing["Id"]?.ToString()?.Trim(), id, StringComparison.OrdinalIgnoreCase))
                        {
                            existingArticle = existing;
                            if (!string.Equals(otherCategory.Key, targetKey, StringComparison.OrdinalIgnoreCase))
                                otherArray.RemoveAt(i);
                            break;
                        }
                    }
                    if (existingArticle is not null) break;
                }
            }

            if (existingArticle is null && number.Length > 0)
            {
                var title = article["Title"]?.ToString()?.Trim() ?? "";
                var matches = new List<(JsonArray Array, int Index, JsonObject Article)>();
                foreach (var otherCategory in bases)
                {
                    if (otherCategory.Value is not JsonArray otherArray) continue;
                    for (var i = 0; i < otherArray.Count; i++)
                    {
                        if (otherArray[i] is JsonObject current &&
                            string.Equals(current["Number"]?.ToString()?.Trim(), number, StringComparison.OrdinalIgnoreCase) &&
                            (title.Length == 0 || string.Equals(current["Title"]?.ToString()?.Trim(), title, StringComparison.Ordinal)))
                            matches.Add((otherArray, i, current));
                    }
                }

                if (matches.Count == 1)
                {
                    var match = matches[0];
                    existingArticle = match.Article;
                    if (!ReferenceEquals(match.Array, target))
                        match.Array.RemoveAt(match.Index);
                }
                else if (matches.Count == 0)
                {
                    for (var i = 0; i < target.Count; i++)
                    {
                        if (target[i] is JsonObject existing &&
                            string.Equals(existing["Number"]?.ToString()?.Trim(), number, StringComparison.OrdinalIgnoreCase))
                        { existingArticle = existing; break; }
                    }
                }
            }

            var merged = existingArticle is null
                ? (JsonObject)article.DeepClone()
                : (JsonObject)existingArticle.DeepClone();
            foreach (var property in article)
                merged[property.Key] = property.Value?.DeepClone();

            var destinationIndex = -1;
            if (id.Length > 0)
            {
                for (var i = 0; i < target.Count; i++)
                {
                    if (target[i] is JsonObject existing &&
                        string.Equals(existing["Id"]?.ToString()?.Trim(), id, StringComparison.OrdinalIgnoreCase))
                    { destinationIndex = i; break; }
                }
            }
            if (destinationIndex >= 0) target[destinationIndex] = merged;
            else target.Add(merged);
        }
    }

    private JsonObject LoadMergedLawDatabase()
    {
        var rootPath = Path.Combine(_dataDir, "laws_database.json");
        var profilePath = Path.Combine(ProfileFolder, "laws_database.json");
        var root = File.Exists(rootPath)
            ? JsonNode.Parse(File.ReadAllText(rootPath)) as JsonObject ?? new JsonObject()
            : new JsonObject { ["Bases"] = new JsonObject() };
        var rootBases = root["Bases"] as JsonObject ?? new JsonObject();
        root["Bases"] = rootBases;

        // Extended Arbat source units are stored in compact per-category JSON files.
        var extendedDirectory = Path.Combine(_dataDir, "ExtendedLaws");
        if (Directory.Exists(extendedDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(extendedDirectory, "*.json").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                var extension = JsonNode.Parse(File.ReadAllText(file)) as JsonObject;
                var category = extension?["Category"]?.ToString();
                var articles = extension?["Articles"] as JsonArray;
                if (!string.IsNullOrWhiteSpace(category) && articles is not null)
                    MergeArticleArray(rootBases, category, articles);
            }
        }

        // Profile data overlays source records without deleting source metadata.
        if (File.Exists(profilePath))
        {
            var profile = JsonNode.Parse(File.ReadAllText(profilePath)) as JsonObject;
            var profileBases = profile?["Bases"] as JsonObject;
            if (profileBases is not null)
            {
                foreach (var category in profileBases)
                    if (category.Value is JsonArray articles)
                        MergeArticleArray(rootBases, category.Key, articles);
            }
        }

        return root;
    }

    private JsonObject? ReadPositions()
    {
        try
        {
            return File.Exists(_positionsPath)
                ? JsonNode.Parse(File.ReadAllText(_positionsPath)) as JsonObject
                : null;
        }
        catch { return null; }
    }

    private void ApplySavedPosition(PanelDefinition panel)
    {
        var saved = ReadPositions()?[panel.Id] as JsonObject;
        if (saved is null) return;
        panel.X = ReadDouble(saved, "X", panel.X);
        panel.Y = ReadDouble(saved, "Y", panel.Y);
        panel.Width = ReadDouble(saved, "Width", panel.Width);
        panel.Height = ReadDouble(saved, "Height", panel.Height);
    }

    private static double ReadDouble(JsonObject obj, string key, double fallback) =>
        double.TryParse(obj[key]?.ToString(), System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private void LoadPanelsAndLaws()
    {
        LoadPanelFile();
        RefreshLawMemo();
        Status.Text = $"Активный профиль: {_activeProfile}. Независимых окон: {_windows.Count}. Библиотека законов подключена.";
    }

    private void LoadPanelFile()
    {
        var path = PanelsPath;
        if (!File.Exists(path)) return;
        try
        {
            var array = JsonNode.Parse(File.ReadAllText(path)) as JsonArray;
            if (array is null) throw new InvalidDataException("Неверный формат panels.json");
            foreach (var node in array)
            {
                if (node is not JsonObject obj || obj["Visible"]?.ToString() == "false") continue;
                var panel = PanelDefinition.Read(obj);
                ApplySavedPosition(panel);
                var window = new PanelWindow(panel, _positionsPath) { MoveMode = _moveMode };
                TrackWindow(window);
                window.Show();
            }
        }
        catch (Exception ex) { Status.Text = "Не удалось загрузить панели: " + ex.Message; }
    }

    private void RefreshPanelWindows()
    {
        var path = PanelsPath;
        if (!File.Exists(path))
        {
            foreach (var window in _windows.Where(w => !w.IsLawMemo).ToArray())
            {
                window.CloseWithoutSaving();
                _windows.Remove(window);
            }
            return;
        }

        try
        {
            var array = JsonNode.Parse(File.ReadAllText(path)) as JsonArray;
            if (array is null) return;
            var definitions = array.OfType<JsonObject>()
                .Where(o => o["Visible"]?.ToString() != "false")
                .Select(PanelDefinition.Read)
                .ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);

            foreach (var old in _windows.Where(w => !w.IsLawMemo).ToArray())
            {
                if (!definitions.ContainsKey(old.PanelId))
                {
                    old.CloseWithoutSaving();
                    _windows.Remove(old);
                }
            }

            foreach (var definition in definitions.Values)
            {
                var existing = _windows.FirstOrDefault(w => !w.IsLawMemo &&
                    string.Equals(w.PanelId, definition.Id, StringComparison.OrdinalIgnoreCase));
                if (existing is null)
                {
                    ApplySavedPosition(definition);
                    var window = new PanelWindow(definition, _positionsPath) { MoveMode = _moveMode };
                    TrackWindow(window);
                    window.Show();
                }
                else
                {
                    ApplySavedPosition(definition);
                    existing.UpdateFromDefinition(definition);
                    if (!existing.IsVisible) existing.Show();
                }
            }
        }
        catch (Exception ex) { Status.Text = "Ошибка обновления панелей: " + ex.Message; }
    }

    private void RefreshLawMemo()
    {
        try
        {
            var root = LoadMergedLawDatabase();
            var bases = root["Bases"] as JsonObject;
            if (bases is null)
            {
                Status.Text = "В базе законов нет раздела Bases.";
                return;
            }

            var flow = new FlowDocument { PagePadding = new Thickness(0), FontFamily = new System.Windows.Media.FontFamily("Segoe UI"), FontSize = 14 };
            flow.Background = System.Windows.Media.Brushes.Transparent;
            flow.Foreground = System.Windows.Media.Brushes.White;
            int count = 0;

            foreach (var category in bases)
            {
                if (category.Value is not JsonArray articles) continue;
                var nonEmpty = articles.OfType<JsonObject>()
                    .Where(article => !(bool.TryParse(article["InMemo"]?.ToString(), out var include) && !include))
                    .ToList();
                if (nonEmpty.Count == 0) continue;

                flow.Blocks.Add(new Paragraph(new Run("━━ " + NormalizeCategory(category.Key).ToUpperInvariant() + " ━━"))
                {
                    FontSize = 17,
                    FontWeight = FontWeights.Bold,
                    Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(230, 194, 135)),
                    Margin = new Thickness(0, 10, 0, 9)
                });

                foreach (var article in nonEmpty)
                {
                    var number = article["Number"]?.ToString() ?? "";
                    var title = article["Title"]?.ToString() ?? "";
                    var body = article["Text"]?.ToString() ?? "";
                    var note = article["Note"]?.ToString() ?? "";
                    var heading = string.Join(" — ", new[] { number, title }.Where(x => !string.IsNullOrWhiteSpace(x)));
                    var rtf = article["RichTextRtf"]?.ToString() ?? "";

                    if (!string.IsNullOrWhiteSpace(heading))
                        flow.Blocks.Add(new Paragraph(new Run(heading))
                        {
                            FontWeight = FontWeights.SemiBold,
                            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(235, 220, 190)),
                            Margin = new Thickness(0, 7, 0, 3)
                        });

                    if (!string.IsNullOrWhiteSpace(rtf))
                    {
                        try
                        {
                            var articleDocument = new FlowDocument { PagePadding = new Thickness(0) };
                            var range = new TextRange(articleDocument.ContentStart, articleDocument.ContentEnd);
                            using var input = new MemoryStream(Encoding.UTF8.GetBytes(rtf));
                            range.Load(input, DataFormats.Rtf);
                            foreach (var block in articleDocument.Blocks.ToArray())
                            {
                                articleDocument.Blocks.Remove(block);
                                flow.Blocks.Add(block);
                            }
                        }
                        catch
                        {
                            if (!string.IsNullOrWhiteSpace(body))
                                flow.Blocks.Add(new Paragraph(new Run(body)) { Margin = new Thickness(0, 0, 0, 5) });
                        }
                    }
                    else if (!string.IsNullOrWhiteSpace(body))
                    {
                        foreach (var line in body.Replace("\r", "").Split('\n'))
                            flow.Blocks.Add(new Paragraph(new Run(line)) { Margin = new Thickness(0, 0, 0, 3) });
                    }

                    if (!string.IsNullOrWhiteSpace(note))
                        flow.Blocks.Add(new Paragraph(new Run("Заметка: " + note))
                        {
                            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(154, 183, 160)),
                            FontStyle = FontStyles.Italic,
                            Margin = new Thickness(0, 2, 0, 7)
                        });
                    count++;
                }
            }

            if (count == 0)
            {
                if (_lawMemoWindow is not null)
                {
                    _lawMemoWindow.CloseWithoutSaving();
                    _windows.Remove(_lawMemoWindow);
                    _lawMemoWindow = null;
                }
                Status.Text = "В базе пока нет статей.";
                return;
            }

            var rangeToSave = new TextRange(flow.ContentStart, flow.ContentEnd);
            using var output = new MemoryStream();
            rangeToSave.Save(output, DataFormats.Rtf);

            var memo = new PanelDefinition
            {
                Id = "LAW-MEMO-" + _activeProfile,
                Title = $"ПАМЯТКА ЗАКОНОВ · {count} СТАТЕЙ",
                Text = new TextRange(flow.ContentStart, flow.ContentEnd).Text,
                RichTextRtf = Encoding.UTF8.GetString(output.ToArray()),
                Width = 640, Height = 760, X = 500, Y = 80, Opacity = .97, Scale = 1, Topmost = true
            };

            if (_lawMemoWindow is null)
            {
                ApplySavedPosition(memo);
                _lawMemoWindow = new PanelWindow(memo, _positionsPath, isLawMemo: true) { MoveMode = _moveMode };
                TrackWindow(_lawMemoWindow);
                _lawMemoWindow.Show();
            }
            else
            {
                ApplySavedPosition(memo);
                _lawMemoWindow.UpdateFromDefinition(memo);
                if (!_lawMemoWindow.IsVisible) _lawMemoWindow.Show();
            }

            Status.Text = $"Памятка законов обновлена автоматически: {count} статей.";
        }
        catch (Exception ex)
        {
            Status.Text = "Ошибка формирования памятки законов: " + ex.Message;
        }
    }

    private void TrackWindow(PanelWindow window)
    {
        if (!_windows.Contains(window)) _windows.Add(window);
        window.Closed += (_, _) =>
        {
            _windows.Remove(window);
            if (ReferenceEquals(_lawMemoWindow, window)) _lawMemoWindow = null;
        };
    }

    private void CloseAllWindows()
    {
        foreach (var window in _windows.ToArray()) window.CloseWithoutSaving();
        _windows.Clear();
        _lawMemoWindow = null;
    }

    private void RegisterKeys()
    {
        var handle = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(handle);
        _source.AddHook(Hook);
        RegisterHotKey(handle, 1, MOD_NOREPEAT, VK_F8);
        RegisterHotKey(handle, 2, MOD_NOREPEAT, VK_F9);
        RegisterHotKey(handle, 3, MOD_NOREPEAT, VK_F10);
        RegisterHotKey(handle, 4, MOD_NOREPEAT, VK_F11);
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            switch (wParam.ToInt32())
            {
                case 1: Application.Current.Shutdown(); break;
                case 2: ToggleAll(); break;
                case 3: if (IsVisible) Hide(); else { Show(); WindowState = WindowState.Normal; Activate(); } break;
                case 4:
                    _moveMode = !_moveMode;
                    foreach (var window in _windows) window.MoveMode = _moveMode;
                    Status.Text = _moveMode
                        ? "F11: свободное перемещение включено; окна также можно двигать за заголовок."
                        : "F11 выключен; перетаскивание за заголовок доступно всегда.";
                    break;
            }
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void ToggleAll()
    {
        var shouldShow = _windows.Any(w => !w.IsVisible);
        foreach (var window in _windows)
        {
            if (shouldShow) window.Show();
            else window.Hide();
        }
    }

    private void Show_Click(object sender, RoutedEventArgs e) { foreach (var window in _windows) window.Show(); }
    private void Hide_Click(object sender, RoutedEventArgs e) { foreach (var window in _windows) window.Hide(); }
    private void Exit_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();
}

public sealed class PanelDefinition
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Text { get; set; } = "";
    public string RichTextRtf { get; set; } = "";
    public double Width { get; set; } = 360;
    public double Height { get; set; } = 260;
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
        RichTextRtf = o["RichTextRtf"]?.ToString() ?? "",
        Width = N(o, "Width", 360),
        Height = N(o, "Height", 260),
        Scale = N(o, "Scale", 1),
        X = N(o, "X", 80),
        Y = N(o, "Y", 100),
        Opacity = N(o, "Opacity", .94),
        Topmost = o["Topmost"]?.ToString() != "false"
    };

    private static double N(JsonObject o, string key, double fallback) =>
        double.TryParse(o[key]?.ToString(), System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : fallback;
}