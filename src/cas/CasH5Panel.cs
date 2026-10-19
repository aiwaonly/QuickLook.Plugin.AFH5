using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;

using Microsoft.Win32;

using QuickLook.Common.Helpers;
using QuickLook.Common.Plugin;

namespace QuickLook.Plugin.AFH5;

/// <summary>
/// The <c>.cas.h5</c> viewer: a searchable tree of the case's zones and settings
/// on the left, the selected entry's settings on the right.
///
/// The whole UI is built in code rather than XAML, so it ships as a single file
/// and follows the host's light or dark theme.
/// </summary>
public sealed class CasH5Panel : UserControl
{
    // ------------------------------------------------------------------ palette

    /// <summary>Every colour the panel uses, for one interface theme.</summary>
    private sealed record Palette(
        Color Window,
        Color Panel,
        Color PanelAlt,
        Color Border,
        Color Accent,
        Color Text,
        Color Muted,
        Color Disabled,
        Color[] Levels,
        Color String,
        Color Value,
        Color Selection,
        Color Hover)
    {
        /// <summary>The accent, dimmed, for the search box background.</summary>
        public Color AccentSoft => Color.FromArgb(0x24, Accent.R, Accent.G, Accent.B);
    }

    private static readonly Color[] DarkLevels =
    [
        Color.FromRgb(0x7A, 0xDC, 0xF5), // cyan
        Color.FromRgb(0x9C, 0xDC, 0x8C), // green
        Color.FromRgb(0xE8, 0xD2, 0x8C), // yellow
        Color.FromRgb(0xD8, 0xA8, 0xE8), // magenta
        Color.FromRgb(0x9C, 0xB8, 0xF0), // blue
        Color.FromRgb(0xF0, 0xA8, 0xA0), // red
    ];

    private static readonly Color[] LightLevels =
    [
        Color.FromRgb(0x0B, 0x63, 0x7A),
        Color.FromRgb(0x2C, 0x6E, 0x1E),
        Color.FromRgb(0x7A, 0x5A, 0x00),
        Color.FromRgb(0x76, 0x2A, 0x7E),
        Color.FromRgb(0x1F, 0x3F, 0x9E),
        Color.FromRgb(0x9E, 0x2A, 0x2A),
    ];

    private static readonly Palette Dark = new(
        Window: Color.FromRgb(0x18, 0x1A, 0x1F),
        Panel: Color.FromRgb(0x20, 0x23, 0x2A),
        PanelAlt: Color.FromRgb(0x27, 0x2B, 0x34),
        Border: Color.FromRgb(0x33, 0x38, 0x42),
        Accent: Color.FromRgb(0x4C, 0x9A, 0xFF),
        Text: Color.FromRgb(0xE6, 0xE8, 0xEC),
        Muted: Color.FromRgb(0x9A, 0xA1, 0xAE),
        Disabled: Color.FromRgb(0x5E, 0x66, 0x73),
        Levels: DarkLevels,
        String: Color.FromRgb(0xDC, 0xE3, 0xEC),
        Value: Color.FromRgb(0xA8, 0xB2, 0xC0),
        Selection: Color.FromRgb(0x2E, 0x46, 0x66),
        Hover: Color.FromRgb(0x38, 0x3F, 0x4D)
    );

    private static readonly Palette Light = new(
        Window: Color.FromRgb(0xF4, 0xF6, 0xF8),
        Panel: Color.FromRgb(0xFF, 0xFF, 0xFF),
        PanelAlt: Color.FromRgb(0xF0, 0xF2, 0xF5),
        Border: Color.FromRgb(0xD8, 0xDD, 0xE4),
        Accent: Color.FromRgb(0x18, 0x6A, 0xD8),
        Text: Color.FromRgb(0x1B, 0x1F, 0x26),
        Muted: Color.FromRgb(0x5A, 0x62, 0x70),
        Disabled: Color.FromRgb(0x9A, 0xA2, 0xAE),
        Levels: LightLevels,
        String: Color.FromRgb(0x1B, 0x1F, 0x26),
        Value: Color.FromRgb(0x3A, 0x42, 0x50),
        Selection: Color.FromRgb(0xCF, 0xE0, 0xF8),
        Hover: Color.FromRgb(0xE2, 0xE8, 0xF2)
    );

    private const string MonoFont = "Consolas, Cascadia Mono, Courier New";
    private const string UiFont = "Segoe UI";

