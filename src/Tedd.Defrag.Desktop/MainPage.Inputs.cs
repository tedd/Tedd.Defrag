using Tedd.Defrag.Core;
using Tedd.Defrag.Windows;

namespace Tedd.Defrag.Desktop;

public partial class MainPage
{
    private ConstrainedEntry[] numericInputs = [];
    private bool updatingInputs;
    private ulong supportedAffinity;

    private void InitializeInputs()
    {
        try { supportedAffinity = WorkerLimits.SupportedAffinityMask(); }
        catch (System.ComponentModel.Win32Exception) { supportedAffinity = 0; }
        var definitions = new (ConstrainedEntry Entry, string Name, InputBounds Bounds)[]
        {
            (CompressionWorkersEntry, "Compression workers", InputBounds.Workers),
            (ScanWorkersEntry, "MFT workers", InputBounds.Workers),
            (PlanningWorkersEntry, "Planner workers", InputBounds.Workers),
            (MoveQueueEntry, "Move queue depth", InputBounds.MoveQueue),
            (MemoryEntry, "Memory cap", InputBounds.Memory),
            (IoEntry, "Relocation bandwidth", InputBounds.NonnegativeInt),
            (AffinityEntry, "CPU affinity", InputBounds.Affinity with { Maximum = supportedAffinity, AllowedBits = supportedAffinity }),
            (BudgetEntry, "Write budget", InputBounds.MiB),
            (MinutesEntry, "Time limit", InputBounds.NonnegativeInt),
            (BoundaryEntry, "Shrink boundary", InputBounds.MiB),
            (MinFragmentsEntry, "Minimum fragments", InputBounds.Fragments),
            (MinFileSizeEntry, "Minimum file size", InputBounds.MiB),
            (MaxFileSizeEntry, "Maximum file size", InputBounds.MiB),
            (ParallelEntry, "Concurrent volumes", InputBounds.ConcurrentVolumes)
        };
        foreach (var (entry, name, bounds) in definitions)
        {
            entry.FieldName = name;
            entry.Configure(bounds);
        }
        numericInputs = definitions.Select(definition => definition.Entry).ToArray();
        foreach (var entry in numericInputs) entry.ValidationChanged += OnNumericInputChanged;
    }

    private void OnNumericInputChanged(object? sender, EventArgs e)
    {
        if (_restoringSettings || updatingInputs) return;
        UpdateVolumeActions();
        SaveDesktopSettings();
    }

    private void OnReviewInput(object? sender, EventArgs e)
    {
        ShowSettings(true);
        AdvancedPanel.IsVisible = true;
        InvalidInput(_selectedPolicy.Operation)?.Focus();
    }

    private void UpdateInputApplicability()
    {
        if (numericInputs.Length == 0 || updatingInputs) return;
        updatingInputs = true;
        try
        {
            MinFragmentsEntry.IsEnabled = true;
            AffinityEntry.IsEnabled = supportedAffinity != 0;
            bool shrink = _selectedPolicy.Operation == Operation.PrepareShrink;
            BoundaryEntry.IsEnabled = shrink;
            ulong maximum = _volume is { SizeBytes: > 0 } volume
                ? Math.Max(1UL, (ulong)(volume.SizeBytes / (1024 * 1024))) : InputBounds.MaximumMiB;
            BoundaryEntry.Configure(new(shrink ? 1UL : 0UL, maximum, Required: shrink), normalize: false);
            MaxFileSizeEntry.SetExternalError(
                MinFileSizeEntry.HasValidNumber && MaxFileSizeEntry.HasValidNumber &&
                !InputBounds.SizeRangeIsValid(MinFileSizeEntry.Value, MaxFileSizeEntry.Value)
                    ? $"Use 0 for unlimited, or at least {MinFileSizeEntry.Value:N0} MiB." : null);
            SaveConcurrencyButton.IsEnabled = ParallelEntry.IsInputValid;
        }
        finally { updatingInputs = false; }
    }

    private ConstrainedEntry? InvalidInput(Operation operation) => numericInputs.FirstOrDefault(entry =>
        entry != ParallelEntry &&
        (entry != MinFragmentsEntry || operation is Operation.MinimumWrite or Operation.FilesOnly) &&
        (entry != BoundaryEntry || operation == Operation.PrepareShrink) && !entry.IsInputValid);

    private static long InputBytes(ConstrainedEntry entry) => checked((long)entry.Value * 1024 * 1024);
}
