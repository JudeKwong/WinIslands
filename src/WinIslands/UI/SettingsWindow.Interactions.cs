using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Point = System.Windows.Point;
using DragEventArgs = System.Windows.DragEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using DragDropEffects = System.Windows.DragDropEffects;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using MessageBox = System.Windows.MessageBox;
using WinIslands.Diagnostics;
using WinIslands.Services;
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace WinIslands.UI;

public partial class SettingsWindow : Window
{
    // 改为纯 WPF 鼠标捕获实现，拖拽排序不再依赖 OLE。
    private Point _dragStart;
    private OrderItem? _dragItem;
    private FrameworkElement? _dragChip;
    private bool _dragActive;

    private void Chip_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragItem = (sender as FrameworkElement)?.DataContext as OrderItem;
        _dragChip = sender as FrameworkElement;
        _dragActive = _dragItem is not null;
    }

    private void Chip_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragActive || _dragItem is null || _dragChip is null) return;
        if (e.LeftButton != MouseButtonState.Pressed) { EndChipDrag(); return; }
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _dragStart.X) < 4 && Math.Abs(pos.Y - _dragStart.Y) < 4) return;
        if (_dragChip.IsMouseCaptured) return;
        if (!_dragChip.CaptureMouse()) { EndChipDrag(); return; }
        _dragChip.Opacity = 0.6;
        Mouse.OverrideCursor = System.Windows.Input.Cursors.SizeWE;
    }

    private void Chip_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragActive || _dragItem is null) return;
        var chip = sender as FrameworkElement ?? _dragChip;
        var strip = FindAncestor<System.Windows.Controls.ItemsControl>(chip);
        if (strip is not null)
        {
            var x = e.GetPosition(strip).X;
            var target = _vm.OrderItems.Count - 1;
            for (var i = 0; i < _vm.OrderItems.Count; i++)
            {
                if (strip.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement fe)
                {
                    var mid = fe.TransformToAncestor(strip).Transform(new Point(fe.ActualWidth / 2, 0)).X;
                    if (x < mid) { target = i; break; }
                    target = i;
                }
            }
            _vm.MoveOrderItemTo(_dragItem, target);
        }
        EndChipDrag();
        e.Handled = true;
    }

    private void Chip_LostMouseCapture(object sender, MouseEventArgs e) => EndChipDrag();

    private void EndChipDrag()
    {
        _dragActive = false;
        var chip = _dragChip;
        _dragChip = null;
        _dragItem = null;
        if (chip is not null)
        {
            try { if (chip.IsMouseCaptured) chip.ReleaseMouseCapture(); } catch { /* ignore */ }
            chip.Opacity = 1;
        }
        Mouse.OverrideCursor = null;
    }

    // ── 页面管理：竖向拖拽排序（经典布局 page_order 交互）──
    private Point _pageDragStart;
    private PageManageRow? _pageDragRow;
    private FrameworkElement? _pageDragHandle;
    private bool _pageDragActive;

    private void PageRow_DragHandleDown(object sender, MouseButtonEventArgs e)
    {
        _pageDragStart = e.GetPosition(null);
        _pageDragRow = (sender as FrameworkElement)?.DataContext as PageManageRow;
        _pageDragHandle = sender as FrameworkElement;
        _pageDragActive = _pageDragRow is not null;
    }

    private void PageRow_DragHandleMove(object sender, MouseEventArgs e)
    {
        if (!_pageDragActive || _pageDragRow is null || _pageDragHandle is null) return;
        if (e.LeftButton != MouseButtonState.Pressed) { EndPageRowDrag(); return; }
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _pageDragStart.X) < 4 && Math.Abs(pos.Y - _pageDragStart.Y) < 4) return;
        if (_pageDragHandle.IsMouseCaptured) return;
        if (!_pageDragHandle.CaptureMouse()) { EndPageRowDrag(); return; }
        _pageDragHandle.Opacity = 0.55;
        Mouse.OverrideCursor = System.Windows.Input.Cursors.SizeNS;
    }

    private void PageRow_DragHandleUp(object sender, MouseButtonEventArgs e)
    {
        if (!_pageDragActive || _pageDragRow is null) return;
        var list = PageManageList;
        var y = e.GetPosition(list).Y;
        var target = _vm.PageManageRows.Count - 1;
        for (var i = 0; i < _vm.PageManageRows.Count; i++)
        {
            if (list.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement fe)
            {
                var mid = fe.TransformToAncestor(list).Transform(new Point(0, fe.ActualHeight / 2)).Y;
                if (y < mid) { target = i; break; }
                target = i;
            }
        }
        _vm.MovePageRowTo(_pageDragRow, target);
        EndPageRowDrag();
        e.Handled = true;
    }

    private void PageRow_LostMouseCapture(object sender, MouseEventArgs e) => EndPageRowDrag();

    private void EndPageRowDrag()
    {
        _pageDragActive = false;
        var handle = _pageDragHandle;
        _pageDragHandle = null;
        _pageDragRow = null;
        if (handle is not null)
        {
            try { if (handle.IsMouseCaptured) handle.ReleaseMouseCapture(); } catch { }
            handle.Opacity = 1;
        }
        Mouse.OverrideCursor = null;
    }

    // ── 组件管理：竖向拖拽排序（经典布局页面管理同款）──
    private Point _componentDragStart;
    private ComponentRow? _componentDragRow;
    private FrameworkElement? _componentDragHandle;
    private bool _componentDragActive;

    private void ComponentRow_DragHandleDown(object sender, MouseButtonEventArgs e)
    {
        _componentDragStart = e.GetPosition(null);
        _componentDragRow = (sender as FrameworkElement)?.DataContext as ComponentRow;
        _componentDragHandle = sender as FrameworkElement;
        _componentDragActive = _componentDragRow is not null;
    }

    private void ComponentRow_DragHandleMove(object sender, MouseEventArgs e)
    {
        if (!_componentDragActive || _componentDragRow is null || _componentDragHandle is null) return;
        if (e.LeftButton != MouseButtonState.Pressed) { EndComponentRowDrag(); return; }
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _componentDragStart.X) < 4 && Math.Abs(pos.Y - _componentDragStart.Y) < 4) return;
        if (_componentDragHandle.IsMouseCaptured) return;
        if (!_componentDragHandle.CaptureMouse()) { EndComponentRowDrag(); return; }
        _componentDragHandle.Opacity = 0.55;
        Mouse.OverrideCursor = System.Windows.Input.Cursors.SizeNS;
    }

    private void ComponentRow_DragHandleUp(object sender, MouseButtonEventArgs e)
    {
        if (!_componentDragActive || _componentDragRow is null) return;
        var list = ComponentManageList;
        var y = e.GetPosition(list).Y;
        var target = _vm.ComponentManageRows.Count - 1;
        for (var i = 0; i < _vm.ComponentManageRows.Count; i++)
        {
            if (list.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement fe)
            {
                var mid = fe.TransformToAncestor(list).Transform(new Point(0, fe.ActualHeight / 2)).Y;
                if (y < mid) { target = i; break; }
                target = i;
            }
        }
        _vm.MoveComponentRowTo(_componentDragRow, target);
        EndComponentRowDrag();
        e.Handled = true;
    }

    private void ComponentRow_LostMouseCapture(object sender, MouseEventArgs e) => EndComponentRowDrag();

    private void EndComponentRowDrag()
    {
        _componentDragActive = false;
        var handle = _componentDragHandle;
        _componentDragHandle = null;
        _componentDragRow = null;
        if (handle is not null)
        {
            try { if (handle.IsMouseCaptured) handle.ReleaseMouseCapture(); } catch { }
            handle.Opacity = 1;
        }
        Mouse.OverrideCursor = null;
    }
}
