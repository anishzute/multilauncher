using System;
using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace Pitstop.Windows;

// One behavior serves the two app lists and the profile sidebar. Payloads are
// local and confined to their source list; dragging never changes Open to Close.
public static class DragReorder
{
    private const string Format = "Pitstop.ListReorder";
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(DragReorder), new PropertyMetadata(false, Changed));
    public static readonly RoutedEvent ReorderedEvent = EventManager.RegisterRoutedEvent("Reordered", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(DragReorder));
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached("State", typeof(State), typeof(DragReorder));
    private sealed record Payload(ItemsControl Owner, object Item);
    private sealed class State
    {
        public Point Start;
        public object? Candidate;
        public InsertionLine? Line;
        public DateTime LastScroll;
    }
    public static void SetEnabled(DependencyObject owner, bool value) => owner.SetValue(EnabledProperty, value);
    public static bool GetEnabled(DependencyObject owner) => (bool)owner.GetValue(EnabledProperty);
    public static void AddReorderedHandler(DependencyObject owner, RoutedEventHandler handler) => ((UIElement)owner).AddHandler(ReorderedEvent, handler);
    public static void RemoveReorderedHandler(DependencyObject owner, RoutedEventHandler handler) => ((UIElement)owner).RemoveHandler(ReorderedEvent, handler);
    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not ItemsControl list) return;
        list.AllowDrop = (bool)e.NewValue;
        if ((bool)e.NewValue)
        {
            list.SetValue(StateProperty, new State());
            list.PreviewMouseLeftButtonDown += Down;
            list.PreviewMouseMove += Move;
            list.PreviewDragOver += Over;
            list.PreviewDragLeave += Leave;
            list.PreviewDrop += Drop;
        }
        else
        {
            Clear(list); list.PreviewMouseLeftButtonDown -= Down; list.PreviewMouseMove -= Move;
            list.PreviewDragOver -= Over; list.PreviewDragLeave -= Leave; list.PreviewDrop -= Drop;
        }
    }
    private static State GetState(ItemsControl list) => (State)list.GetValue(StateProperty);
    private static void Down(object sender, MouseButtonEventArgs e)
    {
        var list = (ItemsControl)sender; var state = GetState(list); state.Candidate = null;
        for (var p = e.OriginalSource as DependencyObject; p is not null && p != list; p = Parent(p))
            if (p is ButtonBase or TextBoxBase or ComboBox or ScrollBar) return;
        var container = ItemsControl.ContainerFromElement(list, e.OriginalSource as DependencyObject);
        if (container is null) return;
        state.Candidate = list.ItemContainerGenerator.ItemFromContainer(container);
        state.Start = e.GetPosition(list);
    }
    private static void Move(object sender, MouseEventArgs e)
    {
        var list = (ItemsControl)sender; var state = GetState(list);
        if (!list.IsEnabled || e.LeftButton != MouseButtonState.Pressed) { state.Candidate = null; return; }
        var position = e.GetPosition(list);
        if (state.Candidate is null || (Math.Abs(position.X - state.Start.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(position.Y - state.Start.Y) < SystemParameters.MinimumVerticalDragDistance)) return;
        var item = state.Candidate; state.Candidate = null;
        try { DragDrop.DoDragDrop(list, new DataObject(Format, new Payload(list, item)), DragDropEffects.Move); }
        finally { Clear(list); }
        e.Handled = true;
    }
    private static Payload? Read(ItemsControl list, DragEventArgs e) => list.IsEnabled && e.Data.GetDataPresent(Format)
        && e.Data.GetData(Format) is Payload payload && payload.Owner == list ? payload : null;
    private static IList Items(ItemsControl list) => list.ItemsSource as IList ?? list.Items;
    private static int Boundary(ItemsControl list, DragEventArgs e, out double y)
    {
        var container = ItemsControl.ContainerFromElement(list, e.OriginalSource as DependencyObject) as FrameworkElement;
        if (container is null) { y = list.ActualHeight; return Items(list).Count; }
        var below = e.GetPosition(container).Y >= container.ActualHeight / 2;
        y = container.TranslatePoint(new Point(0, below ? container.ActualHeight : 0), list).Y;
        return list.ItemContainerGenerator.IndexFromContainer(container) + (below ? 1 : 0);
    }
    private static void Over(object sender, DragEventArgs e)
    {
        var list = (ItemsControl)sender;
        e.Handled = true; e.Effects = Read(list, e) is null ? DragDropEffects.None : DragDropEffects.Move;
        if (e.Effects == DragDropEffects.None) { Clear(list); return; }
        Boundary(list, e, out var y);
        var state = GetState(list);
        var layer = AdornerLayer.GetAdornerLayer(list);
        if (state.Line is null && layer is not null) { state.Line = new InsertionLine(list); layer.Add(state.Line); }
        if (state.Line is { } line) { line.Y = y; line.InvalidateVisual(); }
        if (DateTime.UtcNow - state.LastScroll < TimeSpan.FromMilliseconds(80)) return;
        var scroll = Ancestor<ScrollViewer>(list) ?? Child<ScrollViewer>(list);
        if (scroll is null) return;
        var point = e.GetPosition(scroll);
        if (point.Y < 30) scroll.LineUp();
        else if (point.Y > scroll.ActualHeight - 30) scroll.LineDown();
        state.LastScroll = DateTime.UtcNow;
    }
    private static void Drop(object sender, DragEventArgs e)
    {
        var list = (ItemsControl)sender; var payload = Read(list, e); e.Handled = true;
        if (payload is null) { Clear(list); return; }
        var items = Items(list); var from = items.IndexOf(payload.Item); var to = Boundary(list, e, out _);
        Clear(list);
        if (from < 0) return;
        if (from < to) to--;
        to = Math.Clamp(to, 0, items.Count - 1);
        if (from == to) return;
        items.RemoveAt(from); items.Insert(to, payload.Item);
        list.Items.Refresh();
        list.RaiseEvent(new RoutedEventArgs(ReorderedEvent, list));
        e.Effects = DragDropEffects.Move;
    }
    private static void Leave(object sender, DragEventArgs e) => Clear((ItemsControl)sender);
    private static void Clear(ItemsControl list)
    {
        if (list.GetValue(StateProperty) is not State state) return;
        if (state.Line is not null) AdornerLayer.GetAdornerLayer(list)?.Remove(state.Line);
        state.Line = null;
    }
    private static DependencyObject? Parent(DependencyObject child) => child is Visual ? VisualTreeHelper.GetParent(child) : LogicalTreeHelper.GetParent(child);
    private static T? Ancestor<T>(DependencyObject child) where T : DependencyObject
    {
        for (var p = Parent(child); p is not null; p = Parent(p)) if (p is T found) return found;
        return null;
    }
    private static T? Child<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) return found;
            if (Child<T>(child) is { } nested) return nested;
        }
        return null;
    }
    private sealed class InsertionLine(UIElement element) : Adorner(element)
    {
        public double Y;
        protected override void OnRender(DrawingContext context)
        {
            IsHitTestVisible = false;
            var brush = (Brush)Application.Current.FindResource("Accent");
            context.DrawLine(new Pen(brush, 2), new Point(4, Y), new Point(Math.Max(4, AdornedElement.RenderSize.Width - 4), Y));
            context.DrawEllipse(brush, null, new Point(4, Y), 3, 3);
        }
    }
}
