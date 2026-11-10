using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using OverTranslate.Services.Library;
using Brush = System.Windows.Media.Brush;
using DataObject = System.Windows.DataObject;
using DragEventArgs = System.Windows.DragEventArgs;
using DragDropEffects = System.Windows.DragDropEffects;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using Pen = System.Windows.Media.Pen;

namespace OverTranslate.Views.Library;

public partial class LibraryWindow
{
    private Point _modelDragStart;
    private LibraryConnection? _modelDragCandidate;
    private LibraryConnection? _draggedConnection;
    private ModelInsertionAdorner? _modelInsertion;
    private long _modelLastScroll;

    private void ModelList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _modelDragStart = e.GetPosition(ModelList);
        _modelDragCandidate = (ItemsControl.ContainerFromElement(ModelList, e.OriginalSource as DependencyObject) as ListBoxItem)?.DataContext as LibraryConnection;
    }

    private void ModelList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => _modelDragCandidate = null;

    private void ModelList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) { _modelDragCandidate = null; return; }
        if (_modelDragCandidate is null || _draggedConnection is not null) return;
        var point = e.GetPosition(ModelList);
        if (Math.Abs(point.X - _modelDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(point.Y - _modelDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _draggedConnection = _modelDragCandidate;
        _modelDragCandidate = null;
        _modelLastScroll = 0;
        e.Handled = true;
        try
        {
            System.Windows.DragDrop.DoDragDrop(ModelList, new DataObject(typeof(LibraryConnection), _draggedConnection), DragDropEffects.Move);
        }
        finally { _draggedConnection = null; ClearModelInsertion(); }
    }

    private bool IsModelDrag(DragEventArgs e) => _draggedConnection is not null &&
        (e.AllowedEffects & DragDropEffects.Move) != 0 &&
        ReferenceEquals(e.Data.GetData(typeof(LibraryConnection)), _draggedConnection);

    private (ListBoxItem Row, LibraryConnection Connection, bool After)? ModelDropTarget(Point point)
    {
        if (point.X < 0 || point.Y < 0 || point.X >= ModelList.ActualWidth || point.Y >= ModelList.ActualHeight) return null;
        var hit = ModelList.InputHitTest(point) as DependencyObject;
        var row = hit is null ? null : ItemsControl.ContainerFromElement(ModelList, hit) as ListBoxItem;
        if (row?.DataContext is LibraryConnection connection)
            return (row, connection, ModelList.TranslatePoint(point, row).Y >= row.ActualHeight / 2);
        // 列表末尾的空白也可接收；滚动条不作为排序目标。
        if (ModelList.Items.Count > 0 && ModelList.ItemContainerGenerator.ContainerFromIndex(ModelList.Items.Count - 1) is ListBoxItem last &&
            last.DataContext is LibraryConnection tail && point.X < last.ActualWidth &&
            point.Y >= last.TranslatePoint(new Point(0, last.ActualHeight), ModelList).Y)
            return (last, tail, true);
        return null;
    }

    private void ModelList_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        ClearModelInsertion();
        if (!IsModelDrag(e)) return;
        var point = e.GetPosition(ModelList);
        if (Environment.TickCount64 - _modelLastScroll >= 100 && point.X >= 0 && point.X < ModelList.ActualWidth &&
            point.Y >= 0 && point.Y < ModelList.ActualHeight && FindModelScrollViewer(ModelList) is { } scroll)
        {
            if (point.Y < 24) { scroll.LineUp(); _modelLastScroll = Environment.TickCount64; }
            else if (point.Y > ModelList.ActualHeight - 24) { scroll.LineDown(); _modelLastScroll = Environment.TickCount64; }
        }
        if (ModelDropTarget(point) is not { } target || target.Connection.Id == _draggedConnection!.Id) return;
        e.Effects = DragDropEffects.Move;
        if (AdornerLayer.GetAdornerLayer(target.Row) is { } layer)
        {
            _modelInsertion = new ModelInsertionAdorner(target.Row, target.After, (Brush)FindResource("AppAccent"));
            layer.Add(_modelInsertion);
        }
    }

    private void ModelList_DragLeave(object sender, DragEventArgs e) => ClearModelInsertion();

    private async void ModelList_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        ClearModelInsertion();
        if (!IsModelDrag(e) || ModelDropTarget(e.GetPosition(ModelList)) is not { } target ||
            target.Connection.Id == _draggedConnection!.Id) return;
        var id = _draggedConnection.Id;
        if (_changingData) return;
        e.Effects = DragDropEffects.Move;
        if (!await ChangeDataAsync(() => _store.MoveConnection(id, target.Connection.Id, target.After))) { e.Effects = DragDropEffects.None; return; }
        ModelList.SelectedItem = ModelList.Items.Cast<LibraryConnection>().FirstOrDefault(c => c.Id == id);
        if (ModelList.SelectedItem is { } selected) ModelList.ScrollIntoView(selected);
        Notice("模型资料顺序已保存。");
        e.Effects = DragDropEffects.Move;
    }

    private void ClearModelInsertion()
    {
        if (_modelInsertion is null) return;
        AdornerLayer.GetAdornerLayer(_modelInsertion.AdornedElement)?.Remove(_modelInsertion);
        _modelInsertion = null;
    }

    private static ScrollViewer? FindModelScrollViewer(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer scroll) return scroll;
            if (FindModelScrollViewer(child) is { } nested) return nested;
        }
        return null;
    }

    private sealed class ModelInsertionAdorner : Adorner
    {
        private readonly bool _after;
        private readonly Brush _brush;

        public ModelInsertionAdorner(UIElement element, bool after, Brush brush) : base(element)
        {
            _after = after;
            _brush = brush;
            IsHitTestVisible = false;
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            var y = _after ? AdornedElement.RenderSize.Height - 1 : 1;
            drawingContext.DrawLine(new Pen(_brush, 2), new Point(0, y), new Point(AdornedElement.RenderSize.Width, y));
        }
    }
}
