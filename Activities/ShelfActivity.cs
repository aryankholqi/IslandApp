using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Input;
using IslandApp.Core;
using IslandApp.Interop;

namespace IslandApp.Activities;

/// <summary>A file (or folder) held on the shelf. Only the path is kept: the file itself never moves.</summary>
public sealed record ShelfFile(string Path, string Name, ImageSource? Icon);

/// <summary>
/// File shelf: drop files on the island to hold them, drag them back out somewhere else (IslandWindow handles the
/// drag and drop). Stays while it holds anything; music keeps the island and the shelf moves to the bubble.
/// </summary>
public sealed partial class ShelfActivity : IslandActivity
{
    private readonly ActivityCoordinator _coordinator;

    public ShelfActivity(ActivityCoordinator coordinator)
    {
        _coordinator = coordinator;
        Files.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(CountText));
            OnPropertyChanged(nameof(FirstIcon));
        };
    }

    public ObservableCollection<ShelfFile> Files { get; } = [];

    public override ActivityPriority Priority => ActivityPriority.Shelf;

    public override bool CanBeCompanion => true;

    public override Size CompactSize { get; } = new(250, 36);

    public override Size? ExpandedSize { get; } = new Size(420, 136);

    public string CountText => Files.Count == 1 ? Files[0].Name : $"{Files.Count} files";

    public ImageSource? FirstIcon => Files.Count > 0 ? Files[0].Icon : null;

    public void Add(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (Files.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
            Files.Add(new ShelfFile(path, string.IsNullOrEmpty(name) ? path : name, NativeMethods.GetFileIcon(path)));
        }

        if (Files.Count > 0)
        {
            _coordinator.Show(this);
        }
    }

    public void Remove(IEnumerable<ShelfFile> files)
    {
        foreach (var file in files.ToList())
        {
            Files.Remove(file);
        }

        if (Files.Count == 0)
        {
            _coordinator.Hide(this);
        }
    }

    public static void Open(ShelfFile file)
    {
        try
        {
            Process.Start(new ProcessStartInfo(file.Path) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            Debug.WriteLine($"Can't open {file.Path}: {ex.Message}"); // moved or deleted since it was dropped
        }
    }

    [RelayCommand]
    private void RemoveFile(ShelfFile file) => Remove([file]);

    [RelayCommand]
    private void Clear() => Remove(Files);
}
