using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;

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

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public MainWindow()
    {
        InitializeComponent();
        _dataDir = FindDataDirectory();
        Directory.CreateDirectory(_dataDir);
        DataPath.Text = _dataDir;
        LawList.ItemsSource = _laws;
        PanelList.ItemsSource = _panels;
        CategoryBox.ItemsSource = new[] { "Все категории", "УК", "КоАП", "ПГО", "ФЗ о ФСО", "О территориях" };
        EditCategory.ItemsSource = new[] { "УК", "КоАП", "ПГО", "ФЗ о ФСО", "О территориях" };
        CategoryBox.SelectedIndex = 0;
        EditCategory.SelectedIndex = 0;
        var activeProfileFile = Path.Combine(_dataDir, "active_profile.txt");
        var activeProfile = File.Exists(activeProfileFile) ? File.ReadAllText(activeProfileFile).Trim() : "main";
        ProfileBox.SelectedIndex = activeProfile switch { "deputy" => 1, "personal" => 2, _ => 0 };
        LoadAll();
    }

    private static string FindDataDirectory()
    {
        var baseDir = AppContext.BaseDirectory;
        var beside = Path.Combine(baseDir, "Data");
        if (Directory.Exists(beside)) return beside;
        var parent = Path.Combine(Directory.GetParent(baseDir.TrimEnd(Path.DirectorySeparatorChar))?.FullName ?? baseDir, "Data");
        return parent;
    }

    private string ProfileFolder => Path.Combine(_dataDir, "Profiles", ProfileBox.SelectedIndex switch { 1 => "deputy", 2 => "personal", _ => "main" });
    private string LawsPath => Path.Combine(ProfileFolder, "laws_database.json");
    private string PanelsPath => Path.Combine(ProfileFolder, "panels.json");

    private void LoadAll()
    {
        Directory.CreateDirectory(ProfileFolder);
        _loading = true;
        try
        {
            if (File.Exists(LawsPath))
                _lawRoot = JsonNode.Parse(File.ReadAllText(LawsPath)) as JsonObject ?? new JsonObject();
            else
                _lawRoot = LoadBundledOrDefault();
            _bases = _lawRoot["Bases"] as JsonObject ?? new JsonObject();
            if (_lawRoot["Bases"] is null) _lawRoot["Bases"] = _bases;
            LoadLawRows();
            LoadPanels();
            UiScale.Text = ReadSetting("UiScale", "125");
            GameScale.Text = ReadSetting("GameScale", "0.6");
            StatusText.Text = "Данные загружены • " + DateTime.Now.ToString("HH:mm:ss");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Ошибка загрузки данных: " + ex.Message, "Russia Online Studio", MessageBoxButton.OK, MessageBoxImage.Error);
            _lawRoot = new JsonObject { ["Bases"] = new JsonObject() };
            _bases = (JsonObject)_lawRoot["Bases"]!;
            LoadLawRows();
            LoadPanels();
        }
        finally { _loading = false; }
        UpdateStats();
    }

    private JsonObject LoadBundledOrDefault()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "Data", "laws_database.json");
        if (File.Exists(bundled))
            return JsonNode.Parse(File.ReadAllText(bundled)) as JsonObject ?? new JsonObject();
        return new JsonObject { ["Bases"] = new JsonObject() };
    }

    private string ReadSetting(string key, string fallback)
    {
        var path = Path.Combine(ProfileFolder, "settings.json");
        if (!File.Exists(path)) return fallback;
        try { return JsonNode.Parse(File.ReadAllText(path))?[key]?.ToString() ?? fallback; } catch { return fallback; }
    }

    private void LoadLawRows()
    {
        _laws.Clear();
        foreach (var cat in _bases)
        {
            if (cat.Value is not JsonArray arr) continue;
            foreach (var node in arr)
            {
                if (node is not JsonObject obj) continue;
                _laws.Add(new LawRow(cat.Key, obj));
            }
        }
        RefreshLawFilter();
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
            catch { }
        }
        if (_panels.Count == 0)
            _panels.Add(new PanelRow { Id = Guid.NewGuid().ToString("N"), Title = "Порядок действий", Body = "Добавь собственные инструкции в этом окне.", Width = 360, Scale = 1, Visible = true });
        PanelList.SelectedIndex = _panels.Count > 0 ? 0 : -1;
        UpdatePreview();
    }

    private void RefreshLawFilter()
    {
        var cat = CategoryBox.SelectedItem?.ToString() ?? "Все категории";
        var term = SearchBox.Text.Trim();
        var rows = _laws.Where(x => (cat == "Все категории" || x.Category == cat) &&
            (term.Length == 0 || (x.Number + " " + x.Title + " " + x.Text).Contains(term, StringComparison.OrdinalIgnoreCase))).ToList();
        LawList.ItemsSource = rows;
    }

    private void SaveJson(string path, JsonNode node)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, node.ToJsonString(JsonOptions));
        if (File.Exists(path))
        {
            var backup = path + ".backup_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
            File.Copy(path, backup, false);
        }
        File.Move(temp, path, true);
    }

    private void SaveLaws()
    {
        SaveJson(LawsPath, _lawRoot);
        StatusText.Text = "База законов сохранена с резервной копией • " + DateTime.Now.ToString("HH:mm:ss");
        UpdateStats();
    }

    private void SavePanels()
    {
        var arr = new JsonArray();
        foreach (var p in _panels) arr.Add(p.ToJson());
        SaveJson(PanelsPath, arr);
        StatusText.Text = "Панели сохранены • " + DateTime.Now.ToString("HH:mm:ss");
        UpdateStats();
        UpdatePreview();
    }

    private void UpdateStats()
    {
        StatLaws.Text = _laws.Count.ToString();
        StatPanels.Text = _panels.Count.ToString();
        StatProfile.Text = ProfileBox.SelectedItem is ComboBoxItem c ? c.Content?.ToString() ?? "Основной" : "Основной";
    }

    private void FillLawEditor(LawRow? row)
    {
        _selectedLaw = row;
        _loading = true;
        EditCategory.SelectedItem = row?.Category ?? "УК";
        EditNumber.Text = row?.Number ?? "";
        EditTitle.Text = row?.Title ?? "";
        EditText.Text = row?.Text ?? "";
        EditNote.Text = row?.Note ?? "";
        EditFavorite.IsChecked = row?.Favorite ?? false;
        _loading = false;
    }

    private void ApplyLawEditor()
    {
        if (_selectedLaw is null) return;
        _selectedLaw.Category = EditCategory.SelectedItem?.ToString() ?? "УК";
        _selectedLaw.Number = EditNumber.Text;
        _selectedLaw.Title = EditTitle.Text;
        _selectedLaw.Text = EditText.Text;
        _selectedLaw.Note = EditNote.Text;
        _selectedLaw.Favorite = EditFavorite.IsChecked == true;
        _selectedLaw.Raw["Number"] = _selectedLaw.Number;
        _selectedLaw.Raw["Title"] = _selectedLaw.Title;
        _selectedLaw.Raw["Text"] = _selectedLaw.Text;
        _selectedLaw.Raw["Note"] = _selectedLaw.Note;
        _selectedLaw.Raw["Favorite"] = _selectedLaw.Favorite;
        if (_selectedLaw.OriginalCategory != _selectedLaw.Category)
        {
            if (_bases[_selectedLaw.OriginalCategory] is JsonArray oldArr) oldArr.Remove(_selectedLaw.Raw);
            GetCategoryArray(_selectedLaw.Category).Add(_selectedLaw.Raw);
            _selectedLaw.OriginalCategory = _selectedLaw.Category;
        }
    }

    private JsonArray GetCategoryArray(string cat)
    {
        if (_bases[cat] is JsonArray arr) return arr;
        arr = new JsonArray();
        _bases[cat] = arr;
        return arr;
    }

    private void CategoryBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!_loading) RefreshLawFilter(); }
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) { if (!_loading) RefreshLawFilter(); }
    private void LawList_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (LawList.SelectedItem is LawRow row) FillLawEditor(row); }
    private void AddLaw_Click(object sender, RoutedEventArgs e)
    {
        var raw = new JsonObject { ["Number"] = "", ["Title"] = "Новая статья", ["Text"] = "", ["Note"] = "", ["Favorite"] = false };
        var cat = "УК";
        GetCategoryArray(cat).Add(raw);
        var row = new LawRow(cat, raw);
        _laws.Add(row); RefreshLawFilter(); LawList.SelectedItem = row; FillLawEditor(row);
        EditNumber.Focus();
    }
    private void SaveLaw_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedLaw is null) { MessageBox.Show(this, "Сначала выбери статью."); return; }
        ApplyLawEditor(); SaveLaws(); RefreshLawFilter();
    }
    private void DeleteLaw_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedLaw is null) return;
        if (MessageBox.Show(this, "Удалить статью «" + _selectedLaw.Title + "»?", "Подтверждение", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (_bases[_selectedLaw.OriginalCategory] is JsonArray arr) arr.Remove(_selectedLaw.Raw);
        _laws.Remove(_selectedLaw); FillLawEditor(null); SaveLaws(); RefreshLawFilter();
    }
    private void CopyLaw_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(EditText.Text); StatusText.Text = "Текст скопирован."; } catch { }
    }
    private void AddPanel_Click(object sender, RoutedEventArgs e)
    {
        var p = new PanelRow { Id = Guid.NewGuid().ToString("N"), Title = "Новая панель", Body = "", Width = 360, Scale = 1, Visible = true };
        _panels.Add(p); PanelList.SelectedItem = p; SavePanels();
    }
    private void PanelList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedPanel = PanelList.SelectedItem as PanelRow;
        if (_selectedPanel is null) return;
        _loading = true;
        PanelTitle.Text = _selectedPanel.Title; PanelBody.Text = _selectedPanel.Body;
        PanelWidth.Text = _selectedPanel.Width.ToString(); PanelScale.Text = _selectedPanel.Scale.ToString(System.Globalization.CultureInfo.InvariantCulture);
        PanelVisible.IsChecked = _selectedPanel.Visible;
        _loading = false; UpdatePreview();
    }
    private void ApplyPanelEditor()
    {
        if (_selectedPanel is null) return;
        _selectedPanel.Title = PanelTitle.Text;
        _selectedPanel.Body = PanelBody.Text;
        if (double.TryParse(PanelWidth.Text, out var w)) _selectedPanel.Width = Math.Clamp(w, 220, 1200);
        if (double.TryParse(PanelScale.Text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var s)) _selectedPanel.Scale = Math.Clamp(s, .5, 2);
        _selectedPanel.Visible = PanelVisible.IsChecked == true;
    }
    private void SavePanel_Click(object sender, RoutedEventArgs e) { ApplyPanelEditor(); SavePanels(); PanelList.Items.Refresh(); }
    private void DeletePanel_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPanel is null) return;
        if (MessageBox.Show(this, "Удалить панель?", "Подтверждение", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        _panels.Remove(_selectedPanel); _selectedPanel = null; PanelList.SelectedIndex = _panels.Count > 0 ? 0 : -1; SavePanels();
    }
    private void UpdatePreview() { if (_selectedPanel is not null) PanelPreview.Text = _selectedPanel.Body; }
    private void SaveAll_Click(object sender, RoutedEventArgs e) { if (_selectedLaw is not null) ApplyLawEditor(); if (_selectedPanel is not null) ApplyPanelEditor(); SaveLaws(); SavePanels(); }
    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        var obj = new JsonObject { ["UiScale"] = UiScale.Text, ["GameScale"] = GameScale.Text, ["Hotkeys"] = new JsonObject { ["Exit"] = "F8", ["ToggleAll"] = "F9", ["Control"] = "F10", ["Move"] = "F11" } };
        SaveJson(Path.Combine(ProfileFolder, "settings.json"), obj);
        StatusText.Text = "Настройки сохранены.";
    }
    private void ProfileBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && IsLoaded)
        {
            var key = ProfileBox.SelectedIndex switch { 1 => "deputy", 2 => "personal", _ => "main" };
            File.WriteAllText(Path.Combine(_dataDir, "active_profile.txt"), key);
            LoadAll();
        }
    }
    private void OpenData_Click(object sender, RoutedEventArgs e) { try { Process.Start(new ProcessStartInfo("explorer.exe", _dataDir) { UseShellExecute = true }); } catch { } }
    private void GoLaws_Click(object sender, RoutedEventArgs e) => MainTabs.SelectedIndex = 1;
    private void GoPanels_Click(object sender, RoutedEventArgs e) => MainTabs.SelectedIndex = 2;
}

