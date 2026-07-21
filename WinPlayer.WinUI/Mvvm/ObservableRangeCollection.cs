using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace WinPlayer.WinUI.Mvvm;

/// <summary>
/// 支持用一次 Reset 通知替换全部内容，避免大量逐项通知长时间占用 UI 线程。
/// </summary>
public sealed class ObservableRangeCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        Items.Clear();
        foreach (T item in items)
            Items.Add(item);

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(
            NotifyCollectionChangedAction.Reset));
    }
}
