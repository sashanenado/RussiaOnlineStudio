using Microsoft.Web.WebView2.Core;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace RussiaOnlineStudio;

public partial class MainWindow : Window
{
    private readonly string _dataDir;
    private JsonObject _lawRoot = new();
    private JsonObject _bases = new();
    private readonly ObservableCollection<LawRow> _laws = new();
    private readonly ObservableCollection<PanelRow> _panels = new();
    private LawRow? _selectedLaw;
    private PanelRow? _selectedPanel;
    private bool _loading;
    private bool _forumInitialized;
    private Task? _forumInitTask;
    private readonly DispatcherTimer _lawSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private readonly DispatcherTimer _panelSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static readonly string[] DefaultCategories =
        ["УК", "КоАП", "ПК", "ПГО", "ФЗ о ФСО", "О территориях"];

    public MainWindow()
    {
        InitializeComponent();
        _dataDir = FindDataDirectory();
        Directory.CreateDirectory(_dataDir);
        DataPath.Text = _dataDir;

        _lawSaveTimer.Tick += (_, _) =>
        {
            _lawSaveTimer.Stop();
            if (_loading || _selectedLaw is null) return;
            RunSafe(() =>
            {
                ApplyLawEditor();
                SaveLaws();
                RefreshLawFilter();
            }, "Автосохранение статьи");
        };
        _panelSaveTimer.Tick += (_, _) =>
        {
            _panelSaveTimer.Stop();
            if (_loading || _selectedPanel is null) return;
            RunSafe(() =>
            {
                ApplyPanelEditor();
                SavePanels();
                PanelList.Items.Refresh();
                UpdatePreview();
            }, "Автосохранение панели");
        };

        LawList.ItemsSource = _laws;
        PanelList.ItemsSource = _panels;
        _loading = true;
        ProfileBox.SelectedIndex = ReadActiveProfileKey() switch
        {
            "deputy" => 1,
            "personal" => 2,
            _ => 0
        };
        _loading = false;
        LoadAll();

        Closing += (_, _) =>
        {
            _lawSaveTimer.Stop();
            _panelSaveTimer.Stop();
            RunSafe(() =>
            {
                if (_selectedLaw is not null) { ApplyLawEditor(); SaveLaws(); }
                if (_selectedPanel is not null) { ApplyPanelEditor(); SavePanels(); }
            }, "Сохранение при закрытии");
        };
    }

