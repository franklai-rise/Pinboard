using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Input;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Pinboard.App.Tests;

[TestClass]
public class SidebarGlassTests
{
    [TestMethod]
    public void GlassSidebar_HeaderHitAreaAndIndependentAddButton_KeepSelectionStable()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Pinboard;component/Styles/AppleTheme.xaml", UriKind.Relative) });
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Pinboard;component/Styles/SidebarGlass.xaml", UriKind.Relative) });
                var header = new Grid();
                header.ColumnDefinitions.Add(new ColumnDefinition());
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var label = new TextBlock { Text = "Text Clips", IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center };
                var add = new Button { Content = "+", Width = 22, Height = 22 };
                Grid.SetColumn(add, 1); header.Children.Add(label); header.Children.Add(add);
                var expander = new Expander { Header = header, Style = (Style)app.Resources["SidebarGlassGroup"], Content = new TextBlock { Text = "2026-10-27" } };
                var root = new StackPanel { Width = 240, Background = (Brush)app.Resources["SidebarGlassBackground"] };
                root.Children.Add(expander);
                var list = new ListBox { ItemContainerStyle = (Style)app.Resources["SidebarGlassRow"] };
                list.Items.Add("2026-10-27"); list.Items.Add("2026-10-26"); root.Children.Add(list);
                root.Measure(new Size(240, 600)); root.Arrange(new Rect(0, 0, 240, 600)); root.UpdateLayout();
                var toggle = Descendants(expander).OfType<ToggleButton>().Single();
                Assert.AreEqual(Cursors.Hand, toggle.Cursor, "Disclosure must advertise clickability.");
                var arrowButton = (Border)toggle.Template.FindName("ArrowButton", toggle);
                Assert.AreEqual(24d, arrowButton.ActualWidth);
                Assert.AreEqual(24d, arrowButton.ActualHeight);
                Assert.IsInstanceOfType<ScaleTransform>(arrowButton.RenderTransform);
                Assert.IsInstanceOfType<RotateTransform>(((FrameworkElement)toggle.Template.FindName("Arrow", toggle)).RenderTransform);
                Assert.IsTrue(HasAncestor(InputHit(expander, new Point(16, 17)), toggle), "Arrow capsule must still target the disclosure.");
                toggle.IsEnabled = false; root.UpdateLayout();
                Assert.AreEqual(Cursors.Arrow, toggle.Cursor, "Disabled disclosure must not promise a click.");
                toggle.IsEnabled = true;
                var hoverTrigger = toggle.Template.Triggers.OfType<Trigger>().Single(t => t.Property == UIElement.IsMouseOverProperty);
                Assert.IsTrue(hoverTrigger.Setters.OfType<Setter>().Any(s => s.TargetName == "ArrowButton" && s.Property == Border.BackgroundProperty));
                Assert.IsTrue(hoverTrigger.Setters.OfType<Setter>().Any(s => s.TargetName == "Arrow" && s.Property == System.Windows.Shapes.Shape.StrokeProperty));
                Assert.IsTrue(toggle.ActualWidth > 200, "The disclosure target must span the header, not just the arrow.");
                var hit = InputHit(expander, new Point(90, 17));
                Assert.IsTrue(HasAncestor(hit, toggle), "Clicking the label should hit the full-row toggle. Actual: " + hit?.GetType().FullName);
                var plusPoint = add.TranslatePoint(new Point(11, 11), expander);
                Assert.IsTrue(HasAncestor(InputHit(expander, plusPoint), add), "The plus must remain independent.");
                toggle.IsChecked = true; root.UpdateLayout(); Assert.IsTrue(expander.IsExpanded);
                toggle.IsChecked = false; root.UpdateLayout(); Assert.IsFalse(expander.IsExpanded);
                var item = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
                var height = item.ActualHeight;
                Assert.AreEqual(Cursors.Hand, item.Cursor);
                item.IsEnabled = false; Assert.AreEqual(Cursors.Arrow, item.Cursor); item.IsEnabled = true;
                item.IsSelected = true; root.UpdateLayout();
                Assert.AreEqual(height, item.ActualHeight, .01, "Selection must not move neighbouring rows.");
                Assert.IsTrue(height >= 42);
                var glass = (Border)item.Template.FindName("Glass", item);
                Assert.IsInstanceOfType<LinearGradientBrush>(glass.Background);
                Assert.IsTrue(Descendants(root).OfType<UIElement>().All(e => e.Effect is null), "Sidebar must not allocate blur effects.");
                // Offscreen render: exercises template, animation and glyph resource creation without opening user documents.
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(360, 900, 144, 144, PixelFormats.Pbgra32);
                bitmap.Render(root);
                app.Shutdown();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(30)), "Sidebar test timed out.");
        if (failure is not null) throw new AssertFailedException("Sidebar template failed at runtime: " + failure, failure);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static bool HasAncestor(DependencyObject? current, DependencyObject target)
    {
        while (current is not null) { if (ReferenceEquals(current, target)) return true; current = VisualTreeHelper.GetParent(current); }
        return false;
    }
    private static DependencyObject? InputHit(Visual root, Point point)
    {
        DependencyObject? hit = null;
        VisualTreeHelper.HitTest(root,
            d => d is UIElement e && (!e.IsHitTestVisible || e.Visibility != Visibility.Visible)
                ? HitTestFilterBehavior.ContinueSkipSelfAndChildren : HitTestFilterBehavior.Continue,
            r => { hit = r.VisualHit; return HitTestResultBehavior.Stop; }, new PointHitTestParameters(point));
        return hit;
    }
}
