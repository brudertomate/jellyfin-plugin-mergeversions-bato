using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.MergeVersions.ScheduledTasks;

public abstract class VersionRepairTask : IScheduledTask, IConfigurableScheduledTask
{
    protected readonly MergeVersionsManager Manager;
    protected VersionRepairTask(MergeVersionsManager manager) => Manager = manager;
    public abstract Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken);
    public abstract string Name { get; }
    public abstract string Key { get; }
    public string Description => Name;
    public string Category => "Merge Versions Bato";
    public bool IsHidden => true;
    public bool IsEnabled => true;
    public bool IsLogged => true;
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();
}

public sealed class AnalyzeVersionLinksTask : VersionRepairTask
{
    public AnalyzeVersionLinksTask(MergeVersionsManager manager) : base(manager) { }
    public override string Name => "Analyze version links";
    public override string Key => "BatoAnalyzeVersionLinksTask";
    public override Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => Manager.AnalyzeVersionLinksAsync(progress, cancellationToken);
}

public sealed class RepairOrphanedVersionsTask : VersionRepairTask
{
    public RepairOrphanedVersionsTask(MergeVersionsManager manager) : base(manager) { }
    public override string Name => "Repair orphaned versions";
    public override string Key => "BatoRepairOrphanedVersionsTask";
    public override Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => Manager.RepairOrphanedVersionsAsync(progress, cancellationToken);
}

public sealed class PreviewSplitTask : VersionRepairTask
{
    public PreviewSplitTask(MergeVersionsManager manager) : base(manager) { }
    public override string Name => "Preview split all movies";
    public override string Key => "BatoPreviewSplitTask";
    public override Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => Manager.PreviewSplitAsync(progress, cancellationToken);
}
