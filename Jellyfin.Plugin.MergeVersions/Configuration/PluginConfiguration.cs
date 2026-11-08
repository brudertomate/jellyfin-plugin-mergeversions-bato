using MediaBrowser.Model.Plugins;
using System;

namespace Jellyfin.Plugin.MergeVersions.Configuration
{
    public class PluginConfiguration : BasePluginConfiguration
    {

        public String[] LocationsExcluded { get; set; }

        /// <summary>
        /// Comma separated resolutions (2160, 1080, 720, SD), most preferred first. Decides which
        /// version of a merged movie is preselected.
        /// </summary>
        public String PrimaryResolutionOrder { get; set; }

        public PluginConfiguration()
        {
            LocationsExcluded = Array.Empty<String>();
            PrimaryResolutionOrder = PrimaryVersionSelector.DefaultOrder;
        }
    }
}
