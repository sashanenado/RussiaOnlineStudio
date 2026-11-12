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
using Microsoft.Win32;

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
    private string _loadedProfileKey = "main";
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
                SaveLaws(createBackup: false);
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
                SavePanels(createBackup: false);
                PanelList.Items.Refresh();
                UpdatePreview();
            }, "Автосохранение панели");
        };

        LawList.ItemsSource = _laws;
        PanelList.ItemsSource = _panels;
        _loading = true;
        _loadedProfileKey = ReadActiveProfileKey();
        ProfileBox.SelectedIndex = _loadedProfileKey switch
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
    private string ProfileFolder => Path.Combine(_dataDir, "Profiles", _loadedProfileKey);
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
            {
                try
                {
                    var parsedProfile = JsonNode.Parse(File.ReadAllText(LawsPath)) as JsonObject;
                    if (parsedProfile?["Bases"] is JsonObject)
                        profileDb = parsedProfile;
                    else
                        LogError(new InvalidDataException("Профильная база не содержит объекта Bases; загружена общая база без удаления файла профиля."),
                            "LoadProfileLawDatabase");
                }
                catch (Exception profileException)
                {
                    LogError(profileException, "LoadProfileLawDatabase");
                    StatusText.Text = "Ошибка профиля: используется общая база. Старый файл профиля сохранён и не перезаписан.";
                }
            }

            _lawRoot = MergeLawDatabases(bundled, profileDb);
            _bases = _lawRoot["Bases"] as JsonObject ?? new JsonObject();
            _lawRoot["Bases"] = _bases;

            LoadLawRows();
            LoadPanels();
            UiScale.Text = ReadSetting("UiScale", "125");
            GameScale.Text = ReadSetting("GameScale", "0.6");
            ApplyUiScale();
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

        var extendedDirectory = Path.Combine(_dataDir, "ExtendedLaws");
        if (Directory.Exists(extendedDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(extendedDirectory, "*.json").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                var extension = JsonNode.Parse(File.ReadAllText(file)) as JsonObject;
                var category = extension?["Category"]?.ToString();
                var articles = extension?["Articles"] as JsonArray;
                if (string.IsNullOrWhiteSpace(category) || articles is null) continue;
                var extensionDb = new JsonObject
                {
                    ["Bases"] = new JsonObject
                    {
                        [category] = articles.DeepClone()
                    }
                };
                parsed = MergeLawDatabases(parsed, extensionDb);
            }
        }
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
                var id = sourceArticle["Id"]?.ToString()?.Trim() ?? "";
                var number = sourceArticle["Number"]?.ToString()?.Trim() ?? "";
                JsonObject? existingArticle = null;

                // Find stable IDs globally so a category change removes the old source-category copy.
                if (id.Length > 0)
                {
                    foreach (var otherCategory in targetBases)
                    {
                        if (otherCategory.Value is not JsonArray otherArray) continue;
                        for (var i = 0; i < otherArray.Count; i++)
                        {
                            if (otherArray[i] is JsonObject current &&
                                string.Equals(current["Id"]?.ToString()?.Trim(), id, StringComparison.OrdinalIgnoreCase))
                            {
                                existingArticle = current;
                                if (!string.Equals(otherCategory.Key, targetKey, StringComparison.OrdinalIgnoreCase))
                                    otherArray.RemoveAt(i);
                                break;
                            }
                        }
                        if (existingArticle is not null) break;
                    }
                }

                // Legacy base entries may not have an ID. A move can be detected by a unique
                // exact pair of article number and title across all categories.
                if (existingArticle is null && number.Length > 0)
                {
                    var title = sourceArticle["Title"]?.ToString()?.Trim() ?? "";
                    var matches = new List<(JsonArray Array, int Index, JsonObject Article)>();
                    foreach (var otherCategory in targetBases)
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
                        if (!ReferenceEquals(match.Array, targetArray))
                            match.Array.RemoveAt(match.Index);
                    }
                    else if (matches.Count == 0)
                    {
                        for (var i = 0; i < targetArray.Count; i++)
                        {
                            if (targetArray[i] is JsonObject current &&
                                string.Equals(current["Number"]?.ToString()?.Trim(), number, StringComparison.OrdinalIgnoreCase))
                            { existingArticle = current; break; }
                        }
                    }
                }

                var mergedArticle = existingArticle is null
                    ? (JsonObject)sourceArticle.DeepClone()
                    : (JsonObject)existingArticle.DeepClone();
                foreach (var property in sourceArticle)
                    mergedArticle[property.Key] = property.Value?.DeepClone();

                var destinationIndex = -1;
                if (id.Length > 0)
                {
                    for (var i = 0; i < targetArray.Count; i++)
                    {
                        if (targetArray[i] is JsonObject current &&
                            string.Equals(current["Id"]?.ToString()?.Trim(), id, StringComparison.OrdinalIgnoreCase))
                        { destinationIndex = i; break; }
                    }
                }
                if (destinationIndex >= 0) targetArray[destinationIndex] = mergedArticle;
                else targetArray.Add(mergedArticle);
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
                {
                    // Give every record a stable identity. This prevents two drafts with the same
                    // temporary title and no article number from overwriting each other.
                    if (string.IsNullOrWhiteSpace(obj["Id"]?.ToString()))
                        obj["Id"] = Guid.NewGuid().ToString("N");
                    _laws.Add(new LawRow(cat.Key, obj));
                }
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
        var merged = new Dictionary<string, PanelRow>(StringComparer.OrdinalIgnoreCase);

        void ReadPanelFile(string path, bool profileOverrides)
        {
            if (!File.Exists(path)) return;
            try
            {
                var array = JsonNode.Parse(File.ReadAllText(path)) as JsonArray;
                if (array is null) return;
                foreach (var node in array)
                {
                    if (node is not JsonObject obj) continue;
                    var panel = PanelRow.FromJson(obj);
                    if (string.IsNullOrWhiteSpace(panel.Id))
                        panel.Id = Guid.NewGuid().ToString("N");

                    if (profileOverrides || !merged.ContainsKey(panel.Id))
                        merged[panel.Id] = panel;
                }
            }
            catch (Exception ex) { LogError(ex, "LoadPanels: " + path); }
        }

        // Shared defaults are always loaded first; profile-specific copies override them by ID.
        ReadPanelFile(Path.Combine(_dataDir, "panels.json"), profileOverrides: false);
        ReadPanelFile(PanelsPath, profileOverrides: true);

        // Deletion tombstones stop removed shared defaults from reappearing in this profile.
        var deletedPath = Path.Combine(ProfileFolder, "deleted_panels.json");
        if (File.Exists(deletedPath))
        {
            try
            {
                var deleted = JsonNode.Parse(File.ReadAllText(deletedPath)) as JsonArray;
                if (deleted is not null)
                    foreach (var node in deleted)
                        if (node is not null) merged.Remove(node.ToString());
            }
            catch (Exception ex) { LogError(ex, "LoadDeletedPanels"); }
        }

        foreach (var panel in merged.Values)
            _panels.Add(panel);

        if (_panels.Count == 0)
        {
            _panels.Add(new PanelRow
            {
                Id = Guid.NewGuid().ToString("N"), Title = "Порядок действий",
                Body = "Добавь собственные инструкции в этом окне.",
                Width = 360, Height = 260, Scale = 1, Visible = true
            });
        }

        PanelList.SelectedIndex = _panels.Count > 0 ? 0 : -1;
        UpdatePreview();
    }

    private void RefreshLawFilter()
    {
        var selected = CategoryBox.SelectedItem?.ToString() ?? "Все категории";
        var query = SearchBox.Text?.Trim() ?? "";
        var onlyFavorites = OnlyFavorites.IsChecked == true;
        var rows = _laws.Where(x =>
            (selected == "Все категории" || NormalizeCategory(x.Category).Equals(NormalizeCategory(selected), StringComparison.OrdinalIgnoreCase)) &&
            (!onlyFavorites || x.Favorite) &&
            (query.Length == 0 || string.Join(" ", x.Number, x.Title, x.Text, x.Tags, x.Note, x.Raw["SourceRef"]?.ToString(), x.Raw["SourceLawTitle"]?.ToString()).Contains(query, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var old = _selectedLaw;
        LawList.ItemsSource = rows;
        if (old is not null && rows.Contains(old))
            LawList.SelectedItem = old;
        else if (rows.Count == 0 && NormalizeCategory(selected) == "ПГО")
            EditorHint.Text = "В исходном архиве найдено название «Правила государственных организаций» и количество 118 пунктов, но текст самих правил не передан. Импортировать их можно после получения текста из форума во встроенном браузере.";
        else if (rows.Count == 0 && selected != "Все категории")
            EditorHint.Text = $"Раздел «{selected}» пока пуст. Добавь статью или импортируй JSON.";
        else if (selected == "Все категории")
            EditorHint.Text = "Выбери статью или создай новую.";
    }

    private void SaveJson(string path, JsonNode node, bool createBackup = true)
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

            if (createBackup && File.Exists(path))
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

    private void SaveLaws(bool createBackup = true)
    {
        SaveJson(LawsPath, _lawRoot, createBackup);
        WriteSignal("laws_revision.txt");
        StatusText.Text = $"База сохранена • {_laws.Count} статей • Overlay обновит памятку";
        UpdateStats();
    }

    private void SavePanels(bool createBackup = true)
    {
        var arr = new JsonArray();
        foreach (var panel in _panels) arr.Add(panel.ToJson());
        SaveJson(PanelsPath, arr, createBackup);
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
            EditTags.Text = row?.Tags ?? "";
            EditFavorite.IsChecked = row?.Favorite ?? false;
            EditInMemo.IsChecked = row?.InMemo ?? true;
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
            var sourceRef = row?.Raw["SourceRef"]?.ToString();
            var sourceLaw = row?.Raw["SourceLawTitle"]?.ToString();
            EditorHint.Text = row is null
                ? "Создай новую статью или выбери существующую"
                : $"Категория: {row.Category} • {row.Number}" +
                  (string.IsNullOrWhiteSpace(sourceRef) ? "" : $" • Источник: {sourceRef}") +
                  (string.IsNullOrWhiteSpace(sourceLaw) ? "" : $" — {sourceLaw}");
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
        _selectedLaw.Tags = EditTags.Text;
        _selectedLaw.Favorite = EditFavorite.IsChecked == true;
        _selectedLaw.InMemo = EditInMemo.IsChecked == true;

        _selectedLaw.Raw["Id"] = _selectedLaw.Id;
        _selectedLaw.Raw["Number"] = _selectedLaw.Number;
        _selectedLaw.Raw["Title"] = _selectedLaw.Title;
        _selectedLaw.Raw["Text"] = _selectedLaw.Text;
        _selectedLaw.Raw["RichTextRtf"] = _selectedLaw.RichTextRtf;
        _selectedLaw.Raw["Note"] = _selectedLaw.Note;
        _selectedLaw.Raw["Tags"] = new JsonArray(_selectedLaw.Tags.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(tag => (JsonNode?)JsonValue.Create(tag)).ToArray());
        _selectedLaw.Raw["Favorite"] = _selectedLaw.Favorite;
        _selectedLaw.Raw["InMemo"] = _selectedLaw.InMemo;

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
    private void FilterOptions_Changed(object sender, RoutedEventArgs e) => RefreshLawFilter();
    private void LawList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LawList.SelectedItem is not LawRow row) return;
        if (!_loading && _selectedLaw is not null && !ReferenceEquals(_selectedLaw, row))
        {
            _lawSaveTimer.Stop();
            RunSafe(() => { ApplyLawEditor(); SaveLaws(createBackup: false); }, "Сохранение предыдущей статьи при переключении");
        }
        FillLawEditor(row);
    }
    private void LawEditor_TextChanged(object sender, TextChangedEventArgs e) => ScheduleLawSave();
    private void EditCategory_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        var category = EditCategory.SelectedItem?.ToString();
        if (!string.IsNullOrWhiteSpace(category) &&
            CategoryBox.SelectedItem?.ToString() is string currentFilter &&
            currentFilter != "Все категории")
        {
            _loading = true;
            CategoryBox.SelectedItem = category;
            _loading = false;
            RefreshLawFilter();
        }
        ScheduleLawSave();
    }
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
                ["Id"] = Guid.NewGuid().ToString("N"),
                ["Number"] = "",
                ["Title"] = "Новая статья",
                ["Text"] = "",
                ["RichTextRtf"] = "",
                ["Note"] = "",
                ["Favorite"] = false,
                ["InMemo"] = true
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
        if (!_loading && _selectedPanel is not null && !ReferenceEquals(_selectedPanel, row))
        {
            _panelSaveTimer.Stop();
            RunSafe(() => { ApplyPanelEditor(); SavePanels(createBackup: false); }, "Сохранение предыдущей панели при переключении");
        }
        _panelSaveTimer.Stop();
        _selectedPanel = row;
        _loading = true;
        try
        {
            PanelTitle.Text = row.Title;
            PanelBody.Text = row.Body;
            PanelWidth.Text = row.Width.ToString(CultureInfo.InvariantCulture);
            PanelScale.Text = row.Scale.ToString(CultureInfo.InvariantCulture);
            PanelVisible.IsChecked = row.Visible;
        }
        finally { _loading = false; }
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
            var deletedId = _selectedPanel.Id;
            _panels.Remove(_selectedPanel);
            _selectedPanel = null;
            var deletedPath = Path.Combine(ProfileFolder, "deleted_panels.json");
            var deleted = File.Exists(deletedPath)
                ? JsonNode.Parse(File.ReadAllText(deletedPath)) as JsonArray ?? new JsonArray()
                : new JsonArray();
            if (!deleted.Any(node => string.Equals(node?.ToString(), deletedId, StringComparison.OrdinalIgnoreCase)))
                deleted.Add(deletedId);
            SaveJson(deletedPath, deleted, createBackup: false);
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
            var nextProfile = ActiveProfileKey;
            WriteSignal("active_profile.txt");
            File.WriteAllText(Path.Combine(_dataDir, "active_profile.txt"), nextProfile);
            _loadedProfileKey = nextProfile;
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

    private void ApplyUiScale()
    {
        if (RootLayout is null || UiScale is null) return;
        var requestedPercent = double.TryParse(UiScale.Text, NumberStyles.Any,
            CultureInfo.InvariantCulture, out var value) ? value : 125;
        var requestedScale = Math.Clamp(requestedPercent / 100.0, .8, 1.6);

        // Keep the scaled workspace inside the current Windows work area.
        const double baseWidth = 1120;
        const double baseHeight = 730;
        var workArea = SystemParameters.WorkArea;
        var maxByWidth = Math.Max(.8, (workArea.Width - 40) / baseWidth);
        var maxByHeight = Math.Max(.8, (workArea.Height - 70) / baseHeight);
        var appliedScale = Math.Min(requestedScale, Math.Min(maxByWidth, maxByHeight));

        RootLayout.LayoutTransform = new ScaleTransform(appliedScale, appliedScale);
        Width = baseWidth * appliedScale;
        Height = baseHeight * appliedScale;
        MinWidth = 900 * appliedScale;
        MinHeight = 580 * appliedScale;
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
            ApplyUiScale();
            StatusText.Text = "Настройки сохранены и применены.";
        }, "Сохранение настроек");
    }

    private void ExportDatabase_Click(object sender, RoutedEventArgs e)
    {
        RunSafe(() =>
        {
            _lawSaveTimer.Stop();
            if (_selectedLaw is not null) { ApplyLawEditor(); SaveLaws(createBackup: false); }
            var dialog = new SaveFileDialog
            {
                Title = "Экспорт базы законов",
                Filter = "Файл базы законов (*.json)|*.json",
                FileName = $"RussiaOnline_Laws_{DateTime.Now:yyyyMMdd_HHmm}.json",
                AddExtension = true,
                DefaultExt = ".json"
            };
            if (dialog.ShowDialog(this) != true) return;
            File.WriteAllText(dialog.FileName, _lawRoot.ToJsonString(JsonOptions), new UTF8Encoding(false));
            StatusText.Text = "База законов экспортирована: " + dialog.FileName;
        }, "Экспорт базы законов");
    }

    private void ImportDatabase_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Импорт базы законов",
            Filter = "Файл базы законов (*.json)|*.json|Все файлы (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;
        RunSafe(() =>
        {
            _lawSaveTimer.Stop();
            if (_selectedLaw is not null) ApplyLawEditor();
            var imported = JsonNode.Parse(File.ReadAllText(dialog.FileName)) as JsonObject
                ?? throw new InvalidDataException("JSON должен содержать объект верхнего уровня.");
            if (imported["Bases"] is not JsonObject)
                throw new InvalidDataException("В файле не найден объект Bases. Исходная база не изменена.");

            var merged = MergeLawDatabases(_lawRoot, imported);
            if (merged["Bases"] is not JsonObject mergedBases)
                throw new InvalidDataException("Не удалось прочитать разделы базы.");

            _lawSaveTimer.Stop();
            _selectedLaw = null;
            _loading = true;
            try
            {
                _lawRoot = merged;
                _bases = mergedBases;
                LoadLawRows();
            }
            finally { _loading = false; }

            FillLawEditor(null);
            SaveLaws(createBackup: true);
            RefreshLawFilter();
            StatusText.Text = $"Импорт завершён. Сейчас в базе {_laws.Count} статей.";
        }, "Импорт базы законов");
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
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var userData = Path.Combine(localAppData, "RussiaOnlineStudio", "ForumBrowserProfile");
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
    public string Id { get; set; }
    public string Category { get; set; }
    public string OriginalCategory { get; set; }
    public JsonObject Raw { get; }
    public string Number { get; set; }
    public string Title { get; set; }
    public string Text { get; set; }
    public string RichTextRtf { get; set; }
    public string Note { get; set; }
    public bool Favorite { get; set; }
    public bool InMemo { get; set; }
    public string Display => $"{Number}  {Title}";

    public LawRow(string storageCategory, JsonObject raw)
    {
        OriginalCategory = storageCategory;
        Category = storageCategory.Equals("ук", StringComparison.OrdinalIgnoreCase) ? "УК" :
            storageCategory.Equals("КОАП", StringComparison.OrdinalIgnoreCase) ? "КоАП" :
            storageCategory;
        Raw = raw;
        Id = raw["Id"]?.ToString() ?? Guid.NewGuid().ToString("N");
        raw["Id"] = Id;
        Number = raw["Number"]?.ToString() ?? "";
        Title = raw["Title"]?.ToString() ?? "";
        Text = raw["Text"]?.ToString() ?? "";
        RichTextRtf = raw["RichTextRtf"]?.ToString() ?? "";
        Note = raw["Note"]?.ToString() ?? "";
        Favorite = bool.TryParse(raw["Favorite"]?.ToString(), out var fav) && fav;
        InMemo = !bool.TryParse(raw["InMemo"]?.ToString(), out var inMemo) || inMemo;
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