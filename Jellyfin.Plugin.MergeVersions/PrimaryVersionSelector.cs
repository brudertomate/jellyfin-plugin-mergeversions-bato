using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.MergeVersions
{
    /// <summary>
    /// Picks which version of a movie becomes the preselected primary version.
    /// </summary>
    public static class PrimaryVersionSelector
    {
        public const string DefaultOrder = "1080,720,2160,SD";

        /// <summary>
        /// Maps a video width to a resolution class. Uses the width because cropped
        /// movies keep it while their height shrinks (1920x800 is still 1080p).
        /// </summary>
        public static string Classify(int width)
        {
            if (width >= 2560)
            {
                return "2160";
            }

            if (width >= 1400)
            {
                return "1080";
            }

            return width >= 1000 ? "720" : "SD";
        }

        /// <summary>
        /// Returns the index of the version to preselect, or -1 while a width is still unknown.
        /// Classes missing from <paramref name="order"/> rank last, so a version is always picked.
        /// Within one class the widest version wins.
        /// </summary>
        public static int SelectIndex(IReadOnlyList<int> widths, string order)
        {
            if (widths == null || widths.Count == 0 || widths.Any(width => width <= 0))
            {
                return -1;
            }

            var ranks = ParseOrder(order);
            return Enumerable.Range(0, widths.Count)
                .OrderBy(index => ranks.IndexOf(Classify(widths[index])) is var rank && rank >= 0 ? rank : int.MaxValue)
                .ThenByDescending(index => widths[index])
                .First();
        }

        /// <summary>
        /// Returns the creation date the primary version needs so it is strictly newer than all other versions.
        /// </summary>
        public static DateTime GetPrimaryDateCreated(DateTime primaryDate, IEnumerable<DateTime> otherDates)
        {
            var newestOther = otherDates.DefaultIfEmpty(DateTime.MinValue).Max();
            return primaryDate > newestOther ? primaryDate : newestOther.AddSeconds(1);
        }

        private static List<string> ParseOrder(string order)
        {
            var parsed = (order ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(entry => entry.TrimEnd('p', 'P').ToUpperInvariant())
                .ToList();

            return parsed.Count > 0 ? parsed : ParseOrder(DefaultOrder);
        }
    }
}
