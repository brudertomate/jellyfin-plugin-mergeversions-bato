using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.MergeVersions
{
    /// <summary>
    /// What the latest merge or split run did, kept in memory so the configuration page can show it
    /// live and after a reload. Only the newest <see cref="Capacity"/> entries are kept.
    /// </summary>
    public sealed class RunActivity
    {
        public const int Capacity = 500;

        private readonly object _lock = new();
        private readonly Queue<ActivityEntry> _entries = new();
        private RunSummary _run;
        private long _sequence;

        /// <summary>Starts a new run and forgets the entries of the previous one.</summary>
        public void Start(string operation)
        {
            lock (_lock)
            {
                _entries.Clear();
                _run = new RunSummary
                {
                    Id = Guid.NewGuid(),
                    Operation = operation,
                    State = nameof(RunState.Running),
                    StartedUtc = DateTime.UtcNow,
                };
            }
        }

        public void SetPhase(string phase, int total)
        {
            Update(run =>
            {
                run.Phase = phase;
                run.Total = total;
                run.Done = 0;
            });
        }

        public void SetCurrent(string movie) => Update(run => run.Current = movie);

        public void StepDone() => Update(run => run.Done++);

        public void Merged(string movie, string message) => Add(ActivityLevel.Merged, movie, message, run => run.Merged++);

        public void PrimaryChanged(string movie, string message) => Add(ActivityLevel.Primary, movie, message, run => run.PrimaryChanged++);

        public void PrimaryKept() => Update(run => run.PrimaryKept++);

        public void Split(string movie, string message) => Add(ActivityLevel.Split, movie, message, run => run.Split++);

        public void Skipped(string movie, string message) => Add(ActivityLevel.Skipped, movie, message, run => run.Skipped++);

        public void Error(string movie, string message) => Add(ActivityLevel.Error, movie, message, run => run.Errors++);

        public void Info(string message) => Add(ActivityLevel.Info, null, message, null);

        public void Finish(RunState state, string message = null)
        {
            Update(run =>
            {
                run.State = state.ToString();
                run.Message = message;
                run.Current = null;
                run.EndedUtc = DateTime.UtcNow;
            });
        }

        /// <summary>The current run and every kept entry newer than <paramref name="after"/>.</summary>
        public ActivitySnapshot GetSnapshot(long after = 0)
        {
            lock (_lock)
            {
                return new ActivitySnapshot
                {
                    Run = _run?.Clone(),
                    Entries = _entries.Where(entry => entry.Sequence > after).ToList(),
                    LastSequence = _sequence,
                };
            }
        }

        private void Add(ActivityLevel level, string movie, string message, Action<RunSummary> count)
        {
            lock (_lock)
            {
                if (_run == null)
                {
                    return;
                }

                count?.Invoke(_run);
                _entries.Enqueue(new ActivityEntry
                {
                    Sequence = ++_sequence,
                    TimeUtc = DateTime.UtcNow,
                    Level = level.ToString(),
                    Movie = movie,
                    Message = message,
                });
                while (_entries.Count > Capacity)
                {
                    _entries.Dequeue();
                }
            }
        }

        private void Update(Action<RunSummary> change)
        {
            lock (_lock)
            {
                if (_run != null)
                {
                    change(_run);
                }
            }
        }
    }

    public enum RunState
    {
        Running,
        Completed,
        Failed,
        Cancelled,
    }

    public enum ActivityLevel
    {
        Info,
        Merged,
        Primary,
        Split,
        Skipped,
        Error,
    }

    public sealed class ActivityEntry
    {
        public long Sequence { get; set; }

        public DateTime TimeUtc { get; set; }

        /// <summary>An <see cref="ActivityLevel"/> name, sent as text to the page.</summary>
        public string Level { get; set; }

        public string Movie { get; set; }

        public string Message { get; set; }
    }

    public sealed class RunSummary
    {
        public Guid Id { get; set; }

        /// <summary>"Merge" or "Split".</summary>
        public string Operation { get; set; }

        /// <summary>A <see cref="RunState"/> name.</summary>
        public string State { get; set; }

        public DateTime StartedUtc { get; set; }

        public DateTime? EndedUtc { get; set; }

        public string Phase { get; set; }

        public string Current { get; set; }

        public int Total { get; set; }

        public int Done { get; set; }

        public int Merged { get; set; }

        public int PrimaryChanged { get; set; }

        public int PrimaryKept { get; set; }

        public int Split { get; set; }

        public int Skipped { get; set; }

        public int Errors { get; set; }

        public string Message { get; set; }

        public RunSummary Clone() => (RunSummary)MemberwiseClone();
    }

    public sealed class ActivitySnapshot
    {
        public RunSummary Run { get; set; }

        public List<ActivityEntry> Entries { get; set; }

        public long LastSequence { get; set; }
    }
}