    private static string FindDataDirectory()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "Data");
        if (Directory.Exists(beside)) return beside;
        return beside;
    }

    private string ActiveProfileKey => ProfileBox.SelectedIndex switch
    {
        1 => "deputy",
        2 => "personal",
        _ => "main"
    };
    private string ProfileFolder => Path.Combine(_dataDir, "Profiles", ActiveProfileKey);
    private string LawsPath => Path.Combine(ProfileFolder, "laws_database.json");
    private string PanelsPath => Path.Combine(ProfileFolder, "panels.json");

    private string ReadActiveProfileKey()
    {
        var path = Path.Combine(_dataDir, "active_profile.txt");
        if (!File.Exists(path)) return "main";
        var value = File.ReadAllText(path).Trim();
        return value is "main" or "deputy" or "personal" ? value : "main";
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        BeginAnimation(OpacityProperty, fade);
    }

    private void LoadAll()
    {
        Directory.CreateDirectory(ProfileFolder);
        _loading = true;
        try
        {
            var bundled = LoadBundledOrDefault();
            JsonObject profileDb = new();
            if (File.Exists(LawsPath))
                profileDb = JsonNode.Parse(File.ReadAllText(LawsPath)) as JsonObject ?? new JsonObject();

            _lawRoot = MergeLawDatabases(bundled, profileDb);
            _bases = _lawRoot["Bases"] as JsonObject ?? new JsonObject();
            _lawRoot["Bases"] = _bases;

            LoadLawRows();
            LoadPanels();
            UiScale.Text = ReadSetting("UiScale", "125");
            GameScale.Text = ReadSetting("GameScale", "0.6");
            StatusText.Text = $"Данные загружены • статей: {_laws.Count} • профиль: {ProfileBox.Text}";
        }
        catch (Exception ex)
        {
            LogError(ex, "LoadAll");
            MessageBox.Show(this,
                "Не удалось загрузить базу данных. Исходные файлы не удалены. Подробности: Data\\logs\\errors.log\n\n" + ex.Message,
                "Russia Online Studio", MessageBoxButton.OK, MessageBoxImage.Error);
            _lawRoot = new JsonObject { ["Bases"] = new JsonObject() };
            _bases = (JsonObject)_lawRoot["Bases"]!;
            LoadLawRows();
            LoadPanels();
        }
        finally { _loading = false; }
        UpdateStats();
        RefreshLawFilter();
    }

    private JsonObject LoadBundledOrDefault()
    {
        var bundled = Path.Combine(_dataDir, "laws_database.json");
        if (!File.Exists(bundled))
            return new JsonObject { ["Bases"] = new JsonObject() };
        var parsed = JsonNode.Parse(File.ReadAllText(bundled)) as JsonObject;
        if (parsed?["Bases"] is not JsonObject)
            throw new InvalidDataException("Data/laws_database.json не содержит объект Bases.");
        return parsed;
    }

    private static JsonObject MergeLawDatabases(JsonObject baseDb, JsonObject profileDb)
    {
        var merged = (JsonObject)baseDb.DeepClone();
        var targetBases = merged["Bases"] as JsonObject ?? new JsonObject();
        merged["Bases"] = targetBases;
        var sourceBases = profileDb["Bases"] as JsonObject;
        if (sourceBases is null) return merged;

        foreach (var sourceCategory in sourceBases)
        {
            if (sourceCategory.Value is not JsonArray sourceArray) continue;
            var targetKey = targetBases.Select(p => p.Key)
                .FirstOrDefault(k => NormalizeCategory(k) == NormalizeCategory(sourceCategory.Key))
                ?? sourceCategory.Key;
            if (targetBases[targetKey] is not JsonArray targetArray)
            {
                targetArray = new JsonArray();
                targetBases[targetKey] = targetArray;
            }

            foreach (var sourceNode in sourceArray)
            {
                if (sourceNode is not JsonObject sourceArticle) continue;
                var number = sourceArticle["Number"]?.ToString()?.Trim() ?? "";
                var title = sourceArticle["Title"]?.ToString()?.Trim() ?? "";
                var index = -1;
                if (!string.IsNullOrWhiteSpace(number))
                {
                    for (var i = 0; i < targetArray.Count; i++)
                    {
                        if (targetArray[i] is JsonObject current &&
                            string.Equals(current["Number"]?.ToString()?.Trim(), number, StringComparison.OrdinalIgnoreCase))
                        { index = i; break; }
                    }
                }
                else if (!string.IsNullOrWhiteSpace(title))
                {
                    for (var i = 0; i < targetArray.Count; i++)
                    {
                        if (targetArray[i] is JsonObject current &&
                            string.Equals(current["Title"]?.ToString()?.Trim(), title, StringComparison.OrdinalIgnoreCase))
                        { index = i; break; }
                    }
                }

                if (index >= 0) targetArray[index] = sourceArticle.DeepClone();
                else targetArray.Add(sourceArticle.DeepClone());
            }
        }
        return merged;
    }

    private static string NormalizeCategory(string? value)
    {
        var key = (value ?? "").Trim();
        if (key.Equals("ук", StringComparison.OrdinalIgnoreCase)) return "УК";
        if (key.Equals("коап", StringComparison.OrdinalIgnoreCase)) return "КоАП";
        if (key.Equals("пго", StringComparison.OrdinalIgnoreCase)) return "ПГО";
        if (key.Equals("фз о фсо", StringComparison.OrdinalIgnoreCase)) return "ФЗ о ФСО";
        if (key.Equals("о территориях", StringComparison.OrdinalIgnoreCase)) return "О территориях";
        return key;
    }

    private string FindStorageCategory(string displayCategory)
    {
        return _bases.Select(p => p.Key)
            .FirstOrDefault(k => NormalizeCategory(k).Equals(NormalizeCategory(displayCategory), StringComparison.OrdinalIgnoreCase))
            ?? displayCategory;
    }

    private JsonArray GetCategoryArray(string displayCategory)
    {
        var key = FindStorageCategory(displayCategory);
        if (_bases[key] is JsonArray existing) return existing;
        var array = new JsonArray();
        _bases[key] = array;
        return array;
    }

    private void LoadLawRows()
    {
        _laws.Clear();
        foreach (var cat in _bases)
        {
            if (cat.Value is not JsonArray array) continue;
            foreach (var node in array)
                if (node is JsonObject obj)
                    _laws.Add(new LawRow(cat.Key, obj));
        }

        var categories = DefaultCategories
            .Concat(_bases.Select(p => NormalizeCategory(p.Key)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        CategoryBox.ItemsSource = new[] { "Все категории" }.Concat(categories).ToList();
        EditCategory.ItemsSource = categories;
        CategoryBox.SelectedItem = "Все категории";
        EditCategory.SelectedItem = "УК";
    }

    private void LoadPanels()
    {
        _panels.Clear();
        if (File.Exists(PanelsPath))
        {
            try
            {
                var arr = JsonNode.Parse(File.ReadAllText(PanelsPath)) as JsonArray;
                if (arr is not null)
                    foreach (var node in arr)
                        if (node is JsonObject obj) _panels.Add(PanelRow.FromJson(obj));
            }
            catch (Exception ex) { LogError(ex, "LoadPanels"); }
        }
        if (_panels.Count == 0)
            _panels.Add(new PanelRow
            {
                Id = Guid.NewGuid().ToString("N"), Title = "Порядок действий",
                Body = "Добавь собственные инструкции в этом окне.",
                Width = 360, Height = 260, Scale = 1, Visible = true
            });
        PanelList.SelectedIndex = _panels.Count > 0 ? 0 : -1;
        UpdatePreview();
    }

    private void RefreshLawFilter()
    {
        var selected = CategoryBox.SelectedItem?.ToString() ?? "Все категории";
        var query = SearchBox.Text?.Trim() ?? "";
        var onlyFavorites = false;
        var rows = _laws.Where(x =>
            (selected == "Все категории" || NormalizeCategory(x.Category).Equals(NormalizeCategory(selected), StringComparison.OrdinalIgnoreCase)) &&
            (!onlyFavorites || x.Favorite) &&
            (query.Length == 0 || string.Join(" ", x.Number, x.Title, x.Text).Contains(query, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var old = _selectedLaw;
        LawList.ItemsSource = rows;
        if (old is not null && rows.Contains(old)) LawList.SelectedItem = old;
    }

    private void SaveJson(string path, JsonNode node)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                node.WriteTo(writer);
                writer.Flush();
            }

            if (File.Exists(path))
            {
                var backup = path + ".backup_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff") + "_" + Guid.NewGuid().ToString("N")[..6];
                File.Copy(path, backup, false);
            }
            File.Move(temp, path, true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    private void WriteSignal(string fileName)
    {
        var path = Path.Combine(_dataDir, fileName);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture));
        File.Move(temp, path, true);
    }

    private void SaveLaws()
    {
        SaveJson(LawsPath, _lawRoot);
        WriteSignal("laws_revision.txt");
        StatusText.Text = $"База сохранена • {_laws.Count} статей • Overlay обновит памятку";
        UpdateStats();
    }

    private void SavePanels()
    {
        var arr = new JsonArray();
        foreach (var panel in _panels) arr.Add(panel.ToJson());
        SaveJson(PanelsPath, arr);
        WriteSignal("panels_revision.txt");
        StatusText.Text = "Панели сохранены • Overlay обновляет окна";
        UpdateStats();
        UpdatePreview();
    }

    private void UpdateStats()
    {
        // Overview dashboard removed: the library is now the primary workspace.
        // Keep this method as a no-op for callers from shared save routines.
    }

    private void FillLawEditor(LawRow? row)
    {
        _lawSaveTimer.Stop();
        _selectedLaw = row;
        _loading = true;
        try
        {
            EditCategory.SelectedItem = row?.Category ?? "УК";
            EditNumber.Text = row?.Number ?? "";
            EditTitle.Text = row?.Title ?? "";
            EditNote.Text = row?.Note ?? "";
            EditFavorite.IsChecked = row?.Favorite ?? false;
            var range = new TextRange(EditRich.Document.ContentStart, EditRich.Document.ContentEnd);
            range.Text = "";
            if (!string.IsNullOrWhiteSpace(row?.RichTextRtf))
            {
                try
                {
                    using var ms = new MemoryStream(Encoding.UTF8.GetBytes(row.RichTextRtf));
                    range.Load(ms, DataFormats.Rtf);
                }
                catch
                {
                    range.Text = row?.Text ?? "";
                }
            }
            else
                range.Text = row?.Text ?? "";
            EditorHint.Text = row is null ? "Создай новую статью или выбери существующую" : $"Категория: {row.Category} • {row.Number}";
        }
        finally { _loading = false; }
    }

    private string GetEditorPlainText()
    {
        var range = new TextRange(EditRich.Document.ContentStart, EditRich.Document.ContentEnd);
        return range.Text.TrimEnd('\r', '\n');
    }

    private string GetEditorRtf()
    {
        var range = new TextRange(EditRich.Document.ContentStart, EditRich.Document.ContentEnd);
        using var ms = new MemoryStream();
        range.Save(ms, DataFormats.Rtf);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private void ApplyLawEditor()
    {
        if (_selectedLaw is null) return;
        var requestedCategory = EditCategory.SelectedItem?.ToString() ?? _selectedLaw.Category;
        var targetStorageKey = FindStorageCategory(requestedCategory);
        _selectedLaw.Category = NormalizeCategory(requestedCategory);
        _selectedLaw.Number = EditNumber.Text.Trim();
        _selectedLaw.Title = EditTitle.Text.Trim();
        _selectedLaw.Text = GetEditorPlainText();
        _selectedLaw.RichTextRtf = GetEditorRtf();
        _selectedLaw.Note = EditNote.Text;
        _selectedLaw.Favorite = EditFavorite.IsChecked == true;

        _selectedLaw.Raw["Number"] = _selectedLaw.Number;
        _selectedLaw.Raw["Title"] = _selectedLaw.Title;
        _selectedLaw.Raw["Text"] = _selectedLaw.Text;
        _selectedLaw.Raw["RichTextRtf"] = _selectedLaw.RichTextRtf;
        _selectedLaw.Raw["Note"] = _selectedLaw.Note;
        _selectedLaw.Raw["Favorite"] = _selectedLaw.Favorite;

        if (!_selectedLaw.OriginalCategory.Equals(targetStorageKey, StringComparison.OrdinalIgnoreCase))
        {
            if (_bases[_selectedLaw.OriginalCategory] is JsonArray oldArray)
                oldArray.Remove(_selectedLaw.Raw);
            GetCategoryArray(requestedCategory).Add(_selectedLaw.Raw);
            _selectedLaw.OriginalCategory = targetStorageKey;
        }
    }

    private void ScheduleLawSave()
    {
        if (_loading || _selectedLaw is null) return;
        _lawSaveTimer.Stop();
        _lawSaveTimer.Start();
    }

    private void SchedulePanelSave()
    {
        if (_loading || _selectedPanel is null) return;
        _panelSaveTimer.Stop();
        _panelSaveTimer.Start();
    }

    private void RunSafe(Action action, string operation)
    {
        try { action(); }
        catch (Exception ex)
        {
            LogError(ex, operation);
            StatusText.Text = $"Ошибка: {operation}. Подробности сохранены в Data\\logs\\errors.log";
            MessageBox.Show(this,
                $"{operation} не выполнена. Приложение продолжит работу, ошибка записана в Data\\logs\\errors.log.\n\n{ex.Message}",
                "Russia Online Studio", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void LogError(Exception ex, string operation)
    {
        try
        {
            var path = Path.Combine(_dataDir, "logs", "errors.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {operation}{Environment.NewLine}{ex}{Environment.NewLine}---{Environment.NewLine}");
        }
        catch { }
    }

    private void CategoryBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        RefreshLawFilter();
        var category = CategoryBox.SelectedItem?.ToString();
        if (!string.IsNullOrWhiteSpace(category) && category != "Все категории")
        {
            _loading = true;
            EditCategory.SelectedItem = category;
            _loading = false;
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshLawFilter();
    private void LawList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LawList.SelectedItem is LawRow row) FillLawEditor(row);
    }
    private void LawEditor_TextChanged(object sender, TextChangedEventArgs e) => ScheduleLawSave();
    private void EditCategory_SelectionChanged(object sender, SelectionChangedEventArgs e) => ScheduleLawSave();
    private void LawEditor_OptionChanged(object sender, RoutedEventArgs e) => ScheduleLawSave();

    private void AddLaw_Click(object sender, RoutedEventArgs e)
    {
        RunSafe(() =>
        {
            var selectedFilter = CategoryBox.SelectedItem?.ToString();
            var chosenCategory = selectedFilter is not null && selectedFilter != "Все категории"
                ? selectedFilter
                : EditCategory.SelectedItem?.ToString() ?? "УК";
            chosenCategory = NormalizeCategory(chosenCategory);
            var raw = new JsonObject
            {
                ["Number"] = "",
                ["Title"] = "Новая статья",
                ["Text"] = "",
                ["RichTextRtf"] = "",
                ["Note"] = "",
                ["Favorite"] = false
            };
            GetCategoryArray(chosenCategory).Add(raw);
            var storage = FindStorageCategory(chosenCategory);
            var row = new LawRow(storage, raw);
            _laws.Add(row);

            _loading = true;
            EditCategory.SelectedItem = chosenCategory;
            if (selectedFilter is null || selectedFilter == "Все категории")
                CategoryBox.SelectedItem = chosenCategory;
            _loading = false;

            RefreshLawFilter();
            LawList.SelectedItem = row;
            FillLawEditor(row);
            EditNumber.Focus();
            StatusText.Text = $"Новая статья создаётся в категории «{chosenCategory}».";
        }, "Добавление статьи");
    }

    private void SaveLaw_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedLaw is null)
        {
            MessageBox.Show(this, "Сначала выбери существующую статью или нажми «Добавить».",
                "Russia Online Studio", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _lawSaveTimer.Stop();
        RunSafe(() =>
        {
            ApplyLawEditor();
            SaveLaws();
            RefreshLawFilter();
            EditorHint.Text = $"Сохранено: {NormalizeCategory(_selectedLaw.Category)} · {_selectedLaw.Number}";
        }, "Сохранение статьи");
    }

    private void DeleteLaw_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedLaw is null) return;
        if (MessageBox.Show(this, $"Удалить статью «{_selectedLaw.Number} — {_selectedLaw.Title}»?",
            "Подтверждение", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        RunSafe(() =>
        {
            _lawSaveTimer.Stop();
            if (_bases[_selectedLaw.OriginalCategory] is JsonArray arr)
                arr.Remove(_selectedLaw.Raw);
            _laws.Remove(_selectedLaw);
            _selectedLaw = null;
            FillLawEditor(null);
            SaveLaws();
            RefreshLawFilter();
        }, "Удаление статьи");
    }

    private void CopyLaw_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(GetEditorPlainText()); StatusText.Text = "Текст статьи скопирован."; }
        catch (Exception ex) { RunSafe(() => throw ex, "Копирование статьи"); }
    }

    private void Bold_Click(object sender, RoutedEventArgs e) =>
        ApplySelectionFormat(TextElement.FontWeightProperty, FontWeights.Bold);
    private void Italic_Click(object sender, RoutedEventArgs e) =>
        ApplySelectionFormat(TextElement.FontStyleProperty, FontStyles.Italic);
    private void Underline_Click(object sender, RoutedEventArgs e) =>
        ApplySelectionFormat(Inline.TextDecorationsProperty, TextDecorations.Underline);

    private void ApplySelectionFormat(DependencyProperty property, object value)
    {
        EditRich.Focus();
        if (!EditRich.Selection.IsEmpty) EditRich.Selection.ApplyPropertyValue(property, value);
        else EditRich.Selection.ApplyPropertyValue(property, value);
        ScheduleLawSave();
    }

    private void FontSizeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || FontSizeBox.SelectedItem is not ComboBoxItem item ||
            !double.TryParse(item.Content?.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var size)) return;
        EditRich.Focus();
        EditRich.Selection.ApplyPropertyValue(TextElement.FontSizeProperty, size);
        ScheduleLawSave();
    }

    private void FontColorBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || FontColorBox.SelectedItem is not ComboBoxItem item || item.Tag is not string color) return;
        try
        {
            EditRich.Focus();
            EditRich.Selection.ApplyPropertyValue(TextElement.ForegroundProperty,
                new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)));
            ScheduleLawSave();
        }
        catch { }
    }

    private void HighlightBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || HighlightBox.SelectedItem is not ComboBoxItem item || item.Tag is not string color) return;
        try
        {
            EditRich.Focus();
            var brush = color == "Transparent" ? Brushes.Transparent :
                new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
            EditRich.Selection.ApplyPropertyValue(TextElement.BackgroundProperty, brush);
            ScheduleLawSave();
        }
        catch { }
    }

    private void AddPanel_Click(object sender, RoutedEventArgs e)
    {
        RunSafe(() =>
        {
            var panel = new PanelRow
            {
                Id = Guid.NewGuid().ToString("N"), Title = "Новая панель", Body = "",
                Width = 360, Height = 260, Scale = 1, Visible = true
            };
            _panels.Add(panel);
            PanelList.SelectedItem = panel;
            SavePanels();
        }, "Создание панели");
    }

    private void PanelList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PanelList.SelectedItem is not PanelRow row) return;
        _panelSaveTimer.Stop();
        _selectedPanel = row;
        _loading = true;
        PanelTitle.Text = row.Title;
        PanelBody.Text = row.Body;
        PanelWidth.Text = row.Width.ToString(CultureInfo.InvariantCulture);
        PanelScale.Text = row.Scale.ToString(CultureInfo.InvariantCulture);
        PanelVisible.IsChecked = row.Visible;
        _loading = false;
        UpdatePreview();
    }

    private void PanelEditor_TextChanged(object sender, TextChangedEventArgs e) => SchedulePanelSave();
    private void PanelEditor_OptionChanged(object sender, RoutedEventArgs e) => SchedulePanelSave();

    private void ApplyPanelEditor()
    {
        if (_selectedPanel is null) return;
        _selectedPanel.Title = PanelTitle.Text.Trim();
        _selectedPanel.Body = PanelBody.Text;
        if (double.TryParse(PanelWidth.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out var width))
            _selectedPanel.Width = Math.Clamp(width, 220, 1200);
        if (double.TryParse(PanelScale.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out var scale))
            _selectedPanel.Scale = Math.Clamp(scale, .5, 2);
        _selectedPanel.Visible = PanelVisible.IsChecked == true;
    }

    private void SavePanel_Click(object sender, RoutedEventArgs e)
    {
        _panelSaveTimer.Stop();
        RunSafe(() => { ApplyPanelEditor(); SavePanels(); PanelList.Items.Refresh(); }, "Сохранение панели");
    }

    private void DeletePanel_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPanel is null) return;
        if (MessageBox.Show(this, "Удалить выбранную панель?", "Подтверждение",
            MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        RunSafe(() =>
        {
            _panelSaveTimer.Stop();
            _panels.Remove(_selectedPanel);
            _selectedPanel = null;
            PanelList.SelectedIndex = _panels.Count > 0 ? 0 : -1;
            SavePanels();
        }, "Удаление панели");
    }

    private void UpdatePreview()
    {
        if (PanelPreview is not null)
            PanelPreview.Text = _selectedPanel?.Body ?? "Выбери панель или создай новую.";
    }

    private void ProfileBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || !IsLoaded) return;
        RunSafe(() =>
        {
            _lawSaveTimer.Stop();
            _panelSaveTimer.Stop();
            if (_selectedLaw is not null) { ApplyLawEditor(); SaveLaws(); }
            if (_selectedPanel is not null) { ApplyPanelEditor(); SavePanels(); }
            WriteSignal("active_profile.txt");
            File.WriteAllText(Path.Combine(_dataDir, "active_profile.txt"), ActiveProfileKey);
            _selectedLaw = null;
            _selectedPanel = null;
            LoadAll();
        }, "Переключение профиля");
    }

    private string ReadSetting(string key, string fallback)
    {
        var path = Path.Combine(ProfileFolder, "settings.json");
        if (!File.Exists(path)) return fallback;
        try { return JsonNode.Parse(File.ReadAllText(path))?[key]?.ToString() ?? fallback; }
        catch { return fallback; }
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        RunSafe(() =>
        {
            var obj = new JsonObject
            {
                ["UiScale"] = UiScale.Text,
                ["GameScale"] = GameScale.Text,
                ["Hotkeys"] = new JsonObject
                {
                    ["Exit"] = "F8", ["ToggleAll"] = "F9", ["Control"] = "F10", ["Move"] = "F11"
                }
            };
            SaveJson(Path.Combine(ProfileFolder, "settings.json"), obj);
            StatusText.Text = "Настройки сохранены.";
        }, "Сохранение настроек");
    }

    private void OpenData_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", _dataDir) { UseShellExecute = true }); }
        catch (Exception ex) { RunSafe(() => throw ex, "Открытие папки Data"); }
    }

    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ForumTab is not null && MainTabs.SelectedItem == ForumTab && !_forumInitialized)
            _forumInitTask ??= InitializeForumAsync();
    }

    private async Task InitializeForumAsync()
    {
        try
        {
            StatusText.Text = "Подготовка встроенного браузера...";
            var userData = Path.Combine(_dataDir, "ForumBrowserProfile");
            Directory.CreateDirectory(userData);
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userData);
            await ForumBrowser.EnsureCoreWebView2Async(environment);

            ForumBrowser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            ForumBrowser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            ForumBrowser.CoreWebView2.Settings.IsStatusBarEnabled = true;
            ForumBrowser.CoreWebView2.NavigationStarting += (_, args) =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) &&
                        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                        AddressBox.Text = uri.ToString();
                });
            };
            ForumBrowser.CoreWebView2.NavigationCompleted += (_, args) =>
            {
                Dispatcher.Invoke(() => StatusText.Text = args.IsSuccess
                    ? "Форум загружен. Ctrl+F — поиск по странице."
                    : $"Ошибка загрузки страницы: {args.WebErrorStatus}");
            };
            await ForumBrowser.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
                "document.addEventListener('keydown',function(e){if(e.ctrlKey&&(e.key==='f'||e.key==='F')){e.preventDefault();e.stopImmediatePropagation();window.chrome.webview.postMessage('host-find');}},true);");
            ForumBrowser.CoreWebView2.WebMessageReceived += (_, args) =>
            {
                try
                {
                    if (args.TryGetWebMessageAsString() == "host-find")
                        Dispatcher.BeginInvoke(new Action(ShowFindBar));
                }
                catch { }
            };

            _forumInitialized = true;
            var start = AddressBox.Text;
            if (!IsHttpUrl(start)) start = "https://forum.russia.online/";
            ForumBrowser.CoreWebView2.Navigate(start);
        }
        catch (Exception ex)
        {
            LogError(ex, "InitializeForum");
            StatusText.Text = "Встроенный браузер не запустился.";
            MessageBox.Show(this,
                "Не удалось запустить WebView2. Проверь, установлен ли Microsoft Edge WebView2 Runtime.\n\n" + ex.Message,
                "Russia Online Studio", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static bool IsHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private async void BrowserGo_Click(object sender, RoutedEventArgs e) => await NavigateBrowserAsync();
    private async void AddressBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; await NavigateBrowserAsync(); }
    }

    private async Task NavigateBrowserAsync()
    {
        if (!_forumInitialized) { _forumInitTask ??= InitializeForumAsync(); await _forumInitTask; }
        if (ForumBrowser.CoreWebView2 is null) return;
        var value = AddressBox.Text.Trim();
        if (!value.Contains("://")) value = "https://" + value;
        if (!IsHttpUrl(value))
        {
            MessageBox.Show(this, "Разрешены только адреса http:// и https://.",
                "Russia Online Studio", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        ForumBrowser.CoreWebView2.Navigate(value);
    }

    private void BrowserBack_Click(object sender, RoutedEventArgs e)
    {
        if (ForumBrowser.CoreWebView2?.CanGoBack == true) ForumBrowser.CoreWebView2.GoBack();
    }
    private void BrowserForward_Click(object sender, RoutedEventArgs e)
    {
        if (ForumBrowser.CoreWebView2?.CanGoForward == true) ForumBrowser.CoreWebView2.GoForward();
    }
    private void BrowserReload_Click(object sender, RoutedEventArgs e) => ForumBrowser.CoreWebView2?.Reload();

    private void ShowFindBar()
    {
        FindBar.Visibility = Visibility.Visible;
        FindBox.Focus();
        FindBox.SelectAll();
    }
    private void CloseFind_Click(object sender, RoutedEventArgs e)
    {
        FindBar.Visibility = Visibility.Collapsed;
        ForumBrowser.Focus();
    }
    private async void FindNext_Click(object sender, RoutedEventArgs e) => await FindOnPageAsync(false);
    private async void FindPrevious_Click(object sender, RoutedEventArgs e) => await FindOnPageAsync(true);
    private async void FindBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; await FindOnPageAsync(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)); }
        else if (e.Key == Key.Escape) { FindBar.Visibility = Visibility.Collapsed; ForumBrowser.Focus(); }
    }

    private async Task FindOnPageAsync(bool backwards)
    {
        if (ForumBrowser.CoreWebView2 is null || string.IsNullOrWhiteSpace(FindBox.Text)) return;
        var query = JsonSerializer.Serialize(FindBox.Text);
        var reverse = backwards ? "true" : "false";
        var script = $"window.find({query}, false, {reverse}, true, false, true, false);";
        try { await ForumBrowser.CoreWebView2.ExecuteScriptAsync(script); }
        catch (Exception ex) { LogError(ex, "FindOnPage"); }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            MainTabs.SelectedItem = ForumTab;
            Dispatcher.BeginInvoke(new Action(ShowFindBar));
        }
    }

    private void GoLaws_Click(object sender, RoutedEventArgs e) => MainTabs.SelectedIndex = 0;
    private void GoPanels_Click(object sender, RoutedEventArgs e) => MainTabs.SelectedIndex = 1;
    private void SaveAll_Click(object sender, RoutedEventArgs e)
    {
        RunSafe(() =>
        {
            if (_selectedLaw is not null) { ApplyLawEditor(); SaveLaws(); }
            if (_selectedPanel is not null) { ApplyPanelEditor(); SavePanels(); }
        }, "Сохранение всех данных");
    }
}

