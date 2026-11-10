using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using OverTranslate.Services.Library;
using OverTranslate.Views.Library;
using Xunit;
using DataObject = System.Windows.DataObject;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using Point = System.Windows.Point;

namespace OverTranslate.Tests;

public sealed partial class ScreenTranslateShellTests
{
    private static void VerifyLibraryDragOrder(string directory)
    {
        var path = System.IO.Path.Combine(directory, "order", "library.json");
        var store = new LibraryStore(path);
        store.Update(s =>
        {
            s.SelectedTab = 1;
            s.Connections = [new() { Id = "a", Platform = "same", Model = "visible" },
                new() { Id = "b", Platform = "other", Model = "hidden" },
                new() { Id = "c", Platform = "same", Model = "visible" }];
        });
        var window = new LibraryWindow(store, copy: _ => throw new InvalidOperationException("Reordering must not copy"))
        { Left = -10000, Top = -10000, ShowActivated = false };
        try
        {
            window.Show(); window.UpdateLayout();
            var list = (ListBox)window.FindName("ModelList");
            Assert.True(list.AllowDrop);
            AssertOrder("a", "b", "c");
            Assert.Equal(DragDropEffects.Move, Drop("c", "a", false).Effects);
            AssertOrder("c", "a", "b");
            Assert.Equal("c", ((LibraryConnection)list.SelectedItem).Id);
            Assert.Contains("顺序已保存", ((TextBlock)window.FindName("Status")).Text);
            Assert.Equal(DragDropEffects.Move, Drop("c", "b", true).Effects);
            AssertOrder("a", "b", "c");

            ((TextBox)window.FindName("ModelSearch")).Text = "visible";
            window.UpdateLayout();
            Assert.Equal(["a", "c"], list.Items.Cast<LibraryConnection>().Select(c => c.Id));
            Drop("c", "a", false);
            Assert.Equal(["c", "a", "b"], new LibraryStore(path).Read().Connections.Select(c => c.Id));
            ((TextBox)window.FindName("ModelSearch")).Clear();
            window.UpdateLayout();
            AssertOrder("c", "a", "b");

            Assert.Equal(DragDropEffects.None, Drop("c", "c", true).Effects);
            var otherData = new DataObject("text", "foreign");
            var foreign = Raise(otherData, PointOnRow("a", false), System.Windows.DragDrop.DropEvent);
            Assert.Equal(DragDropEffects.None, foreign.Effects);
            AssertOrder("c", "a", "b");

            // 经过目标时只显示插入线，取消/离开不会提前写盘。
            var data = Begin("c");
            var over = Raise(data, PointOnRow("a", true), System.Windows.DragDrop.DragOverEvent);
            Assert.Equal(DragDropEffects.Move, over.Effects);
            Assert.NotNull(Field(window, "_modelInsertion"));
            Raise(data, new Point(-1, -1), System.Windows.DragDrop.DragLeaveEvent);
            Assert.Null(Field(window, "_modelInsertion"));
            AssertOrder("c", "a", "b");

            // 最后一行下方的空白可作为列表末尾。
            data = Begin("c");
            var last = Row("b");
            var blank = last.TranslatePoint(new Point(30, last.ActualHeight + 12), list);
            Assert.Equal(DragDropEffects.Move, Raise(data, blank, System.Windows.DragDrop.DropEvent).Effects);
            AssertOrder("a", "b", "c");

            var fixture = System.IO.File.ReadAllText(path);
            System.IO.File.WriteAllText(path, "invalid fixture");
            Assert.Equal(DragDropEffects.None, Drop("c", "a", false).Effects);
            Assert.Equal("invalid fixture", System.IO.File.ReadAllText(path));
            System.IO.File.WriteAllText(path, fixture);

            store.Update(s => { for (var i = 0; i < 40; i++) s.Connections.Add(new() { Id = "extra-" + i, Platform = "extra" }); });
            var reload = window.ReloadViewsAsync();
            PumpUntil(() => reload.IsCompleted); reload.GetAwaiter().GetResult();
            ((TextBox)window.FindName("ModelSearch")).Text = "extra";
            window.UpdateLayout();
            var scroll = Visuals<ScrollViewer>(list).First();
            data = Begin("extra-0");
            Raise(data, new Point(40, list.ActualHeight - 4), System.Windows.DragDrop.DragOverEvent);
            window.UpdateLayout();
            Assert.True(scroll.VerticalOffset > 0, "Dragging near the bottom must scroll a long list.");
            Render(window, "library-drag-order");

            LibraryConnection Item(string id) => list.Items.Cast<LibraryConnection>().Single(c => c.Id == id);
            ListBoxItem Row(string id) => (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(Item(id));
            Point PointOnRow(string id, bool after)
            {
                var row = Row(id);
                return row.TranslatePoint(new Point(40, after ? row.ActualHeight - 5 : 5), list);
            }
            DataObject Begin(string id)
            {
                var item = Item(id);
                typeof(LibraryWindow).GetField("_draggedConnection", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, item);
                return new DataObject(typeof(LibraryConnection), item);
            }
            DragEventArgs Drop(string source, string target, bool after) => Raise(Begin(source), PointOnRow(target, after), System.Windows.DragDrop.DropEvent);
            DragEventArgs Raise(DataObject payload, Point position, RoutedEvent routedEvent)
            {
                var args = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs), BindingFlags.Instance | BindingFlags.NonPublic,
                    null, [payload, System.Windows.DragDropKeyStates.LeftMouseButton, DragDropEffects.Move, list, position], null)!;
                args.RoutedEvent = routedEvent;
                list.RaiseEvent(args);
                if (routedEvent == System.Windows.DragDrop.DropEvent)
                    PumpUntil(() => !(bool)Field(window, "_changingData")!);
                window.UpdateLayout();
                return args;
            }
            void AssertOrder(params string[] ids)
            {
                Assert.Equal(ids, list.Items.Cast<LibraryConnection>().Select(c => c.Id));
                Assert.Equal(ids, new LibraryStore(path).Read().Connections.Select(c => c.Id));
            }
        }
        finally { window.CloseForTests(); }
    }
}
