using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Media;
using EzViewer.Core;

namespace EzViewer.Library;

/// <summary>封面网格中的一本漫画。封面在第一次被绑定读取时才排队加载。</summary>
public sealed class ComicTile : INotifyPropertyChanged
{
    private ImageSource? _cover;
    private bool _coverFailed;

    public ComicTile(ComicItem item, string subtitle)
    {
        Item = item;
        Subtitle = subtitle;
        SearchKey = item.Name + "\u0001" + subtitle;
    }

    public ComicItem Item { get; }

    public string Title => Item.Name;

    public string Subtitle { get; }

    public string KindLabel => Item.KindLabel;

    public string ToolTipText => Item.Path;

    internal string SearchKey { get; }

    public ImageSource? Cover
    {
        get
        {
            ThumbnailService.Instance.OnCoverAccessed(this);
            return _cover;
        }
        internal set
        {
            if (ReferenceEquals(_cover, value)) return;
            _cover = value;
            OnPropertyChanged(nameof(Cover));
        }
    }

    internal ImageSource? CoverValue => _cover;

    public bool CoverFailed
    {
        get => _coverFailed;
        internal set
        {
            if (_coverFailed == value) return;
            _coverFailed = value;
            OnPropertyChanged(nameof(CoverFailed));
        }
    }

    // 以下字段只在 ThumbnailService 的锁内访问
    internal LinkedListNode<ComicTile>? QueueNode;
    internal LinkedListNode<ComicTile>? LruNode;
    internal bool Loading;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>网格的一行。按行虚拟化，只有可见的行才会创建控件。</summary>
public sealed class TileRow
{
    public TileRow(IReadOnlyList<ComicTile> tiles) => Tiles = tiles;

    public IReadOnlyList<ComicTile> Tiles { get; }
}

public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        Items.Clear();
        foreach (var item in items) Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
