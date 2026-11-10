using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace RepoBackup.Desktop.Infrastructure;

// WPF supports a Reset notification, but not multi-item Add notifications.
public sealed class PageCollection<T> : ObservableCollection<T>
{
    public void Replace(IEnumerable<T> values)
    {
        var copy = values.ToArray();
        Items.Clear();
        foreach (var value in copy) Items.Add(value);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
