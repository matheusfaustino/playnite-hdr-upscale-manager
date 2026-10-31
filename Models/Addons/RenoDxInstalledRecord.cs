using System;

namespace HdrUpscaleManager.Models.Addons
{
    /* Tracks a RenoDX addon install per game, modeled on RHI's InstalledModRecord — this is
       what makes a later install idempotent and lets CheckForUpdateAsync tell "still current"
       from "wiki has a newer snapshot" without re-downloading first. */
    public class RenoDxInstalledRecord
    {
        public string GameId { get; set; }
        public string GameName { get; set; }
        public string InstallPath { get; set; }
        public string AddonFileName { get; set; }
        public string MatchedWikiName { get; set; }
        public bool IsGeneric { get; set; }
        public string DownloadUrl { get; set; }
        public string FileHash { get; set; }
        public long? RemoteContentLength { get; set; }
        public DateTimeOffset? RemoteLastModified { get; set; }
        public DateTimeOffset InstalledAtUtc { get; set; }
    }
}
