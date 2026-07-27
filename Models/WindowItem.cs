using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MacroFenetre.Models;

public sealed class WindowItem : INotifyPropertyChanged
{
    private bool _isSelected;

    public required nint Handle { get; init; }
    public required string Title { get; init; }
    public required string ProcessName { get; init; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public string DisplayName => $"{ProcessName}  ·  {Title}";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
