using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit.Abstractions;

namespace HdrUpscaleManager.UiTests
{
    /* The "Unity/Unreal, generic" install buttons write an IsGeneric renodx_installs.json
       record, and tests that remove the addon file directly (no Uninstall click) would
       otherwise leave that record behind, so the game would keep reading "Installed". */
    internal static class GenericRenoDxRecordCleanup
    {
        private const string RecordStoreFileName = "renodx_installs.json";

        public static void RemoveByGameName(string gameName, ITestOutputHelper output)
        {
            try
            {
                var path = PluginPaths.RecordStorePath(RecordStoreFileName);
                if (!File.Exists(path)) return;

                var all = JsonConvert.DeserializeObject<Dictionary<string, JObject>>(File.ReadAllText(path));
                if (all == null) return;

                var staleKeys = all
                    .Where(kv => string.Equals((string)kv.Value["GameName"], gameName, StringComparison.OrdinalIgnoreCase))
                    .Select(kv => kv.Key)
                    .ToList();
                if (staleKeys.Count == 0) return;

                foreach (var key in staleKeys) all.Remove(key);
                File.WriteAllText(path, JsonConvert.SerializeObject(all, Formatting.Indented));
                output.WriteLine($"Removed {staleKeys.Count} leftover generic RenoDX record(s) for '{gameName}'.");
            }
            catch (Exception ex)
            {
                output.WriteLine($"Couldn't remove the generic RenoDX record for '{gameName}': {ex.Message}");
            }
        }
    }
}
