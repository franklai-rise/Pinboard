using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Pinboard.App.Models;
using Pinboard.App.Services;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Pinboard.App;

public partial class SearchWindow : Window
{
    private readonly string _libraryPath;
    private readonly SearchService _searchService;
    private readonly string? _documentPath;
    private readonly ObservableCollection<SearchDisplay> _items = [];
    private readonly SearchRequestController _requests = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private int _resultLimit = SearchService.DefaultResultLimit;
    private bool _ready;
    private bool _closed;

    public SearchWindow(string libraryPath, SearchService searchService, string? documentPath = null)
    {
        InitializeComponent();
        _libraryPath = libraryPath;
        _searchService = searchService;
        _documentPath = documentPath;
        Title = documentPath is null
            ? LocalizationService.T("SearchAllTitle")
            : LocalizationService.T("SearchCurrentTitleFormat", PinboardDocument.ReadDisplayTitle(documentPath));
        ResultsList.ItemsSource = _items;
        OrderSelector.Items.Add(Text("Most relevant", "相关性优先"));
        OrderSelector.Items.Add(Text("Recently updated", "最近更新"));
        OrderSelector.SelectedIndex = 0;
        OrderSelector.Visibility = documentPath is null ? Visibility.Visible : Visibility.Collapsed;
        SearchHint.Text = Text("Type to search · Enter opens the selected result", "输入即可搜索 · Enter 打开选中结果");
        MoreResultsButton.Content = Text("Show more", "显示更多");
        _ready = true;
        Loaded += (_, _) => QueryBox.Focus();
        Closed += (_, _) =>
        {
            _closed = true;
            _lifetimeCancellation.Cancel();
            _requests.Dispose();
            _lifetimeCancellation.Dispose();
        };
    }

    public SearchHit? SelectedHit { get; private set; }
    public string SelectedQuery { get; private set; } = "";

    private async void SearchButton_Click(object sender, RoutedEventArgs e) =>
        await StartSearchAsync(debounce: false);

    private async void QueryBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_ready) await StartSearchAsync(debounce: true);
    }

    private async void OrderSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready) await StartSearchAsync(debounce: false);
    }

    private async void MoreResultsButton_Click(object sender, RoutedEventArgs e)
    {
        _resultLimit = Math.Min(SearchService.MaximumResultLimit, _resultLimit + SearchService.DefaultResultLimit);
        await StartSearchAsync(debounce: false, resetLimit: false);
    }

    private async Task StartSearchAsync(bool debounce, bool resetLimit = true)
    {
        if (_closed) return;
        if (resetLimit) _resultLimit = SearchService.DefaultResultLimit;
        using var request = _requests.Begin(QueryBox.Text.Trim(), _lifetimeCancellation.Token);
        var query = request.Query;
        var token = request.Token;
        var order = OrderSelector.SelectedIndex == 1 ? SearchResultOrder.Recent : SearchResultOrder.Relevance;
        var limit = _resultLimit;
        _items.Clear();
        MoreResultsButton.Visibility = Visibility.Collapsed;
        SearchStatus.ToolTip = null;
        if (query.Length == 0)
        {
            SearchStatus.Text = "";
            return;
        }
        SearchStatus.Text = LocalizationService.T("SearchWorking");
        try
        {
            if (debounce) await Task.Delay(250, token);
            var page = await Task.Run(async () =>
            {
                if (_documentPath is null)
                    return await _searchService.SearchLibraryPageAsync(_libraryPath, query, order, limit, token);
                var hits = await new PinboardDocument(_documentPath).SearchAsync(query, token);
                return new SearchResultPage(SearchService.OrderDocumentResults(hits, query), hits.Count,
                    hits.Count >= 100, hits.Count >= 100, 0);
            }, token);
            if (!_requests.IsCurrent(request)) return;
            foreach (var hit in page.Hits) _items.Add(new SearchDisplay(hit, query));
            if (_items.Count > 0) ResultsList.SelectedIndex = 0;
            SearchStatus.Text = ResultStatus(page);
            MoreResultsButton.Visibility = _documentPath is null && page.RetrievedMatchCount > page.Hits.Count
                && limit < SearchService.MaximumResultLimit ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!_requests.IsCurrent(request)) return;
            SearchStatus.Text = Text("Search could not complete. Try again.", "搜索未能完成，请重试。");
            SearchStatus.ToolTip = error.Message;
        }
    }

    private static string ResultStatus(SearchResultPage page)
    {
        var totalLabel = page.MayHaveAdditionalMatches
            ? Text("at least ", "至少 ") + page.RetrievedMatchCount
            : page.RetrievedMatchCount.ToString();
        var status = page.Hits.Count == 0
            ? Text("No matching content", "没有找到匹配内容")
            : page.HasMoreResults
                ? string.Format(Text("Showing {0} of {1} matches", "显示 {1} 条结果中的 {0} 条"), page.Hits.Count, totalLabel)
                : string.Format(Text("{0} matches", "{0} 条结果"), page.Hits.Count);
        if (page.MayHaveAdditionalMatches)
            status += Text(" · Refine the query for more matches in large boards", " · 大画板还有更多内容，请细化关键词");
        else if (page.HasMoreResults && page.Hits.Count >= SearchService.MaximumResultLimit)
            status += Text(" · Refine the query to see other matches", " · 请细化关键词查看其他结果");
        if (page.SkippedDocumentCount > 0)
            status += string.Format(Text(" · {0} unavailable boards skipped", " · 已跳过 {0} 个无法读取的画板"), page.SkippedDocumentCount);
        return status;
    }

    private async void QueryBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await StartSearchAsync(debounce: false);
            if (!_closed && _items.Count > 0)
                (ResultsList.ItemContainerGenerator.ContainerFromIndex(ResultsList.SelectedIndex) as ListBoxItem)?.Focus();
        }
        else if (e.Key == Key.Down && _items.Count > 0)
        {
            e.Handled = true;
            if (ResultsList.SelectedIndex < 0) ResultsList.SelectedIndex = 0;
            ResultsList.ScrollIntoView(ResultsList.SelectedItem);
            (ResultsList.ItemContainerGenerator.ContainerFromIndex(ResultsList.SelectedIndex) as ListBoxItem)?.Focus();
        }
    }

    private void ResultsList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        OpenSelectedResult();
    }

    private void ResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        OpenSelectedResult();
    }

    private void OpenSelectedResult()
    {
        if (ResultsList.SelectedItem is not SearchDisplay display) return;
        SelectedHit = display.Hit;
        SelectedQuery = display.Query;
        DialogResult = true;
    }

    private static string Text(string english, string chinese) =>
        LocalizationService.CurrentLanguage == LocalizationService.Chinese ? chinese : english;

    private sealed record SearchDisplay
    {
        public SearchDisplay(SearchHit hit, string query)
        {
            Hit = hit;
            Query = query;
            Excerpt = SearchPresentation.BuildExcerpt(hit.Text, query);
            var kind = hit.Kind == "ocr" ? "OCR" : Text("Text note", "文字备注");
            try { Metadata = $"{kind} · {File.GetLastWriteTime(hit.DocumentPath):yyyy-MM-dd HH:mm}"; }
            catch (IOException) { Metadata = kind; }
            catch (UnauthorizedAccessException) { Metadata = kind; }
        }
        public SearchHit Hit { get; }
        public string Query { get; }
        public SearchExcerpt Excerpt { get; }
        public string Header => Hit.DocumentTitle;
        public string Metadata { get; }
    }
}