    /// <summary>Sections whose children gain a type level (materials, boundary).</summary>
    private static readonly HashSet<string> TwoLevelSections =
    [
        "materials", "boundary",
    ];

    /// <summary>Sections listing named entries directly under the section node.</summary>
    private static readonly HashSet<string> OneLevelSections =
    [
        "solution", "report-definitions", "plotsets", "monitorsets", "surfaces",
        "mesh", "contours", "vectors", "pathlines", "xy-plot", "scene", "parameters",
    ];

    /// <summary>Beyond this the detail text is cut off; a preview does not need more.</summary>
    private const int MaxDetailCharacters = 400_000;

    private static readonly Regex KeyValue = new(
        "^(?<key>\"(?:[^\"\\\\]|\\\\.)*\")(?<colon>:\\s*)(?<rest>.+)$",
        RegexOptions.Compiled
    );

    // -------------------------------------------------------------------- state

    private Palette _palette = Dark;

    private readonly TreeView _tree;

    /// <summary>
    /// Read-only rather than a <see cref="TextBlock"/> so the settings text can be
    /// selected and copied; it accepts the same coloured <see cref="Run"/>s.
    /// </summary>
    private readonly RichTextBox _detailText;
    private readonly TextBlock _detailTitle;
    private readonly TextBlock _treeMeta;
    private readonly TextBox _search;
    private readonly Border _searchBox;
    private readonly Border _treePanel;
    private readonly Border _detailPanel;
    private readonly Border _treeHeader;
    private readonly Border _detailHeader;

    private TreeViewItem? _root;
    private int _entryCount;
    private string _filter = string.Empty;
    private Node? _hovered;

    // ------------------------------------------------------------------ building

