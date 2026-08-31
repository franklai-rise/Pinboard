using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using Pinboard.App.Models;
using Pinboard.App.Services;

namespace Pinboard.App;

public partial class SearchWindow : Window
{
    private readonly string _libraryPath;
    private readonly SearchService _searchService;
    private readonly string? _documentPath;
    private readonly ObservableCollection<SearchDisplay> _items = [];

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
        Loaded += (_, _) => QueryBox.Focus();
    }

    public SearchHit? SelectedHit { get; private set; }

    private async void SearchButton_Click(object sender, RoutedEventArgs e)
    {
        _items.Clear();
        SearchStatus.Text = LocalizationService.T("SearchWorking");
        var results = _documentPath is null
            ? await _searchService.SearchLibraryAsync(_libraryPath, QueryBox.Text)
            : await new PinboardDocument(_documentPath).SearchAsync(QueryBox.Text);
        foreach (var hit in results)
        {
            _items.Add(new SearchDisplay(hit));
        }
        SearchStatus.Text = results.Count == 0
            ? LocalizationService.T("SearchNoResults")
            : LocalizationService.T("SearchResultsFormat", results.Count);
    }

    private void ResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ResultsList.SelectedItem is not SearchDisplay display)
        {
            return;
        }
        SelectedHit = display.Hit;
        DialogResult = true;
    }

    private sealed record SearchDisplay(SearchHit Hit)
    {
        public string Header => $"{Hit.DocumentTitle} · {(Hit.Kind == "ocr" ? "OCR" : LocalizationService.T("SearchTextNote"))}";
        public string Snippet => Hit.Text.Length <= 240 ? Hit.Text : Hit.Text[..240] + "…";
    }
}