public sealed class LawRow
{
    public string Category { get; set; }
    public string OriginalCategory { get; set; }
    public JsonObject Raw { get; }
    public string Number { get; set; }
    public string Title { get; set; }
    public string Text { get; set; }
    public string RichTextRtf { get; set; }
    public string Note { get; set; }
    public bool Favorite { get; set; }
    public string Display => $"{Number}  {Title}";

    public LawRow(string storageCategory, JsonObject raw)
    {
        OriginalCategory = storageCategory;
        Category = storageCategory.Equals("ук", StringComparison.OrdinalIgnoreCase) ? "УК" :
            storageCategory.Equals("КОАП", StringComparison.OrdinalIgnoreCase) ? "КоАП" :
            storageCategory;
        Raw = raw;
        Number = raw["Number"]?.ToString() ?? "";
        Title = raw["Title"]?.ToString() ?? "";
        Text = raw["Text"]?.ToString() ?? "";
        RichTextRtf = raw["RichTextRtf"]?.ToString() ?? "";
        Note = raw["Note"]?.ToString() ?? "";
        Favorite = bool.TryParse(raw["Favorite"]?.ToString(), out var fav) && fav;
    }
}

public sealed class PanelRow
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "Новая панель";
    public string Body { get; set; } = "";
    public double Width { get; set; } = 360;
    public double Height { get; set; } = 260;
    public double Scale { get; set; } = 1;
    public double X { get; set; } = 80;
    public double Y { get; set; } = 100;
    public double Opacity { get; set; } = .94;
    public bool Visible { get; set; } = true;
    public bool Topmost { get; set; } = true;

    public JsonObject ToJson() => new()
    {
        ["Id"] = Id, ["Title"] = Title, ["Text"] = Body, ["Width"] = Width, ["Height"] = Height,
        ["Scale"] = Scale, ["X"] = X, ["Y"] = Y, ["Opacity"] = Opacity,
        ["Visible"] = Visible, ["Topmost"] = Topmost
    };

    public static PanelRow FromJson(JsonObject j) => new()
    {
        Id = j["Id"]?.ToString() ?? Guid.NewGuid().ToString("N"),
        Title = j["Title"]?.ToString() ?? "Панель",
        Body = j["Text"]?.ToString() ?? j["Body"]?.ToString() ?? "",
        Width = ReadDouble(j, "Width", 360),
        Height = ReadDouble(j, "Height", 260),
        Scale = ReadDouble(j, "Scale", 1),
        X = ReadDouble(j, "X", 80),
        Y = ReadDouble(j, "Y", 100),
        Opacity = ReadDouble(j, "Opacity", .94),
        Visible = !bool.TryParse(j["Visible"]?.ToString(), out var visible) || visible,
        Topmost = !bool.TryParse(j["Topmost"]?.ToString(), out var topmost) || topmost
    };

    private static double ReadDouble(JsonObject o, string k, double fallback) =>
        double.TryParse(o[k]?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var value) ? value : fallback;
}