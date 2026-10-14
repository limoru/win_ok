using Automation.Core.Targeting;

namespace Kes.Gui.ViewModels;

public sealed class TargetCandidateViewModel : ObservableObject
{
    private bool _isSelected;

    public TargetCandidateViewModel(TargetCandidate candidate, bool isSelected = true)
    {
        Candidate = candidate;
        _isSelected = isSelected;
    }

    public TargetCandidate Candidate { get; }
    public TargetBinding Binding => Candidate.Binding;
    public string DisplayName => Candidate.DisplayName;
    public string ProcessName => Candidate.ProcessName;
    public string WindowClass => Candidate.WindowClass;
    public string WindowTitle => Candidate.WindowTitle;
    public string SizeText => $"{Candidate.ClientWidth} x {Candidate.ClientHeight}";
    public string Summary => Candidate.Summary;
    public string ProcessPath => string.IsNullOrWhiteSpace(Candidate.ProcessPath)
        ? "无法读取进程路径"
        : Candidate.ProcessPath;
    public bool IsEmulator => Binding.Kind == TargetKind.MuMuEmulator;
    public string ProfileLabel => IsEmulator ? "模拟器" : "PC 版";
    public string StateLabel => Candidate.IsAvailable ? "运行中" : "未运行";

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public override string ToString() => DisplayName;
}