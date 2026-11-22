using Microsoft.Web.WebView2.Core;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Data;
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
    private readonly ObservableCollection<LawCatalogDocument> _catalogDocuments = new();
    private readonly Dictionary<string, LawDocumentTabState> _lawDocumentTabs = new(StringComparer.OrdinalIgnoreCase);
    private LawDocumentTabState? _archiveTabState;
    private LawDocumentTabState? _activeLawTab;
    private LawRow? _selectedLaw;
    private PanelRow? _selectedPanel;
    private bool _suppressLawSelection;
    private bool _loading;
    private string _loadedProfileKey = "main";
    private bool _forumInitialized;
    private Task? _forumInitTask;
    private readonly DispatcherTimer _lawSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private readonly SemaphoreSlim _lawSaveGate = new(1, 1);
    private long _lawSaveRevision;
    private readonly DispatcherTimer _panelSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public MainWindow()
    {
        InitializeComponent();
        _dataDir = FindDataDirectory();
        Directory.CreateDirectory(_dataDir);
        DataPath.Text = _dataDir;

        _lawSaveTimer.Tick += async (_, _) =>
        {
            _lawSaveTimer.Stop();
            if (_loading || _selectedLaw is null) return;
            try
            {
                ApplyLawEditor();
                await SaveLawsAsync(createBackup: false);
                StatusText.Text = $"База сохранена • {_laws.Count} статей • Overlay обновит памятку";
                RefreshLawFilter();
            }
            catch (Exception ex)
            {
                LogError(ex, "Автосохранение статьи");
                StatusText.Text = "Не удалось сохранить статью. Исходные данные не перезаписаны; см. Data\\logs\\errors.log.";
                MessageBox.Show(this,
                    "Автосохранение не выполнено. Проверь доступ к папке Data и файл журнала.\n\n" + ex.Message,
                    "Russia Online Studio", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
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
        MainTabs.ApplyTemplate();
        UpdateMainTabsLayout();
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

            _lawRoot = MergeProfileSafely(bundled, profileDb);
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
        // The root database is a legacy source, not the active law catalogue.
        // Keep it intact in a separate archive category and load only the 27
        // full-title documents imported from the Arbat-server catalogue.
        var rootPath = Path.Combine(_dataDir, "laws_database.json");
        JsonObject legacyRoot = new();
        if (File.Exists(rootPath))
        {
            legacyRoot = JsonNode.Parse(File.ReadAllText(rootPath)) as JsonObject
                ?? throw new InvalidDataException("Data/laws_database.json повреждён.");
            if (legacyRoot["Bases"] is not JsonObject)
                throw new InvalidDataException("Data/laws_database.json не содержит объект Bases.");
        }

        var result = new JsonObject();
        foreach (var property in legacyRoot)
        {
            if (!property.Key.Equals("Bases", StringComparison.OrdinalIgnoreCase))
                result[property.Key] = property.Value?.DeepClone();
        }

        var activeBases = new JsonObject();
        result["Bases"] = activeBases;
        var catalogue = new JsonArray();
        var extendedDirectory = Path.Combine(_dataDir, "ExtendedLaws");
        if (!Directory.Exists(extendedDirectory))
            throw new DirectoryNotFoundException("Не найдена папка Data\\ExtendedLaws.");

        var files = Directory.EnumerateFiles(extendedDirectory, "*.json")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
        if (files.Count == 0)
            throw new InvalidDataException("В Data\\ExtendedLaws нет импортированных документов Арбатского каталога.");

        foreach (var file in files)
        {
            var extension = JsonNode.Parse(File.ReadAllText(file)) as JsonObject
                ?? throw new InvalidDataException($"Файл {Path.GetFileName(file)} повреждён.");
            var category = extension["Category"]?.ToString()?.Trim();
            var articles = extension["Articles"] as JsonArray;
            var server = extension["Server"]?.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(category) || articles is null)
                throw new InvalidDataException($"В {Path.GetFileName(file)} нет Category или Articles.");
            if (!string.Equals(server, "Арбатский", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Документ {category} не подтверждён для Арбатского сервера.");
            if (activeBases.ContainsKey(category))
                throw new InvalidDataException($"Повторяется заголовок документа: {category}");

            activeBases[category] = articles.DeepClone();
            catalogue.Add(new JsonObject
            {
                ["Order"] = int.TryParse(extension["Order"]?.ToString(), out var order) ? order : 999,
                ["Category"] = category,
                ["Title"] = extension["LawTitle"]?.ToString() ?? category,
                ["SourceUrl"] = extension["SourceUrl"]?.ToString() ?? "",
                ["SourceUpdated"] = extension["SourceUpdated"]?.ToString() ?? "",
                ["ArticleCount"] = extension["ArticleCount"]?.DeepClone()
            });
        }

        result["Catalog"] = catalogue;

        var legacyArchive = new JsonArray();
        if (legacyRoot["Bases"] is JsonObject oldBases)
        {
            foreach (var category in oldBases)
            {
                if (category.Value is not JsonArray oldArray) continue;
                foreach (var item in oldArray)
                {
                    if (item is not JsonObject oldArticle) continue;
                    var archived = (JsonObject)oldArticle.DeepClone();
                    archived["LegacySourceCategory"] = category.Key;
                    archived["SourceStatus"] = "Старая базовая копия. Не является активной редакцией Арбатского каталога.";
                    legacyArchive.Add(archived);
                }
            }
        }
        if (legacyArchive.Count > 0)
            activeBases["Архив • Старые записи (непроверено)"] = legacyArchive;

        return result;
    }

    private static JsonObject MergeProfileSafely(JsonObject canonicalDb, JsonObject profileDb)
    {
        // The bundled Arbat catalogue is authoritative. A profile may contribute
        // personal notes/favourites, but must never overwrite the new law text.
        var merged = (JsonObject)canonicalDb.DeepClone();
        var targetBases = merged["Bases"] as JsonObject ?? new JsonObject();
        merged["Bases"] = targetBases;
        var sourceBases = profileDb["Bases"] as JsonObject;
        if (sourceBases is null) return merged;

        var byId = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        var byNaturalKey = new Dictionary<string, List<JsonObject>>(StringComparer.OrdinalIgnoreCase);
        foreach (var targetArray in targetBases.Select(pair => pair.Value).OfType<JsonArray>())
        {
            foreach (var node in targetArray.OfType<JsonObject>())
            {
                var id = node["Id"]?.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(id)) byId[id] = node;
                var key = BuildArticleMatchKey(node);
                if (key is null) continue;
                if (!byNaturalKey.TryGetValue(key, out var matches))
                    byNaturalKey[key] = matches = new List<JsonObject>();
                matches.Add(node);
            }
        }

        var profileArchive = targetBases["Архив • Старые записи (непроверено)"] as JsonArray
            ?? new JsonArray();
        foreach (var sourceCategory in sourceBases)
        {
            if (sourceCategory.Value is not JsonArray sourceArray) continue;
            foreach (var sourceArticle in sourceArray.OfType<JsonObject>())
            {
                JsonObject? canonical = null;
                var id = sourceArticle["Id"]?.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(id))
                    byId.TryGetValue(id, out canonical);

                if (canonical is null)
                {
                    var key = BuildArticleMatchKey(sourceArticle);
                    if (key is not null && byNaturalKey.TryGetValue(key, out var candidates) && candidates.Count == 1)
                        canonical = candidates[0];
                }

                if (canonical is not null)
                {
                    // Personal metadata is safe to migrate independently of the legal wording.
                    foreach (var field in new[] { "Note", "Tags", "Favorite", "InMemo" })
                    {
                        if (sourceArticle.ContainsKey(field))
                            canonical[field] = sourceArticle[field]?.DeepClone();
                    }

                    var oldText = sourceArticle["Text"]?.ToString() ?? "";
                    var currentText = canonical["Text"]?.ToString() ?? "";
                    var oldTitle = sourceArticle["Title"]?.ToString() ?? "";
                    var currentTitle = canonical["Title"]?.ToString() ?? "";
                    if (!string.Equals(oldText, currentText, StringComparison.Ordinal) ||
                        !string.Equals(oldTitle, currentTitle, StringComparison.Ordinal))
                    {
                        AddProfileArchive(profileArchive, sourceArticle, sourceCategory.Key,
                            "Сохранённая профильная копия прежней редакции; текущий текст загружен из каталога Арбатского сервера.");
                    }
                    else if (sourceArticle["RichTextRtf"] is JsonValue rtf && !string.IsNullOrWhiteSpace(rtf.ToString()))
                    {
                        canonical["RichTextRtf"] = rtf.DeepClone();
                    }
                    continue;
                }

                // Keep custom rows if they already belong to a full-title category.
                // Unmatched rows from old abbreviated categories are kept in the archive,
                // so they cannot appear beside current legal provisions.
                if (targetBases[sourceCategory.Key] is JsonArray sameCategory &&
                    !sourceCategory.Key.StartsWith("Архив", StringComparison.OrdinalIgnoreCase) &&
                    !sourceCategory.Key.Equals("УК", StringComparison.OrdinalIgnoreCase) &&
                    !sourceCategory.Key.Equals("ук", StringComparison.OrdinalIgnoreCase) &&
                    !sourceCategory.Key.Equals("КОАП", StringComparison.OrdinalIgnoreCase) &&
                    !sourceCategory.Key.Equals("КоАП", StringComparison.OrdinalIgnoreCase) &&
                    !sourceCategory.Key.Equals("ПК", StringComparison.OrdinalIgnoreCase))
                {
                    var custom = (JsonObject)sourceArticle.DeepClone();
                    if (!string.IsNullOrWhiteSpace(id) &&
                        sameCategory.OfType<JsonObject>().Any(item =>
                            string.Equals(item["Id"]?.ToString(), id, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    sameCategory.Add(custom);
                }
                else
                {
                    AddProfileArchive(profileArchive, sourceArticle, sourceCategory.Key,
                        "Не удалось однозначно сопоставить со свежим документом каталога; исходная запись сохранена отдельно.");
                }
            }
        }

        if (profileArchive.Count > 0)
            targetBases["Архив • Старые записи (непроверено)"] = profileArchive;
        return merged;
    }

    private static string? BuildArticleMatchKey(JsonObject article)
    {
        var source = NormalizeMatchText(article["SourceLawTitle"]?.ToString());
        var number = NormalizeArticleNumber(article["Number"]?.ToString());
        var title = NormalizeMatchText(article["Title"]?.ToString());
        if (source.Length == 0 || number.Length == 0 || title.Length == 0) return null;
        return $"{source}|{number}|{title}";
    }

    private static string NormalizeMatchText(string? value) =>
        Regex.Replace((value ?? "").Trim().ToLowerInvariant(), @"\s+", " ");

    private static string NormalizeArticleNumber(string? value)
    {
        var source = (value ?? "").Trim();
        var match = Regex.Match(source, @"(?:ст\.?\s*)?(\d+(?:\.\d+)*)$", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : source.ToLowerInvariant();
    }

    private static void AddProfileArchive(JsonArray archive, JsonObject article, string category, string status)
    {
        var copy = (JsonObject)article.DeepClone();
        copy["LegacySourceCategory"] = category;
        copy["SourceStatus"] = status;
        var id = copy["Id"]?.ToString();
        var number = copy["Number"]?.ToString() ?? "";
        var title = copy["Title"]?.ToString() ?? "";
        var text = copy["Text"]?.ToString() ?? "";
        var duplicate = archive.OfType<JsonObject>().Any(item =>
            (!string.IsNullOrWhiteSpace(id) &&
             string.Equals(item["Id"]?.ToString(), id, StringComparison.OrdinalIgnoreCase)) ||
            (string.Equals(item["Number"]?.ToString() ?? "", number, StringComparison.OrdinalIgnoreCase) &&
             string.Equals(item["Title"]?.ToString() ?? "", title, StringComparison.Ordinal) &&
             string.Equals(item["Text"]?.ToString() ?? "", text, StringComparison.Ordinal)));
        if (!duplicate) archive.Add(copy);
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
        _activeLawTab = null;
        MainTabs.SelectedItem = LibraryTab;

        var orderedCategories = new List<string>();
        if (_lawRoot["Catalog"] is JsonArray catalogue)
        {
            foreach (var item in catalogue.OfType<JsonObject>().OrderBy(item =>
                         int.TryParse(item["Order"]?.ToString(), out var order) ? order : 999))
            {
                var category = item["Category"]?.ToString();
                if (!string.IsNullOrWhiteSpace(category) && _bases[category] is JsonArray &&
                    !orderedCategories.Contains(category, StringComparer.OrdinalIgnoreCase))
                    orderedCategories.Add(category);
            }
        }

        var additionalCategories = _bases.Select(pair => pair.Key)
            .Where(category => !orderedCategories.Contains(category, StringComparer.OrdinalIgnoreCase))
            .OrderBy(category => category.StartsWith("Архив", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(category => category, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var category in orderedCategories.Concat(additionalCategories))
        {
            if (_bases[category] is not JsonArray array) continue;
            foreach (var node in array)
                if (node is JsonObject obj)
                {
                    if (string.IsNullOrWhiteSpace(obj["Id"]?.ToString()))
                        obj["Id"] = Guid.NewGuid().ToString("N");
                    _laws.Add(new LawRow(category, obj));
                }
        }

        var categories = orderedCategories.Concat(additionalCategories)
            .Select(NormalizeCategory).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        EditCategory.ItemsSource = categories;
        EditCategory.SelectedItem = categories.FirstOrDefault(category =>
            !category.StartsWith("Архив", StringComparison.OrdinalIgnoreCase)) ?? categories.FirstOrDefault();

        BuildLawDocumentTabs();
        RefreshLawFilter();
        UpdateMainTabsLayout();
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

    private void BuildLawDocumentTabs()
    {
        foreach (var state in _lawDocumentTabs.Values)
            MainTabs.Items.Remove(state.Tab);
        if (_archiveTabState is not null)
            MainTabs.Items.Remove(_archiveTabState.Tab);

        _lawDocumentTabs.Clear();
        _archiveTabState = null;
        _catalogDocuments.Clear();

        var index = Math.Max(0, MainTabs.Items.IndexOf(LibraryTab) + 1);
        if (_lawRoot["Catalog"] is JsonArray catalogue)
        {
            foreach (var item in catalogue.OfType<JsonObject>().OrderBy(item =>
                         int.TryParse(item["Order"]?.ToString(), out var order) ? order : 999))
            {
                var category = item["Category"]?.ToString()?.Trim();
                if (string.IsNullOrWhiteSpace(category) || _bases[category] is not JsonArray articles)
                    continue;

                var document = new LawCatalogDocument
                {
                    Order = int.TryParse(item["Order"]?.ToString(), out var order) ? order : 999,
                    Category = category,
                    DocumentType = item["DocumentType"]?.ToString() ?? "Документ",
                    File = item["File"]?.ToString() ?? "",
                    SourceUrl = item["SourceUrl"]?.ToString() ?? "",
                    SourceUpdated = item["SourceUpdated"]?.ToString() ?? "",
                    ArticleCount = articles.Count,
                    CatalogArticleCount = int.TryParse(item["CatalogArticleCount"]?.ToString(), out var sourceCount)
                        ? sourceCount : articles.Count
                };
                _catalogDocuments.Add(document);

                var state = new LawDocumentTabState
                {
                    Category = category,
                    Document = document,
                    IsArchive = false
                };
                state.Tab = new TabItem
                {
                    Header = BuildShortTabHeader(document),
                    ToolTip = document.Category,
                    Tag = state,
                    Content = BuildLawTabContent(state)
                };
                _lawDocumentTabs[category] = state;
                MainTabs.Items.Insert(index++, state.Tab);
            }
        }

        _archiveTabState = new LawDocumentTabState
        {
            Category = "Архив",
            IsArchive = true
        };
        _archiveTabState.Tab = new TabItem
        {
            Header = "Архив",
            ToolTip = "Старые и неподтверждённые записи. Не являются действующей редакцией документов Арбатского сервера.",
            Tag = _archiveTabState,
            Content = BuildLawTabContent(_archiveTabState)
        };
        MainTabs.Items.Insert(index, _archiveTabState.Tab);
        LawCatalogTiles.ItemsSource = _catalogDocuments;
        CatalogSearchBox.Text = "";
        LibrarySummaryText.Text = $"В каталоге {_catalogDocuments.Count} документов Арбатского сервера. Выбери закон, чтобы открыть его отдельную вкладку.";
    }

    private static string BuildShortTabHeader(LawCatalogDocument document)
    {
        return document.File.ToLowerInvariant() switch
        {
            "criminal-code.json" => "УК",
            "constitution.json" => "Конституция",
            "criminal-procedure.json" => "УПК",
            "koap.json" => "КоАП",
            "labor.json" => "Трудовой кодекс",
            "ethics.json" => "Этика",
            "court.json" => "Суды",
            "gibdd-disciplinary.json" => "Дисциплина ГИБДД",
            "gibdd-charter.json" => "Устав ГИБДД",
            "government-law.json" => "Правительство",
            "traffic.json" => "ПДД",
            "police.json" => "Полиция",
            "prosecutor.json" => "Прокуратура",
            "security-service.json" => "ФСБ",
            "emergency.json" => "ЧП / ВП",
            "territories.json" => "Территории",
            "weapons.json" => "Оружие",
            "fso.json" => "ФСО",
            "documents.json" => "Документы",
            "armed-forces.json" => "ВС РО",
            "business.json" => "Бизнес",
            "investigative-committee.json" => "СК",
            "advocacy.json" => "Адвокатура",
            "media.json" => "СМИ",
            "immunity.json" => "Неприкосновенность",
            "medical.json" => "Медицина",
            "internal-security.json" => "ОСБ ГИБДД",
            _ => document.Category
        };
    }

    private UIElement BuildLawTabContent(LawDocumentTabState state)
    {
        var grid = new Grid { Margin = new Thickness(12), Background = new SolidColorBrush(Color.FromRgb(17, 24, 32)) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new TextBlock
        {
            Text = state.IsArchive ? "СТАРЫЕ И НЕПРОВЕРЕННЫЕ ЗАПИСИ" : state.Category,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(233, 208, 165)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(4, 0, 4, 5),
            ToolTip = state.Category
        };
        Grid.SetRow(heading, 0);
        grid.Children.Add(heading);

        var filter = new DockPanel { Margin = new Thickness(3, 5, 3, 8), LastChildFill = true };
        var favorites = new CheckBox
        {
            Content = "★ Избранное",
            Foreground = new SolidColorBrush(Color.FromRgb(220, 193, 142)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 4, 0),
            Tag = state
        };
        favorites.Checked += DocumentFilter_Changed;
        favorites.Unchecked += DocumentFilter_Changed;
        state.OnlyFavorites = favorites;
        DockPanel.SetDock(favorites, Dock.Right);
        filter.Children.Add(favorites);

        var search = new TextBox
        {
            ToolTip = state.IsArchive ? "Поиск по архивным записям" : $"Поиск только в документе: {state.Category}",
            VerticalContentAlignment = VerticalAlignment.Center,
            MinWidth = 130,
            Tag = state
        };
        search.TextChanged += DocumentSearch_TextChanged;
        state.Search = search;
        filter.Children.Add(search);
        Grid.SetRow(filter, 1);
        grid.Children.Add(filter);

        var list = new ListBox
        {
            Background = new SolidColorBrush(Color.FromRgb(16, 22, 29)),
            Foreground = new SolidColorBrush(Color.FromRgb(232, 237, 243)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(45, 57, 71)),
            BorderThickness = new Thickness(1),
            ItemTemplate = (DataTemplate)FindResource("LawRowTemplate"),
            Tag = state,
            Margin = new Thickness(2)
        };
        list.SelectionChanged += LawList_SelectionChanged;
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        list.GroupStyle.Add(new GroupStyle { HeaderTemplate = (DataTemplate)FindResource("LawChapterGroupTemplate") });
        state.List = list;
        Grid.SetRow(list, 2);
        grid.Children.Add(list);

        var footer = new DockPanel { Margin = new Thickness(3, 7, 3, 0) };
        var count = new TextBlock
        {
            Text = "0 записей",
            Foreground = new SolidColorBrush(Color.FromRgb(143, 156, 171)),
            VerticalAlignment = VerticalAlignment.Center
        };
        state.Count = count;
        footer.Children.Add(count);
        if (!state.IsArchive)
        {
            var add = new Button
            {
                Content = "＋ Добавить статью",
                Background = new SolidColorBrush(Color.FromRgb(198, 161, 107)),
                Foreground = new SolidColorBrush(Color.FromRgb(17, 21, 26)),
                Padding = new Thickness(10, 5, 10, 5),
                ToolTip = "Добавить пользовательскую статью в этот документ",
                HorizontalAlignment = HorizontalAlignment.Right
            };
            add.Click += AddLaw_Click;
            DockPanel.SetDock(add, Dock.Right);
            footer.Children.Add(add);
        }
        Grid.SetRow(footer, 3);
        grid.Children.Add(footer);
        return grid;
    }

    private void RefreshLawFilter()
    {
        foreach (var state in _lawDocumentTabs.Values)
            RefreshLawTab(state);
        if (_archiveTabState is not null)
            RefreshLawTab(_archiveTabState);
    }

    private void RefreshLawTab(LawDocumentTabState state)
    {
        if (state.List is null) return;
        var selected = state.List.SelectedItem as LawRow;
        var query = state.Search?.Text?.Trim() ?? "";
        var onlyFavorites = state.OnlyFavorites?.IsChecked == true;

        var rows = _laws.Where(row =>
        {
            var inDocument = state.IsArchive
                ? row.Category.StartsWith("Архив", StringComparison.OrdinalIgnoreCase)
                : NormalizeCategory(row.Category).Equals(NormalizeCategory(state.Category), StringComparison.OrdinalIgnoreCase);
            if (!inDocument || (onlyFavorites && !row.Favorite)) return false;
            return query.Length == 0 || string.Join(" ", row.Number, row.Title, row.Chapter, row.Text,
                row.Tags, row.Note, row.Raw["SourceRef"]?.ToString(), row.Raw["SourceLawTitle"]?.ToString())
                .Contains(query, StringComparison.OrdinalIgnoreCase);
        }).ToList();

        _suppressLawSelection = true;
        try
        {
            var view = new ListCollectionView(rows);
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(LawRow.Chapter)));
            state.List.ItemsSource = view;
            if (selected is not null && rows.Contains(selected))
                state.List.SelectedItem = selected;
        }
        finally { _suppressLawSelection = false; }

        if (state.Count is not null)
            state.Count.Text = $"{rows.Count} записей" + (state.IsArchive ? " · архив" : "");
    }

    private bool MatchesLawTabFilter(LawRow row, LawDocumentTabState state)
    {
        var inDocument = state.IsArchive
            ? row.Category.StartsWith("Архив", StringComparison.OrdinalIgnoreCase)
            : NormalizeCategory(row.Category).Equals(NormalizeCategory(state.Category), StringComparison.OrdinalIgnoreCase);
        if (!inDocument || (state.OnlyFavorites?.IsChecked == true && !row.Favorite)) return false;
        var query = state.Search?.Text?.Trim() ?? "";
        return query.Length == 0 || string.Join(" ", row.Number, row.Title, row.Chapter, row.Text,
            row.Tags, row.Note, row.Raw["SourceRef"]?.ToString(), row.Raw["SourceLawTitle"]?.ToString())
            .Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void DocumentSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || sender is not TextBox search || search.Tag is not LawDocumentTabState state) return;
        if (_selectedLaw is not null && ReferenceEquals(_activeLawTab, state) &&
            ReferenceEquals(state.List?.SelectedItem, _selectedLaw) && !MatchesLawTabFilter(_selectedLaw, state))
        {
            CommitSelectedLaw();
            _suppressLawSelection = true;
            state.List!.SelectedItem = null;
            _suppressLawSelection = false;
            FillLawEditor(null);
        }
        RefreshLawTab(state);
    }

    private void DocumentFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not CheckBox checkBox || checkBox.Tag is not LawDocumentTabState state) return;
        if (_selectedLaw is not null && ReferenceEquals(_activeLawTab, state) &&
            ReferenceEquals(state.List?.SelectedItem, _selectedLaw) && !MatchesLawTabFilter(_selectedLaw, state))
        {
            CommitSelectedLaw();
            _suppressLawSelection = true;
            state.List!.SelectedItem = null;
            _suppressLawSelection = false;
            FillLawEditor(null);
        }
        RefreshLawTab(state);
    }

    private void CatalogSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_catalogDocuments.Count == 0) return;
        var query = CatalogSearchBox.Text?.Trim() ?? "";
        LawCatalogTiles.ItemsSource = query.Length == 0
            ? _catalogDocuments
            : _catalogDocuments.Where(document => document.Category.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                document.DocumentType.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void OpenLawDocument_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string category &&
            _lawDocumentTabs.TryGetValue(category, out var state))
            MainTabs.SelectedItem = state.Tab;
    }

    private void OpenArchiveTab_Click(object sender, RoutedEventArgs e)
    {
        if (_archiveTabState is not null)
            MainTabs.SelectedItem = _archiveTabState.Tab;
    }

    private void UpdateMainTabsLayout()
    {
        MainTabs.ApplyTemplate();
        var state = (MainTabs.SelectedItem as TabItem)?.Tag as LawDocumentTabState;
        var showEditor = state is not null;
        LawEditorHost.Visibility = showEditor ? Visibility.Visible : Visibility.Collapsed;

        if (MainTabs.Template.FindName("PART_SelectedContentHost", MainTabs) is ContentPresenter presenter)
            Grid.SetColumnSpan(presenter, showEditor ? 1 : 2);
    }

    private void CommitSelectedLaw()
    {
        if (_selectedLaw is null) return;
        _lawSaveTimer.Stop();
        ApplyLawEditor();
        SaveLaws(createBackup: false);
    }

    private sealed class LawDocumentTabState
    {
        public string Category { get; set; } = "";
        public bool IsArchive { get; set; }
        public LawCatalogDocument? Document { get; set; }
        public TabItem Tab { get; set; } = null!;
        public ListBox? List { get; set; }
        public TextBox? Search { get; set; }
        public CheckBox? OnlyFavorites { get; set; }
        public TextBlock? Count { get; set; }
    }

    private void SaveJson(string path, JsonNode node, bool createBackup = true)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
                {
                    node.WriteTo(writer);
                    writer.Flush();
                }
                // Flush file contents before the atomic replacement.
                stream.Flush(flushToDisk: true);
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

    private static void WriteTextAtomically(string path, string value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(value);
                writer.Flush();
                stream.Flush(flushToDisk: true);
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
        WriteTextAtomically(path, DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture));
    }

    private void SaveLaws(bool createBackup = true)
    {
        var path = LawsPath;
        var snapshot = (JsonObject)_lawRoot.DeepClone();
        var revision = Interlocked.Increment(ref _lawSaveRevision);

        // Serialize all writes to the active profile. A background autosave may
        // already be finishing while the user clicks Save, switches profiles or exits.
        _lawSaveGate.Wait();
        try
        {
            // A newer snapshot may have reached the gate first. Never let an older
            // snapshot overwrite a newer save just because its waiter ran later.
            if (revision == Volatile.Read(ref _lawSaveRevision))
            {
                SaveJson(path, snapshot, createBackup);
                WriteSignal("laws_revision.txt");
            }
        }
        finally
        {
            _lawSaveGate.Release();
        }

        StatusText.Text = $"База сохранена • {_laws.Count} статей • Overlay обновит памятку";
        UpdateStats();
    }

    private async Task SaveLawsAsync(bool createBackup = false)
    {
        // Capture a detached snapshot on the UI thread; JsonNode instances must not
        // be mutated while the background thread serializes them.
        var path = LawsPath;
        var snapshot = (JsonObject)_lawRoot.DeepClone();
        var revision = Interlocked.Increment(ref _lawSaveRevision);

        await _lawSaveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            // Do not capture the WPF UI context: synchronous SaveLaws callers may
            // wait for this gate during profile switches or window shutdown.
            if (revision != Volatile.Read(ref _lawSaveRevision))
                return;

            await Task.Run(() => SaveJson(path, snapshot, createBackup)).ConfigureAwait(false);

            // If edits arrived while the file was being written, the newer queued
            // snapshot will perform the final write and publish the refresh signal.
            if (revision == Volatile.Read(ref _lawSaveRevision))
                await Task.Run(() => WriteSignal("laws_revision.txt")).ConfigureAwait(false);
        }
        finally
        {
            _lawSaveGate.Release();
        }
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
            EditCategory.SelectedItem = row?.Category ?? (_activeLawTab is { IsArchive: false } activeTab ? activeTab.Category : _catalogDocuments.FirstOrDefault()?.Category);
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
            var chapter = row?.Raw["Chapter"]?.ToString();
            var updated = row?.Raw["SourceUpdated"]?.ToString();
            var sourceStatus = row?.Raw["SourceStatus"]?.ToString();
            EditorHint.Text = row is null
                ? "Создай новую статью или выбери существующую"
                : $"Категория: {row.Category} • {row.Number}" +
                  (string.IsNullOrWhiteSpace(chapter) ? "" : $" • {chapter}") +
                  (string.IsNullOrWhiteSpace(sourceRef) ? "" : $" • Источник: {sourceRef}") +
                  (string.IsNullOrWhiteSpace(sourceLaw) ? "" : $" — {sourceLaw}") +
                  (string.IsNullOrWhiteSpace(updated) ? "" : $" • Обновлено: {updated}") +
                  (string.IsNullOrWhiteSpace(sourceStatus) ? "" : $" • {sourceStatus}");
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

    private void LawList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _suppressLawSelection || sender is not ListBox list ||
            list.SelectedItem is not LawRow row) return;

        if (_selectedLaw is not null && !ReferenceEquals(_selectedLaw, row))
            CommitSelectedLaw();

        _activeLawTab = list.Tag as LawDocumentTabState;
        FillLawEditor(row);
    }

    private void LawEditor_TextChanged(object sender, TextChangedEventArgs e) => ScheduleLawSave();
    private void EditCategory_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _selectedLaw is null) return;

        var requestedCategory = NormalizeCategory(EditCategory.SelectedItem?.ToString());
        if (string.IsNullOrWhiteSpace(requestedCategory) ||
            requestedCategory.Equals(NormalizeCategory(_selectedLaw.Category), StringComparison.OrdinalIgnoreCase))
            return;

        _lawSaveTimer.Stop();
        RunSafe(() =>
        {
            var movedRow = _selectedLaw;
            CommitSelectedLaw();
            RefreshLawFilter();

            LawDocumentTabState? destination = null;
            if (!_lawDocumentTabs.TryGetValue(requestedCategory, out destination) &&
                requestedCategory.StartsWith("Архив", StringComparison.OrdinalIgnoreCase))
                destination = _archiveTabState;

            if (destination is not null)
            {
                MainTabs.SelectedItem = destination.Tab;
                _activeLawTab = destination;
                _suppressLawSelection = true;
                destination.List!.SelectedItem = movedRow;
                _suppressLawSelection = false;
                FillLawEditor(movedRow);
            }
            EditorHint.Text = $"Статья перемещена в раздел «{requestedCategory}».";
        }, "Перемещение статьи между документами");
    }
    private void LawEditor_OptionChanged(object sender, RoutedEventArgs e) => ScheduleLawSave();

    private void AddLaw_Click(object sender, RoutedEventArgs e)
    {
        if (_activeLawTab is null || _activeLawTab.IsArchive)
        {
            MessageBox.Show(this, "Открой вкладку конкретного документа, чтобы добавить статью в него.",
                "Russia Online Studio", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        RunSafe(() =>
        {
            if (_selectedLaw is not null)
                CommitSelectedLaw();

            var chosenCategory = _activeLawTab.Category;
            var raw = new JsonObject
            {
                ["Id"] = Guid.NewGuid().ToString("N"),
                ["Number"] = "",
                ["Title"] = "Новая статья",
                ["Text"] = "",
                ["RichTextRtf"] = "",
                ["Note"] = "",
                ["Favorite"] = false,
                ["InMemo"] = true,
                ["Chapter"] = "Пользовательские записи",
                ["SourceLawTitle"] = chosenCategory,
                ["SourceServer"] = "Арбатский",
                ["SourceStatus"] = "Пользовательская запись"
            };

            GetCategoryArray(chosenCategory).Add(raw);
            var storage = FindStorageCategory(chosenCategory);
            var row = new LawRow(storage, raw);
            _laws.Add(row);
            SaveLaws(createBackup: false);
            RefreshLawFilter();

            MainTabs.SelectedItem = _activeLawTab.Tab;
            _activeLawTab.List!.SelectedItem = row;
            FillLawEditor(row);
            EditNumber.Focus();
            StatusText.Text = $"Новая запись создаётся в документе «{chosenCategory}».";
        }, "Добавление статьи в документ");
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
            if (_selectedLaw is not null) { CommitSelectedLaw(); FillLawEditor(null); }
            if (_selectedPanel is not null) { ApplyPanelEditor(); SavePanels(); }
            _activeLawTab = null;
            MainTabs.SelectedItem = LibraryTab;
            UpdateMainTabsLayout();
            var nextProfile = ActiveProfileKey;
            WriteTextAtomically(Path.Combine(_dataDir, "active_profile.txt"), nextProfile);
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
        const double baseWidth = 1200;
        const double baseHeight = 800;
        var workArea = SystemParameters.WorkArea;
        var maxByWidth = Math.Max(.8, (workArea.Width - 40) / baseWidth);
        var maxByHeight = Math.Max(.8, (workArea.Height - 70) / baseHeight);
        var appliedScale = Math.Min(requestedScale, Math.Min(maxByWidth, maxByHeight));

        RootLayout.LayoutTransform = new ScaleTransform(appliedScale, appliedScale);
        Width = baseWidth * appliedScale;
        Height = baseHeight * appliedScale;
        MinWidth = 960 * appliedScale;
        MinHeight = 620 * appliedScale;
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

            var merged = MergeProfileSafely(_lawRoot, imported);
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
        if (!ReferenceEquals(sender, MainTabs) || _loading) return;

        var nextLawTab = (MainTabs.SelectedItem as TabItem)?.Tag as LawDocumentTabState;
        if (!ReferenceEquals(_activeLawTab, nextLawTab))
        {
            if (_selectedLaw is not null)
            {
                CommitSelectedLaw();
                FillLawEditor(null);
            }
            _activeLawTab = nextLawTab;
        }

        UpdateMainTabsLayout();
        if (nextLawTab is not null)
        {
            StatusText.Text = nextLawTab.IsArchive
                ? "Архив старых и неподтверждённых записей. Эти материалы не являются действующими законами."
                : $"Документ: {nextLawTab.Category} • только статьи этого документа";
        }

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

public sealed class LawCatalogDocument
{
    public int Order { get; set; }
    public string Category { get; set; } = "";
    public string DocumentType { get; set; } = "";
    public string File { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    public string SourceUpdated { get; set; } = "";
    public int ArticleCount { get; set; }
    public int CatalogArticleCount { get; set; }
}

public sealed class LawRow
{
    public string Id { get; set; }
    public string Category { get; set; }
    public string OriginalCategory { get; set; }
    public JsonObject Raw { get; }
    public string Number { get; set; }
    public string Title { get; set; }
    public string Chapter { get; set; }
    public string Text { get; set; }
    public string RichTextRtf { get; set; }
    public string Note { get; set; }
    public string Tags { get; set; }
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
        Chapter = raw["Chapter"]?.ToString() ?? "";
        Text = raw["Text"]?.ToString() ?? "";
        RichTextRtf = raw["RichTextRtf"]?.ToString() ?? "";
        Note = raw["Note"]?.ToString() ?? "";
        Tags = raw["Tags"] is JsonArray tagArray
            ? string.Join(", ", tagArray.Select(tag => tag?.ToString()).Where(tag => !string.IsNullOrWhiteSpace(tag)))
            : raw["Tags"]?.ToString() ?? "";
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