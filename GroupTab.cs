using System.ComponentModel;
using System.Runtime.CompilerServices;
using NewsWatch.Models;

namespace NewsWatch;

/// <summary>One tab above the feed list: Default (Id null) or a user group.</summary>
public sealed class GroupTab : INotifyPropertyChanged
{
    private string _name = "";
    private int _count;
    private int _failing;
    private bool _isEditing;
    private bool _isDropTarget;
    private bool _canMoveLeft;
    private bool _canMoveRight;

    public Guid? Id { get; init; }

    public bool IsDefault => Id == null;

    /// <summary>Default can't be renamed, moved or deleted.</summary>
    public bool CanEdit => !IsDefault;

    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(); }
    }

    /// <summary>Feeds in this group.</summary>
    public int Count
    {
        get => _count;
        set { _count = value; OnPropertyChanged(); OnPropertyChanged(nameof(Summary)); }
    }

    /// <summary>Active feeds in this group whose last check failed: the tab shows a red dot, so a broken feed on a tab that isn't showing still stands out.</summary>
    public int Failing
    {
        get => _failing;
        set { _failing = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasFailing)); OnPropertyChanged(nameof(Summary)); }
    }

    public bool HasFailing => Failing > 0;

    public string Summary =>
        (Count == 1 ? "1 feed" : $"{Count} feeds") +
        (Failing > 0 ? $" · {Failing} failing" : "") +
        (IsDefault ? "" : " · double-click to rename, right-click for more");

    /// <summary>The name is being edited in place.</summary>
    public bool IsEditing
    {
        get => _isEditing;
        set { _isEditing = value; OnPropertyChanged(); }
    }

    /// <summary>A feed row is being dragged over this tab.</summary>
    public bool IsDropTarget
    {
        get => _isDropTarget;
        set { _isDropTarget = value; OnPropertyChanged(); }
    }

    public bool CanMoveLeft
    {
        get => _canMoveLeft;
        set { _canMoveLeft = value; OnPropertyChanged(); }
    }

    public bool CanMoveRight
    {
        get => _canMoveRight;
        set { _canMoveRight = value; OnPropertyChanged(); }
    }

    public static GroupTab Default() => new() { Name = FeedGroup.DefaultName };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
