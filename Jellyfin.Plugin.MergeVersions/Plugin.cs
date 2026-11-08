using System;
using System.Collections.Generic;
using Jellyfin.Plugin.MergeVersions.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Common.Plugins;

namespace Jellyfin.Plugin.MergeVersions
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages 
    {
        public Plugin(IServerApplicationPaths appPaths, IXmlSerializer xmlSerializer)
            : base(appPaths, xmlSerializer)
        {
            Instance = this;
        }

        public override string Name => "Merge Versions Bato";

        public static Plugin Instance { get; private set; }

        public override string Description
            => "Merge Versions with a preferred primary version (fork of danieladov/jellyfin-plugin-mergeversions)";

        public PluginConfiguration PluginConfiguration => Configuration;

        private readonly Guid _id = new Guid("ee4c6c04-1281-4cea-8a59-66a0ef8b8de4");
        public override Guid Id => _id;

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = "Merge Versions Bato",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.configurationpage.html"
                }
            };
        }
    }
}