public sealed class LawRow
{
    public string Category { get; set; }
    public string OriginalCategory { get; set; }
    public JsonObject Raw { get; }
    public string Number { get; set; }
    public string Title { get; set; }
    public string Text { get; set; }
    public string Note { get; set; }
    public bool Favorite { get; set; }
    public string Display => $"{Number}  {Title}";
    public LawRow(string category, JsonObject raw)
    {
        Category = OriginalCategory = category; Raw = raw;
        Number = raw["Number"]?.ToString() ?? ""; Title = raw["Title"]?.ToString() ?? "";
        Text = raw["Text"]?.ToString() ?? ""; Note = raw["Note"]?.ToString() ?? "";
        Favorite = raw["Favorite"]?.GetValue<bool>() ?? false;
    }
}
public sealed class PanelRow
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "Новая панель";
    public string Body { get; set; } = "";
    public double Width { get; set; } = 360;
    public double Scale { get; set; } = 1;
    public double X { get; set; } = 80;
    public double Y { get; set; } = 100;
    public double Opacity { get; set; } = .94;
    public bool Visible { get; set; } = true;
    public bool Topmost { get; set; } = true;
    public JsonObject ToJson() => new() { ["Id"] = Id, ["Title"] = Title, ["Text"] = Body, ["Width"] = Width, ["Scale"] = Scale, ["X"] = X, ["Y"] = Y, ["Opacity"] = Opacity, ["Visible"] = Visible, ["Topmost"] = Topmost };
    public static PanelRow FromJson(JsonObject j) => new() { Id = j["Id"]?.ToString() ?? Guid.NewGuid().ToString("N"), Title = j["Title"]?.ToString() ?? "Панель", Body = j["Text"]?.ToString() ?? j["Body"]?.ToString() ?? "", Width = ReadDouble(j,"Width",360), Scale = ReadDouble(j,"Scale",1), X = ReadDouble(j,"X",80), Y = ReadDouble(j,"Y",100), Opacity = ReadDouble(j,"Opacity",.94), Visible = j["Visible"]?.GetValue<bool>() ?? true, Topmost = j["Topmost"]?.GetValue<bool>() ?? true };
    private static double ReadDouble(JsonObject o,string k,double d) => double.TryParse(o[k]?.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture,out var v)?v:d;
}