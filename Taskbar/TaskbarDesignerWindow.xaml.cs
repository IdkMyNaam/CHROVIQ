using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace WindowsCustomizer;

/// <summary>
/// Edit taskbar designs without touching JSON: designs on the left, the three zones in the middle,
/// options for the selected module and the look on the right, and a live preview on top.
/// Every change is saved to the design's file shortly after; the main window watches those files,
/// so the real taskbar updates too when the edited design is in use.
/// </summary>
public partial class TaskbarDesignerWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly Func<string> _getActiveName;
    private readonly Action<string> _setActive;

    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };

    private List<TaskbarDesign> _designs = new();
    private TaskbarDesign? _design;          // the design being edited
    private TaskbarModule? _selected;        // the selected module
    private ListBox? _activeZoneList;        // zone the next "Add" goes to
    private bool _suppress;                  // true while lists are refilled in code

    /// <param name="getActiveName">Name of the design the taskbar uses now.</param>
    /// <param name="setActive">Makes a design the one the taskbar uses.</param>
    public TaskbarDesignerWindow(Func<string> getActiveName, Action<string> setActive)
    {
        _getActiveName = getActiveName;
        _setActive = setActive;
        InitializeComponent();
        AppThemes.Register(this); // the app theme, now and when it changes
        FluentLook.Shield(PreviewFrame); // the preview looks exactly like the real bar, not like this window
        LiveCheck.IsChecked = _liveSetting;

        AddModuleBox.ItemsSource = Enum.GetValues<ModuleType>().Select(t => new ModuleChoice(t)).ToList();
        AddModuleBox.SelectedIndex = 0;
        _activeZoneList = LeftList;

        _saveTimer.Tick += (_, _) => Save(); // live mode: just save
        _previewTimer.Tick += (_, _) => { _previewTimer.Stop(); RefreshPreview(); };
        PreviewFrame.SizeChanged += (_, _) => SchedulePreview();
        Closing += (_, _) => SaveNow(); // don't lose the last change

        LoadDesignList(_getActiveName());
    }

    // ---------- Designs ----------

    private sealed record DesignItem(TaskbarDesign Design, bool InUse, bool BuiltIn)
    {
        /// <summary>For the design tiles: the name, and a small second line.</summary>
        public string Name => Design.Name;
        public string Notes => InUse && BuiltIn ? "In use · built-in" : InUse ? "In use" : BuiltIn ? "Built-in" : "Your design";
        public Visibility InUseMark => InUse ? Visibility.Visible : Visibility.Collapsed;

        public override string ToString()
        {
            var notes = new List<string>();
            if (InUse) notes.Add("in use");
            if (BuiltIn) notes.Add("built-in");
            return notes.Count == 0 ? Design.Name : $"{Design.Name}  ({string.Join(", ", notes)})";
        }
    }

    /// <summary>An entry in the "Add" dropdown: just the module's plain name.</summary>
    private sealed record ModuleChoice(ModuleType Type)
    {
        public override string ToString() => Type switch
        {
            ModuleType.Menu => "Menu button",
            ModuleType.Start => "Start button",
            ModuleType.WindowList => "Window list",
            ModuleType.PinnedApps => "Pinned apps",
            ModuleType.QuickSettings => "Quick Settings",
            ModuleType.ShowDesktop => "Show desktop",
            ModuleType.WindowsSearch => "Windows search button",
            ModuleType.SearchBox => "Search box (apps + web)",
            ModuleType.SystemStats => "CPU and memory",
            ModuleType.Media => "Media controls",
            ModuleType.Tray => "Tray icons",
            _ => Type.ToString(),
        };
    }

    /// <summary>Reads the designs from disk and selects <paramref name="selectName"/>.</summary>
    private void LoadDesignList(string? selectName)
    {
        SaveNow();
        var (designs, errors) = TaskbarDesigns.LoadAll();
        _designs = designs;
        if (errors.Count > 0)
            ShowStatus("Some design files could not be read: " + string.Join(" ", errors), error: true);

        string active = _getActiveName();
        _suppress = true;
        DesignList.ItemsSource = _designs
            .Select(d => new DesignItem(d, d.Name.Equals(active, StringComparison.CurrentCultureIgnoreCase), TaskbarDesigns.IsBuiltIn(d.Name)))
            .ToList();
        _suppress = false;

        var toSelect = DesignList.Items.Cast<DesignItem>()
            .FirstOrDefault(i => i.Design.Name.Equals(selectName, StringComparison.CurrentCultureIgnoreCase))
            ?? DesignList.Items.Cast<DesignItem>().FirstOrDefault();
        DesignList.SelectedItem = toSelect;
        if (toSelect == null) ShowDesign(null);
    }

    private void DesignList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        SaveNow(); // finish the previous design first
        ShowDesign((DesignList.SelectedItem as DesignItem)?.Design);
    }

    private void ShowDesign(TaskbarDesign? design)
    {
        _design = design;
        _selected = null;
        RefreshZones();
        BuildLookOptions();
        BuildModuleOptions();
        RefreshPreview();
        UseButton.IsEnabled = design != null;
    }

    private string UniqueName(string wanted)
    {
        string name = wanted;
        for (int n = 2; _designs.Any(d => d.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase)); n++)
            name = $"{wanted} {n}";
        return name;
    }

    private void NewDesign_Click(object sender, RoutedEventArgs e)
    {
        var design = new TaskbarDesign
        {
            Name = UniqueName("New design"),
            Left = { TaskbarModule.Create(ModuleType.Menu), TaskbarModule.Create(ModuleType.WindowList) },
            Right = { TaskbarModule.Create(ModuleType.Clock) },
        };
        SaveAndSelect(design);
    }

    private void DuplicateDesign_Click(object sender, RoutedEventArgs e)
    {
        if (_design == null) return;
        SaveNow();
        var copy = TaskbarDesigns.Clone(_design);
        copy.Name = UniqueName(_design.Name + " copy");
        SaveAndSelect(copy);
    }

    private void SaveAndSelect(TaskbarDesign design)
    {
        try
        {
            TaskbarDesigns.Save(design);
            LoadDesignList(design.Name);
            ShowStatus($"Created \"{design.Name}\".");
        }
        catch (Exception ex)
        {
            ShowStatus($"Could not create the design: {ex.Message}", error: true);
        }
    }

    private void DeleteDesign_Click(object sender, RoutedEventArgs e)
    {
        if (_design == null) return;
        string name = _design.Name;
        bool builtIn = TaskbarDesigns.IsBuiltIn(name);

        var question = builtIn
            ? $"Reset the built-in design \"{name}\" to its original?\nYour changes to it are lost."
            : $"Delete the design \"{name}\"?";
        if (MessageBox.Show(this, question, "Taskbar designer", MessageBoxButton.YesNo, MessageBoxImage.Question,
                MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        try
        {
            _saveTimer.Stop(); // don't write it back
            _design = null;
            TaskbarDesigns.Delete(name);

            // The taskbar can't keep using a design that no longer exists.
            if (!builtIn && name.Equals(_getActiveName(), StringComparison.CurrentCultureIgnoreCase))
                _setActive(TaskbarDesigns.DefaultName);

            LoadDesignList(builtIn ? name : null); // a built-in comes back in its original form
            ShowStatus(builtIn ? $"\"{name}\" was reset to the original." : $"Deleted \"{name}\".");
        }
        catch (Exception ex)
        {
            ShowStatus($"Could not delete the design: {ex.Message}", error: true);
        }
    }

    private void ImportDesign_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Import a taskbar design", Filter = "Taskbar design|*.json|All files|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        SaveNow();
        try
        {
            var design = TaskbarDesigns.Import(dialog.FileName);
            design.Name = UniqueName(design.Name); // never overwrite a design you already have
            TaskbarDesigns.Save(design);
            LoadDesignList(design.Name);
            ShowStatus($"Imported \"{design.Name}\".");
        }
        catch (Exception ex)
        {
            ShowStatus($"Could not import that file: {ex.Message}", error: true);
        }
    }

    private void ExportDesign_Click(object sender, RoutedEventArgs e)
    {
        if (_design == null) return;
        SaveNow();
        var dialog = new SaveFileDialog
        {
            Title = "Export a taskbar design",
            Filter = "Taskbar design|*.json",
            FileName = System.IO.Path.GetFileName(TaskbarDesigns.PathFor(_design.Name)),
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            TaskbarDesigns.Export(_design, dialog.FileName);
            ShowStatus($"Exported \"{_design.Name}\" to {dialog.FileName}. Pinned apps and buttons keep their paths from this PC.");
        }
        catch (Exception ex)
        {
            ShowStatus($"Could not export: {ex.Message}", error: true);
        }
    }

    private void UseDesign_Click(object sender, RoutedEventArgs e)
    {
        if (_design == null) return;
        SaveNow();
        _setActive(_design.Name);
        LoadDesignList(_design.Name);
        ShowStatus($"The taskbar now uses \"{_design.Name}\".");
    }

    /// <summary>Renames the design: writes it under the new name, removes the old file, keeps it in use if it was.</summary>
    private void RenameDesign(string newName)
    {
        if (_design == null) return;
        string oldName = _design.Name;
        newName = newName.Trim();
        if (newName.Length == 0 || newName == oldName) return;

        if (_designs.Any(d => d != _design && d.Name.Equals(newName, StringComparison.CurrentCultureIgnoreCase)))
        {
            ShowStatus($"There is already a design called \"{newName}\".", error: true);
            BuildLookOptions(); // put the old name back in the box
            return;
        }

        try
        {
            _saveTimer.Stop();
            bool wasActive = oldName.Equals(_getActiveName(), StringComparison.CurrentCultureIgnoreCase);
            _design.Name = newName;
            TaskbarDesigns.Save(_design);

            // Same file when only the capitals changed (Windows file names ignore case): don't delete it then.
            if (!string.Equals(TaskbarDesigns.PathFor(oldName), TaskbarDesigns.PathFor(newName), StringComparison.OrdinalIgnoreCase))
                TaskbarDesigns.Delete(oldName);

            if (wasActive) _setActive(newName);
            LoadDesignList(newName);
            ShowStatus(TaskbarDesigns.IsBuiltIn(oldName)
                ? $"Renamed to \"{newName}\". The built-in \"{oldName}\" is back in its original form."
                : $"Renamed to \"{newName}\".");
        }
        catch (Exception ex)
        {
            ShowStatus($"Could not rename: {ex.Message}", error: true);
        }
    }

    // ---------- Zones and modules ----------

    private IEnumerable<(ListBox list, List<TaskbarModule> zone)> Zones()
    {
        if (_design == null) yield break;
        yield return (LeftList, _design.Left);
        yield return (CenterList, _design.Center);
        yield return (RightList, _design.Right);
    }

    /// <summary>Refills the three zone lists and selects <see cref="_selected"/> again.</summary>
    private void RefreshZones()
    {
        _suppress = true;
        foreach (var list in new[] { LeftList, CenterList, RightList })
            list.ItemsSource = null;
        foreach (var (list, zone) in Zones())
        {
            list.ItemsSource = zone;
            list.SelectedItem = _selected != null && zone.Contains(_selected) ? _selected : null;
            if (list.SelectedItem != null) _activeZoneList = list;
        }
        _suppress = false;
    }

    private void ZoneList_GotFocus(object sender, RoutedEventArgs e) => _activeZoneList = (ListBox)sender;

    private void ZoneList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        var list = (ListBox)sender;
        if (list.SelectedItem is not TaskbarModule module) return;
        if (module == _selected) return; // same module (e.g. after a list refresh): keep the option boxes and their focus

        // Only one module is selected at a time, across all three zones.
        _suppress = true;
        foreach (var other in new[] { LeftList, CenterList, RightList })
            if (other != list) other.SelectedItem = null;
        _suppress = false;

        _selected = module;
        _activeZoneList = list;
        BuildModuleOptions();
        OptionTabs.SelectedItem = ModuleTab;
    }

    /// <summary>The zone and position of the selected module.</summary>
    private (List<TaskbarModule> zone, int index, int zoneNumber)? Selection()
    {
        int number = 0;
        foreach (var (_, zone) in Zones())
        {
            int index = _selected == null ? -1 : zone.IndexOf(_selected);
            if (index >= 0) return (zone, index, number);
            number++;
        }
        return null;
    }

    private List<TaskbarModule>? ZoneOf(ListBox? list) => Zones().FirstOrDefault(z => z.list == list).zone;

    private void AddModule_Click(object sender, RoutedEventArgs e)
    {
        if (_design == null || AddModuleBox.SelectedItem is not ModuleChoice choice) return;

        var module = TaskbarModule.Create(choice.Type);
        if (Selection() is var (zone, index, _))
            zone.Insert(index + 1, module); // right after the selected module
        else
            (ZoneOf(_activeZoneList) ?? _design.Left).Add(module);

        _selected = module;
        AfterStructureChange();
    }

    private void RemoveModule_Click(object sender, RoutedEventArgs e)
    {
        if (Selection() is not var (zone, index, _)) return;
        zone.RemoveAt(index);
        _selected = zone.Count == 0 ? null : zone[Math.Min(index, zone.Count - 1)];
        AfterStructureChange();
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => MoveWithinZone(-1);

    private void MoveDown_Click(object sender, RoutedEventArgs e) => MoveWithinZone(+1);

    private void MoveWithinZone(int step)
    {
        if (Selection() is not var (zone, index, _)) return;
        int target = index + step;
        if (target < 0 || target >= zone.Count) return;
        (zone[index], zone[target]) = (zone[target], zone[index]);
        AfterStructureChange();
    }

    private void MoveZoneLeft_Click(object sender, RoutedEventArgs e) => MoveToZone(-1);

    private void MoveZoneRight_Click(object sender, RoutedEventArgs e) => MoveToZone(+1);

    private void MoveToZone(int step)
    {
        if (Selection() is not var (zone, index, number)) return;
        var zones = Zones().Select(z => z.zone).ToList();
        int target = number + step;
        if (target < 0 || target >= zones.Count) return;

        zone.RemoveAt(index);
        // Moving right puts it at the start of the next zone, moving left at the end: it stays "next to" where it was.
        if (step > 0) zones[target].Insert(0, _selected!);
        else zones[target].Add(_selected!);
        AfterStructureChange();
    }

    private void AfterStructureChange()
    {
        RefreshZones();
        BuildModuleOptions();
        Changed();
    }

    // ---------- Module options ----------

    private void BuildModuleOptions()
    {
        ModuleOptions.Children.Clear();
        var m = _selected;
        if (m == null)
        {
            Hint(ModuleOptions, "Select a module in one of the zones to change its options.");
            return;
        }

        Header(ModuleOptions, m.Type switch
        {
            ModuleType.WindowList => "Window list",
            ModuleType.PinnedApps => "Pinned apps",
            ModuleType.QuickSettings => "Quick Settings",
            _ => m.Type.ToString(),
        });

        switch (m.Type)
        {
            case ModuleType.Menu:
                Hint(ModuleOptions, "Opens the Windows Customizer menu: profiles, taskbar options, exit. No options.");
                break;
            case ModuleType.Start:
                CheckField(ModuleOptions, "Open Windows Customizer's start menu", m.CustomStart == true,
                    v => m.CustomStart = v ? true : null,
                    "Off: opens the Windows Start menu. On: opens our start menu next to the button " +
                    "(its look is set in the main window under Start menu > \"Start menu settings...\").");
                break;
            case ModuleType.Battery:
                Hint(ModuleOptions, "Battery level; hidden on PCs without a battery. No options.");
                break;
            case ModuleType.QuickSettings:
                Hint(ModuleOptions, "Opens Windows Quick Settings (Win+A). No options.");
                break;
            case ModuleType.WindowList:
                Hint(ModuleOptions, "One button per open window. Icon-only buttons and the button width are on the Look tab.");
                break;

            case ModuleType.Clock:
                TextField(ModuleOptions, "Time format", m.TimeFormat ?? "", v => m.TimeFormat = Empty(v),
                    "Empty = short time. Examples: HH:mm, h:mm tt, HH:mm:ss", ClockExample);
                TextField(ModuleOptions, "Date format", m.DateFormat ?? "d",
                    v => m.DateFormat = v.Trim().Length == 0 ? "" : v,
                    "Empty = no date. Examples: d, ddd d MMM, dddd d MMMM", ClockExample);
                break;

            case ModuleType.Spacer:
                NumberField(ModuleOptions, "Width (pixels)", m.Width ?? 16, v => m.Width = v, 0, 1000);
                break;

            case ModuleType.Text:
                TextField(ModuleOptions, "Text", m.Text ?? "", v => m.Text = v);
                break;

            case ModuleType.Button:
                TextField(ModuleOptions, "Text", m.Text ?? "", v => m.Text = Empty(v), "Shown next to the icon. Empty = icon only.");
                TextField(ModuleOptions, "Icon", m.Icon ?? "", v => m.Icon = Empty(v),
                    "A Segoe icon code like E721 (search), E8B7 (folder), E774 (globe), E713 (settings), or a .ico/.exe file. Empty = the target's own icon.");
                FileField(ModuleOptions, "Opens", m.Target ?? "", v => m.Target = Empty(v),
                    "A program, file, folder or web address (https://...).");
                TextField(ModuleOptions, "Arguments", m.Arguments ?? "", v => m.Arguments = Empty(v), "Optional, for programs.");
                TextField(ModuleOptions, "Tooltip", m.ToolTip ?? "", v => m.ToolTip = Empty(v));
                break;

            case ModuleType.PinnedApps:
                PinnedAppsEditor(m);
                break;

            case ModuleType.ShowDesktop:
                Hint(ModuleOptions, "A thin line: click it to minimize all windows, click again to bring them back. No options.");
                break;
            case ModuleType.WindowsSearch:
                Hint(ModuleOptions, "Opens Windows search (Win+S). No options.");
                break;
            case ModuleType.SystemStats:
                Hint(ModuleOptions, "CPU and memory use, updated every second. Click it to open Task Manager. No options.");
                break;

            case ModuleType.Volume:
                CheckField(ModuleOptions, "Show the volume as a number", m.ShowValue == true, v => m.ShowValue = v ? true : null);
                Hint(ModuleOptions, "Click to mute, mouse wheel to change the volume, right-click for sound settings.");
                break;

            case ModuleType.SearchBox:
                NumberField(ModuleOptions, "Width (pixels)", m.Width ?? 220, v => m.Width = v, 80, 800);
                TextField(ModuleOptions, "Placeholder text", m.Text ?? "", v => m.Text = Empty(v), "Empty = \"Search apps and the web\".");
                TextField(ModuleOptions, "Web search address", m.SearchUrl ?? "", v => m.SearchUrl = Empty(v),
                    "{0} is replaced by what you typed. Empty = the one from Search settings (main window).");
                Hint(ModuleOptions, "Clicking the box opens the search window next to it. Its look (colors, size, results) " +
                    "is set in the main window under Search > \"Search settings...\", shared with the Windows key search.");
                break;

            case ModuleType.Tray:
                CheckField(ModuleOptions, "Only pinned icons on the bar, the rest behind ^", m.TrayOverflow == true,
                    v => m.TrayOverflow = v ? true : null,
                    "At first the system icons are pinned. Ctrl+click an icon on the bar or in the ^ flyout to move it.");
                NumberField(ModuleOptions, "Icon size (pixels)", m.IconSize ?? 16, v => m.IconSize = v, 10, 48);
                NumberField(ModuleOptions, "Space around each icon", m.IconSpacing ?? 5, v => m.IconSpacing = v, 0, 30);
                NumberField(ModuleOptions, "Icons per row in the ^ flyout", m.FlyoutColumns ?? 5, v => m.FlyoutColumns = (int)v, 1, 20);
                ColorField(ModuleOptions, "Flyout background", m.FlyoutBackground ?? "", v => m.FlyoutBackground = Empty(v),
                    "Empty = dark gray. #AARRGGBB for see-through.");
                Hint(ModuleOptions,
                    "The notification-area icons of all programs; clicks and right-click menus go to the programs as usual. " +
                    "While a bar with this module is showing, programs send their icons here instead of to the Windows taskbar, " +
                    "so the Windows tray may stop getting new icons (best combined with \"Hide the Windows taskbar\"). " +
                    "Icons go back to Windows when the bar closes. The preview here shows a placeholder.");
                break;

            case ModuleType.Media:
                NumberField(ModuleOptions, "Maximum width of the title (pixels)", m.Width ?? 240, v => m.Width = v, 60, 800);
                Hint(ModuleOptions, "Shows what's playing in Spotify, browsers and other media apps, with previous / play-pause / next. Hidden when nothing plays.");
                break;
        }
    }

    private static string? Empty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>Shows what a clock format looks like right now (or that it's broken).</summary>
    private static string? ClockExample(string format)
    {
        if (string.IsNullOrWhiteSpace(format)) return null;
        try { return "Now: " + DateTime.Now.ToString(format, CultureInfo.CurrentCulture); }
        catch (FormatException) { return "Not a valid format"; }
    }

    private void PinnedAppsEditor(TaskbarModule m)
    {
        m.Apps ??= new List<string>();
        var apps = m.Apps;

        CheckField(ModuleOptions, "Dock style", m.ShowRunning != false, v => m.ShowRunning = v ? null : false,
            "A dot under running apps; clicking a running app switches to it instead of opening it again, " +
            "and its windows don't get a second button in the window list.");

        Label(ModuleOptions, "Programs and shortcuts");
        var list = new ListBox { Height = 160, ItemsSource = apps };
        ModuleOptions.Children.Add(list);

        void Refresh(int select)
        {
            list.ItemsSource = null;
            list.ItemsSource = apps;
            list.SelectedIndex = Math.Min(select, apps.Count - 1);
            RefreshZones(); // the zone list shows the count
            Changed();
        }

        var buttons = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        buttons.Children.Add(SmallButton("Add...", () =>
        {
            var dialog = new OpenFileDialog
            {
                Title = "Pin a program or shortcut",
                Filter = "Programs and shortcuts|*.exe;*.lnk|All files|*.*",
                Multiselect = true,
            };
            if (dialog.ShowDialog(this) != true) return;
            apps.AddRange(dialog.FileNames);
            Refresh(apps.Count - 1);
        }));
        buttons.Children.Add(SmallButton("Remove", () =>
        {
            if (list.SelectedIndex < 0) return;
            int i = list.SelectedIndex;
            apps.RemoveAt(i);
            Refresh(i);
        }));
        buttons.Children.Add(SmallButton("▲", () =>
        {
            int i = list.SelectedIndex;
            if (i <= 0) return;
            (apps[i - 1], apps[i]) = (apps[i], apps[i - 1]);
            Refresh(i - 1);
        }));
        buttons.Children.Add(SmallButton("▼", () =>
        {
            int i = list.SelectedIndex;
            if (i < 0 || i >= apps.Count - 1) return;
            (apps[i + 1], apps[i]) = (apps[i], apps[i + 1]);
            Refresh(i + 1);
        }));
        ModuleOptions.Children.Add(buttons);
        Hint(ModuleOptions, "Tip: Start menu shortcuts are in %ProgramData%\\Microsoft\\Windows\\Start Menu\\Programs.");
    }

    // ---------- Look options ----------

    private void BuildLookOptions()
    {
         LookOptions.Children.Clear();
        if (_design == null) return;
        var design = _design;
        var look = design.Look ??= new TaskbarLook();

        // Name: renamed when the box loses focus or Enter is pressed (not on every key).
        Label(LookOptions, "Design name");
        var nameBox = new TextBox { Text = design.Name };
        nameBox.LostFocus += (_, _) => RenameDesign(nameBox.Text);
        nameBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) RenameDesign(nameBox.Text); };
        LookOptions.Children.Add(nameBox);

        Header(LookOptions, "Behavior");
        Label(LookOptions, "Screen edge");
        var edgeBox = new ComboBox { ItemsSource = new[] { "Use the main setting", "Top", "Bottom" } };
        edgeBox.SelectedIndex = design.Edge switch { TaskbarEdge.Top => 1, TaskbarEdge.Bottom => 2, _ => 0 };
        edgeBox.SelectionChanged += (_, _) =>
        {
            design.Edge = edgeBox.SelectedIndex switch { 1 => TaskbarEdge.Top, 2 => TaskbarEdge.Bottom, _ => null };
            Changed();
        };
        LookOptions.Children.Add(edgeBox);
        CheckField(LookOptions, "Auto-hide", design.AutoHide, v => design.AutoHide = v,
            "Slides away when not in use; move the mouse to the screen edge to bring it back. Doesn't reserve screen space.");

        Header(LookOptions, "Size and colors");
        NumberField(LookOptions, "Height (pixels)", look.Height, v => look.Height = v, 20, 200);
        ColorField(LookOptions, "Background", look.Background, v => look.Background = v,
            "#RRGGBB, or #AARRGGBB to make it see-through (AA: 00 invisible ... FF solid).");
        ColorField(LookOptions, "Text and icons", look.Foreground, v => look.Foreground = v);
        ColorField(LookOptions, "Accent (active window line)", look.Accent ?? "", v => look.Accent = Empty(v),
            "Empty = the Windows accent color.");

        Header(LookOptions, "Font");
        FontField(LookOptions, look);
        NumberField(LookOptions, "Font size", look.FontSize, v => look.FontSize = v, 6, 48);

        Header(LookOptions, "Shape");
        NumberField(LookOptions, "Bar corner radius", look.CornerRadius, v => look.CornerRadius = v, 0, 100);
        NumberField(LookOptions, "Button corner radius", look.ButtonCornerRadius, v => look.ButtonCornerRadius = v, 0, 50);

        Header(LookOptions, "Window buttons");
        CheckField(LookOptions, "Icons only (title on hover)", look.WindowIconsOnly, v => look.WindowIconsOnly = v);
        NumberField(LookOptions, "Maximum button width", look.WindowButtonWidth, v => look.WindowButtonWidth = v, 40, 600);
        CheckField(LookOptions, "Group windows by app", look.GroupWindows, v => look.GroupWindows = v,
            "One button per app with a window count; clicking it lets you pick the window.");
        CheckField(LookOptions, "Preview windows on hover", look.Thumbnails, v => look.Thumbnails = v,
            "Live thumbnails above the button; click one to switch to that window.");
        CheckField(LookOptions, "Middle-click closes a window", look.MiddleClickCloses, v => look.MiddleClickCloses = v);

        Header(LookOptions, "Icon colors");
        CheckField(LookOptions, "Tint icons in one color", look.TintIcons, v => look.TintIcons = v,
            "Window and app icons in one color for a clean look; full color while the mouse is on them.");
        ColorField(LookOptions, "Tint color", look.IconTint ?? "", v => look.IconTint = Empty(v), "Empty = the text color.");
        CheckField(LookOptions, "Glow in each app's own color", look.IconGlow, v => look.IconGlow = v,
            "Hover and active highlights of window buttons take the main color of the app's icon.");
        CheckField(LookOptions, "Smooth hover fade", look.HoverFade, v => look.HoverFade = v,
            "Highlights fade in and out instead of switching instantly.");

        Header(LookOptions, "Effects");
        CheckField(LookOptions, "Blur behind the bar (experimental)", look.Blur, v => look.Blur = v,
            "Use a see-through background. Shows on the real taskbar only, not in this preview, and not with a floating dock yet.");

        BuildMaterialOptions(look);

        CheckField(LookOptions, "Floating dock", look.Floating != null, on =>
        {
            look.Floating = on ? new FloatingDock() : null;
            BuildLookOptions(); // show or hide the dock options
        }, "Centered, only as wide as its contents, away from the screen edge.");
        if (look.Floating is { } dock)
        {
            NumberField(LookOptions, "Distance from the edge", dock.Margin, v => dock.Margin = v, 0, 100);
            CheckField(LookOptions, "Reserve screen space", dock.ReserveSpace, v => dock.ReserveSpace = v,
                "Off: the dock floats over windows instead of pushing them aside.");
        }
    }

    // ---------- Material (shader) ----------

    /// <summary>The material dropdown, its description, its settings (made from material.json) and any compile error.</summary>
    private void BuildMaterialOptions(TaskbarLook look)
    {
        Header(LookOptions, "Material (shader)");
        var materials = Materials.LoadAll();
        Label(LookOptions, "Material");
        var box = new ComboBox { ItemsSource = new[] { "(none: plain background)" }.Concat(materials.Select(m => m.Name)).ToList() };
        var current = materials.FirstOrDefault(m => m.Name.Equals(look.Material, StringComparison.CurrentCultureIgnoreCase));
        box.SelectedIndex = current == null ? 0 : materials.IndexOf(current) + 1;
        // Only the part below the dropdown is rebuilt when it changes: rebuilding the dropdown itself while it is
        // closing (WPF-UI animates it) crashes WPF.
        var details = new StackPanel();
        box.SelectionChanged += (_, _) =>
        {
            look.Material = box.SelectedIndex <= 0 ? null : materials[box.SelectedIndex - 1].Name;
            Changed();
            FillMaterialDetails(details, look, box.SelectedIndex <= 0 ? null : materials[box.SelectedIndex - 1]);
        };
        LookOptions.Children.Add(box);
        LookOptions.Children.Add(details);
        FillMaterialDetails(details, look, current);

        var folders = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        folders.Children.Add(SmallButton("Your materials folder", () =>
        {
            System.IO.Directory.CreateDirectory(Materials.UserFolder);
            ShellLaunch.OpenFolder(Materials.UserFolder);
        }));
        folders.Children.Add(SmallButton("Built-in materials", () => ShellLaunch.OpenFolder(Materials.BuiltInFolder)));
        folders.Children.Add(SmallButton("Import material...", () =>
        {
            if (MaterialFiles.Import(this) is not { } name) return;
            look.Material = name; // use it right away
            Changed();
            Dispatcher.BeginInvoke(BuildLookOptions);
        }));
        folders.Children.Add(SmallButton("Export this material...", () =>
        {
            if (Materials.Find(look.Material) is { } chosen) MaterialFiles.Export(this, chosen);
        }));
        LookOptions.Children.Add(folders);
        Hint(LookOptions, "Your own: copy a built-in material's folder into your materials folder, give it a new name in material.json, " +
                          "and edit material.hlsl. Reopen this tab to see changes.");
    }

    /// <summary>The chosen material's description, compile error (if any) and settings.</summary>
    private void FillMaterialDetails(Panel details, TaskbarLook look, Material? current)
    {
        details.Children.Clear();
        if (current == null)
        {
            Hint(details, "A material draws the bar's surface with a shader: glass, glow, painted styles, animations. Icons and text stay sharp on top.");
            return;
        }
        Hint(details, (current.Description ?? "") + (current.Animated
            ? $" Animated: redrawn up to {MaterialClock.FramesPerSecond} times a second, paused while a fullscreen app or game is in front."
            : ""));

        // Doesn't compile: say why (the line numbers are those of material.hlsl).
        Materials.Compile(current, out var error);
        if (error != null)
            details.Children.Add(new TextBlock
            {
                Text = "This material doesn't compile, so the bar shows its plain background:\n" + error,
                Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
                FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 11,
            });

        var values = look.MaterialValues ??= new Dictionary<string, string>();
        foreach (var parameter in current.Parameters)
        {
            string value = values.TryGetValue(parameter.Name, out var v) ? v : parameter.Default;
            if (parameter.Type == MaterialParameterType.Color)
                ColorField(details, parameter.Name, value, text => values[parameter.Name] = text, parameter.Hint);
            else
                SliderField(details, parameter, value, number => values[parameter.Name] = number.ToString("0.###", CultureInfo.InvariantCulture));
        }
    }

    /// <summary>A slider with its value next to it.</summary>
    private void SliderField(Panel panel, MaterialParameter parameter, string value, Action<double> set)
    {
        Label(panel, parameter.Name);
        double number = double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : parameter.Min;
        var row = new DockPanel();
        var shown = new TextBlock { Width = 44, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(shown, Dock.Right);
        var slider = new Slider { Minimum = parameter.Min, Maximum = Math.Max(parameter.Max, parameter.Min + 0.001), Value = number, VerticalAlignment = VerticalAlignment.Center };
        shown.Text = number.ToString("0.##", CultureInfo.CurrentCulture);
        slider.ValueChanged += (_, _) =>
        {
            shown.Text = slider.Value.ToString("0.##", CultureInfo.CurrentCulture);
            set(slider.Value);
            Changed();
        };
        row.Children.Add(shown);
        row.Children.Add(slider);
        panel.Children.Add(row);
        if (parameter.Hint != null) Hint(panel, parameter.Hint);
    }

    // ---------- Preview and saving ----------

    /// <summary>Called after every edit: updates the preview soon and saves a little later.</summary>
    private void Changed()
    {
        SchedulePreview();
        if (Live)
        {
            _saveTimer.Stop();
            _saveTimer.Start();
        }
        else
        {
            SetUnapplied(true);
        }
    }

    // ---------- Live update or Apply ----------

    /// <summary>Remembered while the app runs, so reopening the designer keeps the choice.</summary>
    private static bool _liveSetting = true;

    private bool Live => LiveCheck.IsChecked == true;
    private bool _unapplied;

    private void SetUnapplied(bool unapplied)
    {
        _unapplied = unapplied;
        ApplyButton.IsEnabled = unapplied;
        if (unapplied) ShowStatus("Changes not applied yet: press \"Apply to taskbar\".");
    }

    private void LiveCheck_Click(object sender, RoutedEventArgs e)
    {
        _liveSetting = Live;
        if (Live && _unapplied) Apply_Click(sender, e); // switching live on applies what's pending
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        Save();
        SetUnapplied(false);
        ShowStatus("Applied to the taskbar.");
    }

    /// <summary>
    /// Before leaving a design (switching, closing, renaming...): saves it. With live updates off and
    /// changes not applied, asks first; "No" throws those changes away.
    /// </summary>
    private void SaveNow()
    {
        _saveTimer.Stop();
        if (_design == null) return;
        if (!Live && _unapplied)
        {
            var answer = MessageBox.Show(this, $"Apply your changes to \"{_design.Name}\"?", "Taskbar designer",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
            SetUnapplied(false);
            if (answer != MessageBoxResult.Yes) return; // the design is read from disk again when shown next
        }
        Save();
    }

    private void SchedulePreview()
    {
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    /// <summary>Draws the design with the same control the real taskbar uses.</summary>
    private void RefreshPreview()
    {
        if (_design == null)
        {
            PreviewHost.Content = null;
            return;
        }

        var view = new TaskbarView(_design)
        {
            IsHitTestVisible = false, // a preview: clicking shouldn't open Start or launch programs
        };
        view.Height = view.BarHeight;
        view.Margin = new Thickness(view.DockMargin, view.DockMargin, view.DockMargin, 0);
        PreviewFrame.Height = Math.Max(60, view.BarHeight + 2 * view.DockMargin + 8);
        PreviewHost.Content = view;
    }

    private void Save()
    {
        _saveTimer.Stop();
        if (_design == null) return;
        try
        {
            TaskbarDesigns.Save(_design);
        }
        catch (Exception ex)
        {
            ShowStatus($"Could not save \"{_design.Name}\": {ex.Message}", error: true);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ShowStatus(string text, bool error = false)
    {
        StatusText.Text = text;
        StatusText.Foreground = error ? Brushes.IndianRed : Brushes.Gray;
    }

    // ---------- Small UI helpers for the option panels ----------

    private static void Header(Panel panel, string text) =>
        panel.Children.Add(new TextBlock
        {
            Text = text,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, panel.Children.Count == 0 ? 0 : 14, 0, 4),
        });

    private static void Label(Panel panel, string text) =>
        panel.Children.Add(new TextBlock { Text = text, Margin = new Thickness(0, 8, 0, 2) });

    private static void Hint(Panel panel, string text) =>
        panel.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = Brushes.Gray,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        });

    private static Button SmallButton(string text, Action onClick)
    {
        var button = new Button { Content = text, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 6, 0) };
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <param name="example">Optional: text shown under the box for the current value (like a clock format example).</param>
    private void TextField(Panel panel, string label, string value, Action<string> set, string? hint = null,
        Func<string, string?>? example = null)
    {
        Label(panel, label);
        var box = new TextBox { Text = value };
        panel.Children.Add(box);

        TextBlock? exampleText = null;
        if (example != null)
        {
            exampleText = new TextBlock { Foreground = Brushes.Gray, FontSize = 11, Text = example(value) ?? "" };
            panel.Children.Add(exampleText);
        }
        if (hint != null) Hint(panel, hint);

        box.TextChanged += (_, _) =>
        {
            set(box.Text);
            if (exampleText != null) exampleText.Text = example!(box.Text) ?? "";
            RefreshZoneText();
            Changed();
        };
    }

    /// <summary>A text box plus "Browse..." for a file.</summary>
    private void FileField(Panel panel, string label, string value, Action<string> set, string? hint = null)
    {
        Label(panel, label);
        var row = new DockPanel();
        var browse = new Button { Content = "Browse...", Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(6, 0, 0, 0) };
        DockPanel.SetDock(browse, Dock.Right);
        var box = new TextBox { Text = value };
        row.Children.Add(browse);
        row.Children.Add(box);
        panel.Children.Add(row);
        if (hint != null) Hint(panel, hint);

        box.TextChanged += (_, _) => { set(box.Text); RefreshZoneText(); Changed(); };
        browse.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog { Title = label, Filter = "All files|*.*" };
            if (dialog.ShowDialog(this) == true) box.Text = dialog.FileName; // TextChanged saves it
        };
    }

    /// <summary>A number box; an invalid number gets a red border and is not used.</summary>
    private void NumberField(Panel panel, string label, double value, Action<double> set, double min, double max)
    {
        Label(panel, $"{label}  ({min:0}–{max:0})");
        var box = new TextBox { Text = value.ToString("0.##", CultureInfo.CurrentCulture) };
        var normalBorder = box.BorderBrush;
        panel.Children.Add(box);

        box.TextChanged += (_, _) =>
        {
            bool ok = double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double v)
                      || double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
            ok = ok && v >= min && v <= max;
            box.BorderBrush = ok ? normalBorder : Brushes.IndianRed;
            if (!ok) return;
            set(v);
            RefreshZoneText();
            Changed();
        };
    }

    private void CheckField(Panel panel, string label, bool value, Action<bool> set, string? hint = null)
    {
        var check = new CheckBox { Content = label, IsChecked = value, Margin = new Thickness(0, 8, 0, 0) };
        check.Click += (_, _) => { set(check.IsChecked == true); Changed(); };
        panel.Children.Add(check);
        if (hint != null) Hint(panel, hint);
    }

    /// <summary>A color box with a swatch; an invalid color gets a red border (the bar then uses its default).</summary>
    private void ColorField(Panel panel, string label, string value, Action<string> set, string? hint = null)
    {
        Label(panel, label);
        var row = new DockPanel();
        var swatch = new Border
        {
            Width = 24, Height = 22, Margin = new Thickness(0, 0, 6, 0),
            BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1),
        };
        DockPanel.SetDock(swatch, Dock.Left);
        var box = new TextBox { Text = value, VerticalContentAlignment = VerticalAlignment.Center };
        var normalBorder = box.BorderBrush;
        row.Children.Add(swatch);
        row.Children.Add(box);
        panel.Children.Add(row);
        if (hint != null) Hint(panel, hint);

        void ShowSwatch()
        {
            bool ok = TaskbarView.IsValidColor(box.Text);
            box.BorderBrush = ok ? normalBorder : Brushes.IndianRed;
            swatch.Background = ok && !string.IsNullOrWhiteSpace(box.Text)
                ? new SolidColorBrush(TaskbarView.ParseColor(box.Text, Colors.Transparent))
                : Brushes.Transparent;
        }
        ShowSwatch();

        box.TextChanged += (_, _) =>
        {
            ShowSwatch();
            set(box.Text.Trim());
            Changed();
        };
    }

    private void FontField(Panel panel, TaskbarLook look)
    {
        Label(panel, "Font");
        var fonts = Fonts.SystemFontFamilies.Select(f => f.Source).OrderBy(n => n).ToList();
        var box = new ComboBox { IsEditable = true, ItemsSource = fonts, Text = look.FontFamily };
        panel.Children.Add(box);

        void Set()
        {
            look.FontFamily = string.IsNullOrWhiteSpace(box.Text) ? "Segoe UI" : box.Text.Trim();
            Changed();
        }
        box.SelectionChanged += (_, _) => { if (box.SelectedItem is string s) { box.Text = s; Set(); } };
        box.LostFocus += (_, _) => Set();
    }

    /// <summary>Module descriptions in the zone lists include options (like "Spacer (16 px)"): refresh their text.</summary>
    private void RefreshZoneText()
    {
        _suppress = true; // refreshing must not count as the user picking a module
        foreach (var list in new[] { LeftList, CenterList, RightList })
            list.Items.Refresh();
        _suppress = false;
    }
}
