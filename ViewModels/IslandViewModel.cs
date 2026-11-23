using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IslandApp.Activities;
using IslandApp.Core;

namespace IslandApp.ViewModels;

public sealed partial class IslandViewModel(
    ActivityCoordinator coordinator,
    TimerActivity timer,
    ShelfActivity shelf,
    Action openSettings,
    Action showClaudeUsage) : ObservableObject
{
    public ActivityCoordinator Coordinator { get; } = coordinator;

    public ShelfActivity Shelf { get; } = shelf;

    public void Collapse()
    {
        if (Coordinator.Current is { } current)
        {
            current.IsExpanded = false;
        }
    }

    [RelayCommand]
    private void ToggleExpand()
    {
        if (Coordinator.Current is MessageActivity message)
        {
            message.OpenCommand.Execute(null);
            return;
        }

        if (Coordinator.Current is { CanExpand: true } current)
        {
            current.IsExpanded = !current.IsExpanded;
        }
    }

    [RelayCommand]
    private void StartTimer(string minutes) =>
        timer.Start(TimeSpan.FromMinutes(int.Parse(minutes, CultureInfo.InvariantCulture)));

    [RelayCommand]
    private void OpenSettings() => openSettings();

    [RelayCommand]
    private void ShowClaudeUsage() => showClaudeUsage();

    [RelayCommand]
    private void Exit() => Application.Current.Shutdown();
}
