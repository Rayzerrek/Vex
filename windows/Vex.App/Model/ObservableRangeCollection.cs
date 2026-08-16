using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Vex.App.Model;

/// <summary>
/// An <see cref="ObservableCollection{T}"/> that supports bulk replacements
/// to avoid multiple CollectionChanged notifications during large directory loads.
/// </summary>
public class ObservableRangeCollection<T> : ObservableCollection<T>
{
    private static readonly PropertyChangedEventArgs CountProp = new(nameof(Count));
    private static readonly PropertyChangedEventArgs ItemProp = new("Item[]");
    private static readonly NotifyCollectionChangedEventArgs ResetEvent = new(NotifyCollectionChangedAction.Reset);

    public void ReplaceRange(IEnumerable<T> collection)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var item in collection)
            Items.Add(item);

        OnPropertyChanged(CountProp);
        OnPropertyChanged(ItemProp);
        OnCollectionChanged(ResetEvent);
    }
}
