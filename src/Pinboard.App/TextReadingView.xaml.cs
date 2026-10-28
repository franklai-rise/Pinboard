using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Pinboard.App.Services;

namespace Pinboard.App;

public sealed class TextReadingNavigateEventArgs(string elementId, string? query) : EventArgs
{
    public string ElementId { get; } = elementId;
    public string? Query { get; } = query;
}

public sealed record TextReadingViewState(
    string? SelectedElementId,
    string Filter,
    bool NewestFirst,
    bool CollapseConsecutiveDuplicates,
    bool FavoritesOnly,
    double ListScrollOffset,
    double TextScrollOffset);

/// <summary>Native, read-only text browsing without loading WebView or rewriting the board.</summary>
public partial class TextReadingView : System.Windows.Controls.UserControl
{
    private IReadOnlyList<TextReadingClip> _clips = [];
    private string _language = "en";
    private bool _updating;
    private readonly DispatcherTimer _filterTimer;
    private string? _displayedElementId;
    private string? _displayedText;
    private string? _displayedQuery;
    private TextReadingFavoritesStore? _favoritesStore;
    private string? _documentPath;
    private IReadOnlySet<string> _favoriteElementIds = new HashSet<string>(StringComparer.Ordinal);

    public string? FavoritesError { get; private set; }

    public event EventHandler<TextReadingNavigateEventArgs>? NavigateToElement;

