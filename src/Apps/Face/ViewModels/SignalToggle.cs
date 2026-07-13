using CommunityToolkit.Mvvm.ComponentModel;

namespace Face.ViewModels;

/// <summary>One selectable signal in the Waveform tab's checkbox list.</summary>
public partial class SignalToggle : ObservableObject {
    public string Name { get; }

    [ObservableProperty] public partial bool IsSelected { get; set; }

    public SignalToggle(string name, bool isSelected) {
        Name = name;
        IsSelected = isSelected;
    }
}