using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MacroFenetre.Models;

public sealed class WindowGroup : INotifyPropertyChanged, IDisposable
{
    private bool _isExpanded;

    public WindowGroup(string processName, IEnumerable<WindowItem> windows, bool isExpanded)
    {
        ProcessName = processName;
        Windows = new ObservableCollection<WindowItem>(windows);
        _isExpanded = isExpanded;

        foreach (var window in Windows)
        {
            window.PropertyChanged += Window_PropertyChanged;
        }
    }

    public string ProcessName { get; }
    public ObservableCollection<WindowItem> Windows { get; }
    public string CountDisplay => $"{Windows.Count} fenêtre{(Windows.Count > 1 ? "s" : string.Empty)}";
    public string SelectedDisplay => $"{Windows.Count(window => window.IsSelected)}/{Windows.Count}";

    public bool? SelectionState
    {
        get
        {
            var selectedCount = Windows.Count(window => window.IsSelected);
            if (selectedCount == 0)
            {
                return false;
            }

            return selectedCount == Windows.Count ? true : null;
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
            {
                return;
            }

            _isExpanded = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetAll(bool isSelected)
    {
        foreach (var window in Windows)
        {
            window.IsSelected = isSelected;
        }

        RefreshSelection();
    }

    public void Dispose()
    {
        foreach (var window in Windows)
        {
            window.PropertyChanged -= Window_PropertyChanged;
        }

        GC.SuppressFinalize(this);
    }

    private void Window_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WindowItem.IsSelected))
        {
            RefreshSelection();
        }
    }

    private void RefreshSelection()
    {
        OnPropertyChanged(nameof(SelectionState));
        OnPropertyChanged(nameof(SelectedDisplay));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