    /// <summary>Creates the panel for a case file, reading it immediately.</summary>
    public CasH5Panel(string path, Themes theme)
    {
        _palette = PaletteFor(theme);

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        Unloaded += (_, _) => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

        _tree = new TreeView
        {
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(6, 4, 6, 8),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        _tree.SelectedItemChanged += OnSelectedItemChanged;
        _tree.MouseMove += OnTreeMouseMove;
        _tree.MouseLeave += (_, _) => SetHovered(null);

        _search = new TextBox { BorderThickness = new Thickness(0), Background = Brushes.Transparent };
        _search.TextChanged += (_, _) => ApplyFilter(_search.Text.Trim());
        AutomationProperties.SetName(_search, "Search settings");
        _searchBox = new Border
        {
            Child = _search,
            Padding = new Thickness(8, 5, 8, 5),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
        };

        _treeMeta = new TextBlock
        {
            FontFamily = new FontFamily(UiFont),
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 4, 0),
        };

        _treeHeader = new Border { Child = BuildTreeHeader(), BorderThickness = new Thickness(0, 0, 0, 1) };
        _treePanel = new Border
        {
            BorderThickness = new Thickness(1),
            Child = _tree,
            Margin = new Thickness(0, 0, 8, 0),
        };

        _detailTitle = new TextBlock
        {
            FontFamily = new FontFamily(MonoFont),
            FontSize = 14.5,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var detailDocument = new FlowDocument
        {
            FontFamily = new FontFamily(MonoFont),
            FontSize = 14.5,
            // Roughly 1.28x the font size, which keeps the JSON lines compact but
            // still easy to scan now that the pane holds selectable text.
            LineHeight = 18.5,
            PagePadding = new Thickness(0),
        };

        _detailText = new RichTextBox
        {
            Document = detailDocument,
            IsReadOnly = true,
            IsReadOnlyCaretVisible = true,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(14, 10, 14, 12),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            IsDocumentEnabled = true,
        };

        // A tight strip holding just the path of the selected entry.
        var detailStack = new StackPanel { Margin = new Thickness(14, 6, 14, 6) };
        detailStack.Children.Add(_detailTitle);
        _detailHeader = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Child = detailStack };

        var detailLayout = new DockPanel();
        DockPanel.SetDock(_detailHeader, Dock.Top);
        detailLayout.Children.Add(_detailHeader);
        detailLayout.Children.Add(_detailText);

        _detailPanel = new Border { BorderThickness = new Thickness(1), Child = detailLayout };

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60, GridUnitType.Star) });
        Grid.SetColumn(_treePanel, 0);
        Grid.SetColumn(_detailPanel, 1);
        body.Children.Add(_treePanel);
        body.Children.Add(_detailPanel);

        Content = body;
        ApplyPalette();

        Load(Path.GetFileName(path), CaseReader.Read(path));
    }

    /// <summary>Switches the colour palette.</summary>
    public void SetTheme(Themes theme)
    {
        var palette = PaletteFor(theme);
        if (palette == _palette)
        {
            return;
        }

        _palette = palette;
        ApplyPalette();
    }

    /// <summary>
    /// The palette for a requested theme.
    ///
    /// The host reports <see cref="Themes.None"/> when it has no explicit theme,
    /// which is what a system-driven light desktop produces. Treating that as dark
    /// was why the panel stayed dark on a light desktop, so fall back to the OS.
    /// </summary>
    private static Palette PaletteFor(Themes theme)
    {
        return theme switch
        {
            Themes.Light => Light,
            Themes.Dark => Dark,
            _ => OSThemeHelper.AppsUseDarkTheme() ? Dark : Light,
        };
    }

    /// <summary>Follows the desktop switching between light and dark while open.</summary>
    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle))
        {
            return;
        }

        // The host may have had no opinion, in which case the desktop decides.
        SetTheme(Themes.None);
    }

    /// <summary>
    /// A tree item whose whole row toggles expansion.
    ///
    /// WPF only expands when the little chevron is hit; clicking the row selects
    /// the item instead. Handling the click here makes the label itself the target,
    /// while a click that really did land on the chevron is left to WPF so the two
    /// cannot toggle twice.
    /// </summary>
    private sealed class Node : TreeViewItem
    {
        /// <summary>Fill for a row the pointer is over but which is not selected.</summary>
        public Brush? HoverBrush { get; set; }

        /// <summary>Bar drawn down the leading edge of a hovered row.</summary>
        public Brush? HoverBarBrush { get; set; }

        /// <summary>
        /// Applies or clears the hover mark on this row only. Nothing here listens
        /// for mouse events: <c>MouseEnter</c> bubbles, so a pointer over a child
        /// row would light up every ancestor too. The tree decides which single
        /// row is under the pointer and calls this.
        /// </summary>
        public void SetHover(bool on)
        {
            var show = on && !IsSelected;
            Background = show ? HoverBrush : null;
            BorderBrush = show ? HoverBarBrush : null;
            BorderThickness = show ? new Thickness(2, 0, 0, 0) : new Thickness(0);
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            var onExpander = IsOnExpander(e.OriginalSource as DependencyObject);
            base.OnMouseLeftButtonDown(e);

            if (HasItems && !onExpander)
            {
                IsExpanded = !IsExpanded;
            }
        }

        /// <summary>Whether the event came from this item's expander chevron.</summary>
        private bool IsOnExpander(DependencyObject? source)
        {
            for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
            {
                if (current is ToggleButton toggle)
                {
                    return ReferenceEquals(toggle.TemplatedParent, this);
                }
            }

            return false;
        }
    }

    private UIElement BuildTreeHeader()
    {
        var icon = new TextBlock
        {
            Text = "\uE721",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };

        var grid = new Grid { Margin = new Thickness(12, 9, 8, 9) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(icon, 0);
        Grid.SetColumn(_searchBox, 1);
        Grid.SetColumn(_treeMeta, 2);
        grid.Children.Add(icon);
        grid.Children.Add(_searchBox);
        grid.Children.Add(_treeMeta);
        return grid;
    }

    /// <summary>
    /// Pins the tree's row colours to the panel palette.
    ///
    /// WPF paints a selected-but-unfocused row with the system's inactive
    /// highlight, which on a dark desktop is a light grey that swallows the level
    /// hues and makes the row unreadable. Overriding both the focused and the
    /// inactive keys on the tree itself keeps the selected row legible when the
    /// focus moves to the detail pane, without touching the host's colours.
    /// </summary>
    private void ApplySelectionColors()
    {
        var resources = _tree.Resources;
        var background = new SolidColorBrush(_palette.Selection);
        background.Freeze();

        resources[SystemColors.HighlightBrushKey] = background;
        resources[SystemColors.InactiveSelectionHighlightBrushKey] = background;
        resources[SystemColors.HighlightTextBrushKey] = new SolidColorBrush(_palette.Text);
        resources[SystemColors.InactiveSelectionHighlightTextBrushKey] = new SolidColorBrush(_palette.Text);
    }

    /// <summary>
    /// Marks the single row the pointer is over. Walking up from the element under
    /// the pointer gives the innermost row, so an ancestor row is never lit as well.
    /// </summary>
    private void OnTreeMouseMove(object sender, MouseEventArgs e)
    {
        SetHovered(FindNode(e.OriginalSource as DependencyObject));
    }

    private void SetHovered(Node? node)
    {
        if (ReferenceEquals(_hovered, node))
        {
            return;
        }

        _hovered?.SetHover(false);
        _hovered = node;
        _hovered?.SetHover(true);
    }

    /// <summary>The innermost row containing <paramref name="source"/>, if any.</summary>
    private static Node? FindNode(DependencyObject? source)
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is Node node)
            {
                return node;
            }
        }

        return null;
    }

    private void ApplyPalette()
    {
        ApplySelectionColors();

        Background = new SolidColorBrush(_palette.Window);

        _treePanel.Background = new SolidColorBrush(_palette.Panel);
        _treePanel.BorderBrush = new SolidColorBrush(_palette.Border);
        _treeHeader.Background = new SolidColorBrush(_palette.Panel);
        _treeHeader.BorderBrush = new SolidColorBrush(_palette.Border);

        _detailPanel.Background = new SolidColorBrush(_palette.Panel);
        _detailPanel.BorderBrush = new SolidColorBrush(_palette.Border);
        _detailHeader.Background = new SolidColorBrush(_palette.Panel);
        _detailHeader.BorderBrush = new SolidColorBrush(_palette.Border);

        _search.Foreground = new SolidColorBrush(_palette.Text);
        _search.CaretBrush = new SolidColorBrush(_palette.Accent);
        _searchBox.BorderBrush = new SolidColorBrush(_palette.Border);
        _searchBox.Background = new SolidColorBrush(_palette.AccentSoft);

        _treeMeta.Foreground = new SolidColorBrush(_palette.Disabled);
        _detailTitle.Foreground = new SolidColorBrush(_palette.Text);
        _detailText.Foreground = new SolidColorBrush(_palette.Value);

        if (_root is not null)
        {
            StyleNode(_root, 0);
            Refresh(force: true);

            // The brushes were just replaced, so repaint the row under the pointer.
            _hovered?.SetHover(true);
        }
    }

    // ---------------------------------------------------------------- populate

    private void Load(string fileName, string json)
    {
        JsonValue root;
        try
        {
            // The reader output is re-parsed so the tree and the detail pane work
            // on one plain model; numbers and booleans become their text.
            root = JsonValue.Parse(json).AsDisplayValue();
        }
        catch (FormatException)
        {
            _root = new TreeViewItem { Header = fileName, IsExpanded = true };
            _tree.Items.Add(_root);
            _detailTitle.Text = fileName;
            ShowText(json);
            return;
        }

        _root = new TreeViewItem { IsExpanded = true, Header = fileName };
        _entryCount = BuildNodes(_root, root);
        _tree.Items.Add(_root);
        StyleNode(_root, 0);

        _detailTitle.Text = "Select an entry";
        ShowText("Pick an entry on the left to see its settings.");

        Refresh(force: true);
    }

    /// <summary>
    /// Adds the children of <paramref name="parent"/> and returns how many leaves
    /// the subtree holds.
    /// </summary>
    private static int BuildNodes(TreeViewItem parent, JsonValue data)
    {
        var leaves = 0;
        foreach (var property in data.Enumerate())
        {
            switch (property.Value.IsObject)
            {
                case true when TwoLevelSections.Contains(property.Key):
                    {
                        var node = Section(property.Key);
                        foreach (var group in property.Value.Enumerate())
                        {
                            var groupNode = Section(group.Key);
                            AddEntries(groupNode, group.Value);
                            node.Items.Add(groupNode);
                        }
                        parent.Items.Add(node);
                        leaves += CountLeaves(property.Value);
                        break;
                    }
                case true when OneLevelSections.Contains(property.Key):
                    {
                        var node = Section(property.Key);
                        AddEntries(node, property.Value);
                        parent.Items.Add(node);
                        leaves += CountLeaves(property.Value);
                        break;
                    }
                default:
                    parent.Items.Add(Leaf(property.Key, property.Value));
                    leaves++;
                    break;
            }
        }

        return leaves;
    }

    private static void AddEntries(TreeViewItem parent, JsonValue entries)
    {
        foreach (var entry in entries.Enumerate())
        {
            parent.Items.Add(Leaf(entry.Key, entry.Value));
        }
    }

    /// <summary>A section or type row: expandable, with no payload of its own.</summary>
    private static TreeViewItem Section(string text)
    {
        return new Node
        {
            Header = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center },
        };
    }

    private static TreeViewItem Leaf(string text, JsonValue data)
    {
        return new Node
        {
            Header = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center },
            Tag = data,
        };
    }

    private static int CountLeaves(JsonValue data)
    {
        var count = 0;
        foreach (var property in data.Enumerate())
        {
            count += property.Value.IsObject ? CountLeaves(property.Value) : 1;
        }
        return count;
    }

    /// <summary>Applies the monospace font, spacing and level hue to a subtree.</summary>
    private void StyleNode(TreeViewItem node, int depth)
    {
        node.Foreground = new SolidColorBrush(_palette.Levels[depth % _palette.Levels.Length]);
        node.FontFamily = new FontFamily(MonoFont);
        node.FontSize = 12.5;
        node.Padding = new Thickness(2, 3, 2, 3);

        if (node is Node custom)
        {
            custom.HoverBrush = new SolidColorBrush(_palette.Hover);
            custom.HoverBarBrush = new SolidColorBrush(_palette.Accent);
        }

        foreach (var child in node.Items.OfType<TreeViewItem>())
        {
            StyleNode(child, depth + 1);
        }
    }

    // ------------------------------------------------------------------ search

    /// <summary>Hides every node that neither matches nor contains a match.</summary>
    private void ApplyFilter(string filter)
    {
        _filter = filter;
        Refresh(force: false);
    }

    private void Refresh(bool force)
    {
        if (_root is null)
        {
            return;
        }

        FilterNode(_root, _filter);
        _treeMeta.Text = _filter.Length == 0 ? string.Empty : $"{CountVisible(_root)} / {_entryCount}";

        if (force || _filter.Length == 0)
        {
            _root.IsExpanded = true;
        }
    }

    private bool FilterNode(TreeViewItem node, string filter)
    {
        var selfMatch = filter.Length > 0
                        && HeaderText(node).Contains(filter, StringComparison.OrdinalIgnoreCase);

        var childVisible = false;
        foreach (var child in node.Items.OfType<TreeViewItem>())
        {
            childVisible |= FilterNode(child, filter);
        }

        var visible = filter.Length == 0 || selfMatch || childVisible;
        node.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

        if (filter.Length > 0 && childVisible && !selfMatch)
        {
            node.IsExpanded = true;
        }

        return visible;
    }

    private static int CountVisible(TreeViewItem node)
    {
        var count = 0;
        foreach (var child in node.Items.OfType<TreeViewItem>())
        {
            if (child.Visibility != Visibility.Visible)
            {
                continue;
            }

            count += child.Items.Count == 0 ? 1 : CountVisible(child);
        }
        return count;
    }

    // -------------------------------------------------------------- selection

    private void OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        // A row that just became selected drops its hover mark, since selection has
        // its own highlight; the tree re-marks it on the next mouse move.
        SetHovered(null);

        if (e.NewValue is not TreeViewItem node || node.Items.Count > 0)
        {
            // Sections toggle through Node.OnMouseLeftButtonDown instead.
            return;
        }

        var data = node.Tag as JsonValue;
        _detailTitle.Text = NodePath(node);
        ShowJson(data);
    }

    /// <summary>
    /// Labels from a section down to <paramref name="node"/>. The tree root is the
    /// file name, which the window title already shows, so it is left out.
    /// </summary>
    private static string NodePath(TreeViewItem node)
    {
        var labels = new List<string>();
        ItemsControl? current = node;
        while (current is not null)
        {
            // ItemsControlFromItemContainer returns the TreeView for the root, so
            // only items nested inside another item contribute a label.
            var parent = current is TreeViewItem item
                ? ItemsControl.ItemsControlFromItemContainer(item)
                : null;

            if (current is TreeViewItem labelled && parent is TreeViewItem)
            {
                labels.Add(HeaderText(labelled));
            }

            current = parent;
        }

        labels.Reverse();
        return string.Join(" \u203A ", labels);
    }

    private static string HeaderText(TreeViewItem item)
    {
        return item.Header switch
        {
            string text => text,
            TextBlock block => block.Text,
            StackPanel panel when panel.Children.Count > 0 && panel.Children[0] is TextBlock first => first.Text,
            _ => item.Header?.ToString() ?? string.Empty,
        };
    }

    // ---------------------------------------------------------------- rendering
    private void ShowJson(JsonValue? data)
    {
        if (data is null)
        {
            ShowText("No settings recorded for this entry.");
            return;
        }

        if (!data.IsObject && !data.IsArray)
        {
            ShowText(data.ToPlainText());
            return;
        }

        var json = data.ToIndentedJson();
        var truncated = json.Length > MaxDetailCharacters;
        if (truncated)
        {
            json = json.Substring(0, MaxDetailCharacters);
        }

        var paragraph = new Paragraph { Margin = new Thickness(0) };
        foreach (var (color, text) in Colorize(json))
        {
            paragraph.Inlines.Add(NewRun(text, color));
        }

        if (truncated)
        {
            paragraph.Inlines.Add(NewRun("\n\n... output truncated ...", _palette.Disabled));
        }

        SetDocument(paragraph);
    }

    private void ShowText(string text)
    {
        SetDocument(new Paragraph(NewRun(text, _palette.Muted)) { Margin = new Thickness(0) });
    }

    /// <summary>Replaces the detail document's content, keeping one paragraph.</summary>
    private void SetDocument(Paragraph paragraph)
    {
        var document = _detailText.Document;
        document.Blocks.Clear();
        document.Blocks.Add(paragraph);
        // A read-only box would otherwise still show the previous caret position.
        _detailText.CaretPosition = document.ContentStart;
    }

    /// <summary>
    /// A run whose whitespace survives layout. WPF collapses leading spaces in
    /// inline text unless <c>xml:space="preserve"</c> is set, which would flatten
    /// the JSON indentation.
    /// </summary>
    private static Run NewRun(string text, Color color)
    {
        var run = new Run(text) { Foreground = new SolidColorBrush(color) };
        run.SetValue(XmlAttributeProperties.XmlSpaceProperty, "preserve");
        return run;
    }

    /// <summary>
    /// Splits indented JSON into coloured runs: keys cycle through six hues by
    /// nesting depth, string values are near-white, structural brackets are dimmed
    /// and everything else is grey.
    ///
    /// Each line's newline is appended to that line's last run rather than living
    /// in a run of its own: a run holding only <c>\n</c> makes WPF open a second
    /// line box, which would double the leading. Adjacent runs of one colour are
    /// merged, so a large document stays cheap to render.
    /// </summary>
    private List<(Color Color, string Text)> Colorize(string json)
    {
        const string structure = "{}[],";

        var runs = new List<(Color Color, string Text)>();
        var pending = new StringBuilder();
        Color? pendingColor = null;

        void Flush()
        {
            if (pending.Length == 0)
            {
                return;
            }

            runs.Add((pendingColor ?? _palette.Value, pending.ToString()));
            pending.Clear();
        }

        void Emit(string chunk, Color color)
        {
            if (chunk.Length == 0)
            {
                return;
            }

            if (pendingColor == color)
            {
                pending.Append(chunk);
                return;
            }

            Flush();
            pendingColor = color;
            pending.Append(chunk);
        }

        var lines = json.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            // Where this line's text starts, so a blank line can be detected.
            var lineStart = pending.Length;

            var line = lines[index];
            var content = line.TrimStart(' ');
            var level = (line.Length - content.Length) / 4;
            var levelColor = _palette.Levels[level % _palette.Levels.Length];

            Emit(line.Substring(0, line.Length - content.Length), _palette.Value);

            var match = KeyValue.Match(content);
            if (match.Success)
            {
                Emit(match.Groups["key"].Value, levelColor);
                Emit(match.Groups["colon"].Value, _palette.Disabled);
                Emit(match.Groups["rest"].Value, ValueColor(match.Groups["rest"].Value, structure));
            }
            else
            {
                Emit(content, ValueColor(content, structure));
            }

            if (index >= lines.Length - 1)
            {
                continue;
            }

            if (pending.Length == lineStart)
            {
                // A blank line: the newline has to be its own run.
                Emit("\n", _palette.Value);
                Flush();
            }
            else
            {
                pending.Append('\n');
            }
        }

        Flush();
        return runs;
    }

    private Color ValueColor(string value, string structure)
    {
        var bare = value.TrimEnd(',').Trim();
        if (bare.StartsWith("\"", StringComparison.Ordinal))
        {
            return _palette.String;
        }

        if (bare.Length > 0 && IsAllStructure(bare, structure))
        {
            return _palette.Disabled;
        }

        return _palette.Value;
    }

    private static bool IsAllStructure(string value, string structure)
    {
        foreach (var c in value)
        {
            if (structure.IndexOf(c) < 0)
            {
                return false;
            }
        }
        return true;
    }
}
