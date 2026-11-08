using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MergeVersions.ScheduledTasks
{
    public class MergeMoviesTask : IScheduledTask
    {
        private readonly ILogger _logger;
        private readonly MergeVersionsManager _mergeVersionsManager;

        public MergeMoviesTask(
            MergeVersionsManager mergeVersionsManager,
            ILogger<MergeVersionsManager> logger
        )
        {
            _logger = logger;
            _mergeVersionsManager = mergeVersionsManager;
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            _logger.LogInformation("Starting plugin, Merging Movies");
            await _mergeVersionsManager.MergeMoviesAsync(progress, cancellationToken: cancellationToken);
            _logger.LogInformation("All movies merged");
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            // Run this task every 24 hours
            return new[]
            {
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfoType.IntervalTrigger,
                    IntervalTicks = TimeSpan.FromHours(24).Ticks
                }
            };
        }

        public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            return Execute(cancellationToken, progress);
        }

        public string Name => "Merge All Movies";
        public string Key => "BatoMergeMoviesTask";
        public string Description => "Scans all libraries to merge repeated movies";
        public string Category => "Merge Versions Bato";
    }

    public class MergeEpisodesTask : IScheduledTask
    {
        private readonly ILogger _logger;
        private readonly MergeVersionsManager _mergeVersionsManager;

        public MergeEpisodesTask(
            MergeVersionsManager mergeVersionsManager,
            ILogger<MergeVersionsManager> logger
        )
        {
            _logger = logger;
            _mergeVersionsManager = mergeVersionsManager;
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            _logger.LogInformation("Starting plugin, Merging Episodes");
            await _mergeVersionsManager.MergeEpisodesAsync(progress, cancellationToken: cancellationToken);
            _logger.LogInformation("Merging Episodes task finished");
            return;
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            // Run this task every 24 hours
            return new[]
            {
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfoType.IntervalTrigger,
                    IntervalTicks = TimeSpan.FromHours(24).Ticks
                }
            };
        }

        public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            return Execute(cancellationToken, progress);
        }

        public string Name => "Merge All Episodes";
        public string Key => "BatoMergeEpisodesTask";
        public string Description => "Merges all repeated episodes";
        public string Category => "Merge Versions Bato";
    }

    /// <summary>
    /// Splits all merged movies. Hidden from the task list and without triggers, so it only runs
    /// from the configuration page, where Jellyfin's task manager reports its progress and result.
    /// </summary>
    public class SplitMoviesTask : IScheduledTask, IConfigurableScheduledTask
    {
        private readonly MergeVersionsManager _mergeVersionsManager;

        public SplitMoviesTask(MergeVersionsManager mergeVersionsManager)
        {
            _mergeVersionsManager = mergeVersionsManager;
        }

        public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
            => _mergeVersionsManager.SplitMoviesAsync(progress, cancellationToken: cancellationToken);

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();

        public string Name => "Split All Movies";
        public string Key => "BatoSplitMoviesTask";
        public string Description => "Splits all merged movies";
        public string Category => "Merge Versions Bato";
        public bool IsHidden => true;
        public bool IsEnabled => true;
        public bool IsLogged => true;
    }

    /// <summary>
    /// Splits all merged episodes. Hidden like <see cref="SplitMoviesTask"/>.
    /// </summary>
    public class SplitEpisodesTask : IScheduledTask, IConfigurableScheduledTask
    {
        private readonly MergeVersionsManager _mergeVersionsManager;

        public SplitEpisodesTask(MergeVersionsManager mergeVersionsManager)
        {
            _mergeVersionsManager = mergeVersionsManager;
        }

        public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
            => _mergeVersionsManager.SplitEpisodesAsync(progress, cancellationToken: cancellationToken);

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();

        public string Name => "Split All Episodes";
        public string Key => "BatoSplitEpisodesTask";
        public string Description => "Splits all merged episodes";
        public string Category => "Merge Versions Bato";
        public bool IsHidden => true;
        public bool IsEnabled => true;
        public bool IsLogged => true;
    }
}
