using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Pinboard.App.Services;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace Pinboard.App;

public static class SearchHighlight
{
    public static readonly DependencyProperty ExcerptProperty = DependencyProperty.RegisterAttached(
        "Excerpt", typeof(SearchExcerpt), typeof(SearchHighlight), new PropertyMetadata(null, ExcerptChanged));

    public static void SetExcerpt(DependencyObject target, SearchExcerpt value) => target.SetValue(ExcerptProperty, value);
    public static SearchExcerpt? GetExcerpt(DependencyObject target) => (SearchExcerpt?)target.GetValue(ExcerptProperty);

    private static void ExcerptChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not TextBlock block) return;
        block.Inlines.Clear();
        if (args.NewValue is not SearchExcerpt excerpt) return;
        foreach (var segment in excerpt.Runs)
        {
            var run = new Run(segment.Text);
            if (segment.IsMatch)
            {
                run.FontWeight = FontWeights.SemiBold;
                run.Foreground = (Brush?)block.TryFindResource("AccentBrush") ?? Brushes.DodgerBlue;
                run.Background = (Brush?)block.TryFindResource("AccentSoftBrush") ?? Brushes.AliceBlue;
            }
            block.Inlines.Add(run);
        }
    }
}