    public TextReadingView()
    {
        InitializeComponent();
        _filterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        _filterTimer.Tick += (_, _) => { _filterTimer.Stop(); RefreshItems(); };
        FullTextBox.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy,
            (_, e) => { if (!FullTextBox.Selection.IsEmpty) TryCopy(FullTextBox.Selection.Text); e.Handled = true; },
            (_, e) => { e.CanExecute = !FullTextBox.Selection.IsEmpty; e.Handled = true; }));
        ApplyLanguage();
    }

    public void LoadScene(string sceneJson, string language)
    {
        _clips = TextReadingScene.Parse(sceneJson);
        _language = language;
        ApplyLanguage();
        RefreshItems();
    }

    public void ConfigureFavorites(string statePath, string documentPath)
    {
        FavoritesError = null;
        _documentPath = Path.GetFullPath(documentPath);
        _favoritesStore = new TextReadingFavoritesStore(statePath);
        try { _favoriteElementIds = _favoritesStore.GetFavorites(_documentPath); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            // Preserve an unreadable profile rather than overwriting it with an empty file.
            FavoritesError = error.Message;
            _favoritesStore = null;
            _favoriteElementIds = new HashSet<string>(StringComparer.Ordinal);
        }
        RefreshItems();
    }

    public void Clear()
    {
        _filterTimer.Stop();
        _clips = [];
        _updating = true;
        FilterBox.Clear();
        FavoritesOnlyBox.IsChecked = false;
        _updating = false;
        RefreshItems();
    }

    public TextReadingViewState CaptureViewState() => new(
        (ClipsList.SelectedItem as ReadingRow)?.Group.Clip.ElementId,
        FilterBox.Text,
        OrderBox.SelectedIndex != 1,
        CollapseDuplicatesBox.IsChecked == true,
        FavoritesOnlyBox.IsChecked == true,
        FindChild<System.Windows.Controls.ScrollViewer>(ClipsList)?.VerticalOffset ?? 0,
        FullTextBox.VerticalOffset);

    public void RestoreViewState(TextReadingViewState state)
    {
        _filterTimer.Stop();
        _updating = true;
        FilterBox.Text = state.Filter;
        OrderBox.SelectedIndex = state.NewestFirst ? 0 : 1;
        CollapseDuplicatesBox.IsChecked = state.CollapseConsecutiveDuplicates;
        FavoritesOnlyBox.IsChecked = state.FavoritesOnly;
        ClipsList.SelectedItem = null;
        _updating = false;
        RefreshItems(state.SelectedElementId);
        var document = FullTextBox.Document;
        var itemsSource = ClipsList.ItemsSource;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            // Do not let a delayed restore change a different board or a newer filtered result.
            if (!ReferenceEquals(ClipsList.ItemsSource, itemsSource) || FullTextBox.Document != document) return;
            FindChild<System.Windows.Controls.ScrollViewer>(ClipsList)?.ScrollToVerticalOffset(SafeOffset(state.ListScrollOffset));
            FullTextBox.ScrollToVerticalOffset(SafeOffset(state.TextScrollOffset));
        }));
    }

    public bool FocusElement(string elementId, string? query = null)
    {
        _filterTimer.Stop();
        _updating = true;
        if (query is not null) FilterBox.Text = query;
        if (FavoritesOnlyBox.IsChecked == true && !_favoriteElementIds.Contains(elementId))
            FavoritesOnlyBox.IsChecked = false;
        _updating = false;
        RefreshItems(elementId);
        if (ClipsList.SelectedItem is not ReadingRow selected || !selected.Group.ElementIds.Contains(elementId)) return false;
        ClipsList.ScrollIntoView(selected);
        return true;
    }

    public void ScrollToLatest()
    {
        if (_clips.Count == 0) return;
        var latest = _clips.OrderBy(clip => clip.CapturedAt?.UtcTicks ?? long.MinValue).ThenBy(clip => clip.SceneIndex).Last();
        if (!FocusElement(latest.ElementId))
        {
            _updating = true;
            FilterBox.Clear();
            _updating = false;
            FocusElement(latest.ElementId);
        }
    }

    public void FocusSearch() => FilterBox.Focus();
    public void FocusLatest() => ScrollToLatest();

    public void FocusBottom()
    {
        if (ClipsList.Items.Count > 0 && ClipsList.Items[^1] is ReadingRow bottom)
        {
            ClipsList.SelectedItem = bottom;
            ClipsList.ScrollIntoView(bottom);
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(FullTextBox.ScrollToEnd));
    }

    private bool Chinese => _language.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
    private string T(string english, string chinese) => Chinese ? chinese : english;

    private void ApplyLanguage()
    {
        _updating = true;
        var selectedIndex = Math.Max(0, OrderBox.SelectedIndex);
        OrderBox.Items.Clear();
        OrderBox.Items.Add(T("Newest first", "最新在前"));
        OrderBox.Items.Add(T("Oldest first", "最早在前"));
        OrderBox.SelectedIndex = selectedIndex;
        FilterBox.ToolTip = T("Search this text board", "搜索当前文字画板");
        System.Windows.Automation.AutomationProperties.SetName(FilterBox, T("Search text clips", "搜索文字剪藏"));
        System.Windows.Automation.AutomationProperties.SetName(OrderBox, T("Sort order", "排序方式"));
        CollapseDuplicatesBox.Content = T("Group repeated clips", "折叠连续重复内容");
        CollapseDuplicatesBox.ToolTip = T("Only changes the view. Every copied record is kept.", "仅折叠显示，每条复制记录仍完整保留。");
        FavoritesOnlyBox.Content = T("Favorites", "收藏");
        FavoritesOnlyBox.ToolTip = T("Favorites are saved in your local profile, separate from this board.", "收藏单独保存于本机配置，不修改画板内容。");
        SelectHint.Text = T("Select a clip to read its complete text.", "选择一张卡片，即可阅读完整文字。");
        CopyButton.Content = T("Copy text", "复制文字");
        ShowOnCanvasButton.Content = T("Show on canvas", "在画布中查看");
        _updating = false;
    }

    private void RefreshItems(string? preferredElementId = null)
    {
        if (_updating || ClipsList is null) return;
        var selectedId = preferredElementId ?? (ClipsList.SelectedItem as ReadingRow)?.Group.Clip.ElementId;
        var scrollViewer = FindChild<System.Windows.Controls.ScrollViewer>(ClipsList);
        var scrollOffset = scrollViewer?.VerticalOffset ?? 0;
        var query = FilterBox.Text.Trim();
        var groups = TextReadingScene.Project(_clips, query, OrderBox.SelectedIndex != 1, CollapseDuplicatesBox.IsChecked == true);
        if (FavoritesOnlyBox.IsChecked == true) groups = groups.Where(IsFavorite).ToArray();
        var rows = groups.Select(group => new ReadingRow(group, Timestamp(group.Clip.CapturedAt),
                group.DuplicateCount > 1 ? T($"×{group.DuplicateCount}", $"{group.DuplicateCount} 次") : "",
                TextReadingScene.Excerpt(group.Clip.Text, query), IsFavorite(group) ? "★" : "")).ToArray();
        _updating = true;
        ClipsList.ItemsSource = rows;
        var selected = selectedId is null ? null : rows.FirstOrDefault(row => row.Group.ElementIds.Contains(selectedId));
        ClipsList.SelectedItem = selected;
        CountLabel.Text = string.IsNullOrEmpty(query) && FavoritesOnlyBox.IsChecked != true
            ? T($"{_clips.Count:N0} clips", $"{_clips.Count:N0} 条文字")
            : T($"{rows.Sum(row => row.Group.DuplicateCount):N0} of {_clips.Count:N0} clips", $"找到 {rows.Sum(row => row.Group.DuplicateCount):N0} / {_clips.Count:N0} 条文字");
        EmptyLabel.Text = _clips.Count == 0 ? T("Copied text will appear here.", "收录的文字会出现在这里。")
            : FavoritesOnlyBox.IsChecked == true && string.IsNullOrEmpty(query) ? T("Star a clip to find it here.", "为文字添加星标后，即可在这里找到。")
            : T("No matching text. Try another word.", "没有匹配的文字，请尝试其他关键词。");
        EmptyLabel.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _updating = false;
        ShowSelectedClip();
        if (preferredElementId is null && scrollViewer is not null)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (ReferenceEquals(ClipsList.ItemsSource, rows)) scrollViewer.ScrollToVerticalOffset(scrollOffset);
            }));
    }

    private string Timestamp(DateTimeOffset? capturedAt) => capturedAt is { } date
        ? date.LocalDateTime.ToString("yyyy-MM-dd  HH:mm:ss", CultureInfo.InvariantCulture)
        : T("Undated note", "无日期备注");

    private void ShowSelectedClip()
    {
        var selected = ClipsList.SelectedItem as ReadingRow;
        CopyButton.IsEnabled = selected is not null;
        ShowOnCanvasButton.IsEnabled = selected is not null;
        var favorite = selected is not null && IsFavorite(selected.Group);
        FavoriteButton.IsEnabled = selected is not null && _favoritesStore is not null;
        FavoriteButton.Content = favorite ? "★" : "☆";
        FavoriteButton.Foreground = favorite ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 159, 10))
            : new SolidColorBrush(System.Windows.Media.Color.FromRgb(142, 142, 147));
        var favoriteAction = FavoritesError is not null ? T("Favorites could not be loaded. Your profile file was preserved.", "无法加载收藏，原配置文件已保留。")
            : favorite ? T("Remove favorite", "取消收藏") : T("Add favorite", "添加收藏");
        FavoriteButton.ToolTip = favoriteAction;
        System.Windows.Automation.AutomationProperties.SetName(FavoriteButton, favoriteAction);
        SelectHint.Visibility = selected is null ? Visibility.Visible : Visibility.Collapsed;
        DetailTimestamp.Text = selected?.TimestampLabel ?? "";
        var selectedQuery = FilterBox.Text.Trim();
        if (selected is not null && _displayedElementId == selected.Group.Clip.ElementId &&
            _displayedText == selected.Group.Clip.Text && _displayedQuery == selectedQuery)
            return;
        _displayedElementId = selected?.Group.Clip.ElementId;
        _displayedText = selected?.Group.Clip.Text;
        _displayedQuery = selectedQuery;
        var document = new FlowDocument { PagePadding = new Thickness(0), LineHeight = 25, FontSize = 16 };
        if (selected is not null)
        {
            var paragraph = new Paragraph { Margin = new Thickness(0) };
            var text = selected.Group.Clip.Text;
            var query = selectedQuery;
            var offset = 0;
            var matches = 0;
            var firstMatch = -1;
            while (!string.IsNullOrEmpty(query) && matches < 500)
            {
                var match = text.IndexOf(query, offset, StringComparison.OrdinalIgnoreCase);
                if (match < 0) break;
                if (firstMatch < 0) firstMatch = match;
                if (match > offset) paragraph.Inlines.Add(new Run(text[offset..match]));
                paragraph.Inlines.Add(new Run(text.Substring(match, query.Length)) { Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 238, 170)) });
                offset = match + query.Length;
                matches++;
            }
            if (offset < text.Length) paragraph.Inlines.Add(new Run(text[offset..]));
            document.Blocks.Add(paragraph);
            FullTextBox.Document = document;
            FullTextBox.ScrollToHome();
            if (firstMatch >= 0)
            {
                // TextPointer offsets count formatting symbols; locate the first highlighted run instead.
                var highlighted = paragraph.Inlines.OfType<Run>().FirstOrDefault(run => run.Background is not null);
                if (highlighted is not null)
                    Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                    {
                        if (FullTextBox.Document == document)
                        {
                            var rectangle = highlighted.ContentStart.GetCharacterRect(LogicalDirection.Forward);
                            FullTextBox.ScrollToVerticalOffset(FullTextBox.VerticalOffset + rectangle.Top - 40);
                        }
                    }));
            }
        }
        else FullTextBox.Document = document;
    }

    private void View_Loaded(object sender, RoutedEventArgs e) => RefreshItems();
    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating || _filterTimer is null) return;
        _filterTimer.Stop();
        _filterTimer.Start();
    }
    private void OrderBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!_updating && _filterTimer is not null) RefreshItems(); }
    private void CollapseDuplicates_Changed(object sender, RoutedEventArgs e) { if (!_updating && _filterTimer is not null) RefreshItems(); }
    private void FavoritesOnly_Changed(object sender, RoutedEventArgs e) { if (!_updating && _filterTimer is not null) RefreshItems(); }
    private void ClipsList_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!_updating) ShowSelectedClip(); }

    private void ClipsList_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ClipsList.SelectedItem is not null) { FullTextBox.Focus(); e.Handled = true; }
    }

    private void FullTextBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            if (!FullTextBox.Selection.IsEmpty) TryCopy(FullTextBox.Selection.Text);
            e.Handled = true;
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (ClipsList.SelectedItem is ReadingRow row) TryCopy(row.Group.Clip.Text);
    }

    private void TryCopy(string text)
    {
        try { ClipboardCaptureService.SetTextWithoutCapture(text); }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            MessageBox.Show(T("The clipboard is busy. Please try again.", "剪贴板正在被占用，请重试。"), "Pinboard", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void ShowOnCanvas_Click(object sender, RoutedEventArgs e)
    {
        if (ClipsList.SelectedItem is ReadingRow row)
            NavigateToElement?.Invoke(this, new TextReadingNavigateEventArgs(row.Group.Clip.ElementId, FilterBox.Text.Trim()));
    }

    private bool IsFavorite(TextReadingGroup group) => group.ElementIds.Any(_favoriteElementIds.Contains);

    private void Favorite_Click(object sender, RoutedEventArgs e)
    {
        if (ClipsList.SelectedItem is not ReadingRow row || _favoritesStore is null || _documentPath is null) return;
        try
        {
            _favoritesStore.SetFavorites(_documentPath, row.Group.ElementIds, !IsFavorite(row.Group));
            _favoriteElementIds = _favoritesStore.GetFavorites(_documentPath);
            RefreshItems();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            MessageBox.Show(T("Could not save the favorite. Your board content has not changed.", "无法保存收藏，画板内容保持不变。"),
                "Pinboard", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static TChild? FindChild<TChild>(DependencyObject parent) where TChild : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is TChild typed) return typed;
            if (FindChild<TChild>(child) is { } descendant) return descendant;
        }
        return null;
    }

    private static double SafeOffset(double offset) => double.IsFinite(offset) && offset > 0 ? offset : 0;

    private sealed record ReadingRow(TextReadingGroup Group, string TimestampLabel, string RepeatsLabel, string Excerpt, string FavoriteLabel);
}
