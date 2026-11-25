using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using EraTrans.Core;

namespace EraTrans.App;

internal sealed class FileTreeNodeViewModel : INotifyPropertyChanged
{
    private Brush _statusBrush;
    private string _toolTipText;
    private bool _isExpanded;
    private bool _isSelected;

    public FileTreeNodeViewModel(
        string displayName,
        string toolTipText,
        Brush statusBrush,
        bool isFolder,
        FileTreeNodeViewModel? parent,
        IReadOnlyList<FileTreeNodeViewModel> children,
        DictionaryEditorFile? file = null,
        DictionaryFileTreeFolder? folder = null,
        bool isExpanded = false)
    {
        DisplayName = displayName;
        _toolTipText = toolTipText;
        _statusBrush = statusBrush;
        IsFolder = isFolder;
        Parent = parent;
        Children = children;
        File = file;
        Folder = folder;
        _isExpanded = isExpanded;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string DisplayName { get; }
    public bool IsFolder { get; }
    public FontWeight LabelFontWeight => IsFolder ? FontWeights.SemiBold : FontWeights.Normal;
    public DictionaryEditorFile? File { get; }
    public DictionaryFileTreeFolder? Folder { get; }
    public FileTreeNodeViewModel? Parent { get; }
    public IReadOnlyList<FileTreeNodeViewModel> Children { get; }

    public Brush StatusBrush
    {
        get => _statusBrush;
        set
        {
            if (ReferenceEquals(_statusBrush, value))
            {
                return;
            }

            _statusBrush = value;
            OnPropertyChanged();
        }
    }

    public string ToolTipText
    {
        get => _toolTipText;
        set
        {
            if (string.Equals(_toolTipText, value, StringComparison.Ordinal))
            {
                return;
            }

            _toolTipText = value;
            OnPropertyChanged();
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

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
