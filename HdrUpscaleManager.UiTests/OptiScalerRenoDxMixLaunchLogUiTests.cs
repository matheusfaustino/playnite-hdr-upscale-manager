using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using Newtonsoft.Json;
using Xunit;
using Xunit.Abstractions;

namespace HdrUpscaleManager.UiTests
{
    /* Scenario 7 of PLAN.md §9 — OptiScaler + RenoDX mix. Unlike scenario 6 (OptiScaler + Luma,
       genuinely unhandled), this one is expected to actually work: RenoDX ships as a plain
       .addon64 dropped into the game folder (AddonInstallService), and ReShade scans that same
       folder for add-ons regardless of which name IT currently occupies — so RenoDX riding on
       ReShade wherever OptiScaler's coexistence logic put it (ReShadeCoexistName, under the
       game's real deploy directory) should need no special handling at all. This test's job is
       to confirm that expectation holds for real, not just on paper.

       Deep Rock Galactic isn't on RenoDX's wiki-matched list, so this uses the "Unreal Engine,
       generic" fallback button — same as
       RenoDxUnityGenericLaunchLogUiTests: the addon file is found via recursive search rather
       than record polling, and the IsGeneric record the install writes is dropped in cleanup
       through GenericRenoDxRecordCleanup.

       Install order: OptiScaler first, then RenoDX — exercises the ReShade-auto-install-for-
       RenoDX path landing correctly at ReShadeCoexistName (since OptiScaler already occupies the
       normal proxy name) under Deep Rock Galactic's real nested deploy directory
       (FSD\Binaries\Win64\, not the install root — see OptiScalerReShadeCoexistenceLaunchLogUiTests
       for why that distinction matters here). */
    [Collection(PlayniteUiCollection.Name)]
    public class OptiScalerRenoDxMixLaunchLogUiTests
    {
        private const string GameName = "Deep Rock Galactic";
        private const string GameConfigKey = "DEEPROCKGALACTIC";
        private const string OptiScalerInstallButton = "OptiScalerInstallNoDlss";
        private const string OptiScalerRecordStoreFileName = "optiscaler_installs.json";
        private const string ReShadeCoexistName = "ReShade64.dll"; // OptiScalerService.ReShadeCoexistName

        private static readonly string[] RenoDxAddonPatterns = { "*.addon64", "*.addon32" };

        private readonly ITestOutputHelper output;

        public OptiScalerRenoDxMixLaunchLogUiTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        [Fact]
        public void InstallOptiScalerThenRenoDx_RealGame_RenoDxRidesOnCoexistingReShade()
        {
            var game = LocalTestGameConfig.Resolve(GameConfigKey);
            var dir = game.InstallDirectory;
            GuardNoPreExistingInstall(dir);

            var testStartedUtc = DateTime.UtcNow;
            using var fixture = new PlayniteFixture();
            fixture.GetMainWindow();
            var managerWindow = ManagerWindowNavigation.Open(fixture, GameName, output);
            string resolvedDir = null;
            string addonPath = null;

            try
            {
                var record = ClickOptiScalerInstallUntilRecordAppears(managerWindow);
                resolvedDir = record.InstallPath;
                output.WriteLine($"OptiScaler installed as '{record.DllName}' under '{resolvedDir}'.");

                const int maxAttempts = 10;
                var appeared = false;
                for (var attempt = 1; attempt <= maxAttempts && !appeared; attempt++)
                {
                    ManagerSectionNavigation.ClickButton(managerWindow, "RenoDxInstallUnrealGeneric", output);
                    appeared = Retry.WhileFalse(
                        () => (addonPath = FindFileRecursive(resolvedDir, RenoDxAddonPatterns)) != null,
                        timeout: TimeSpan.FromSeconds(10),
                        interval: TimeSpan.FromMilliseconds(500)).Result;
                }

                if (!appeared)
                {
                    WindowScreenshot.CaptureOnTimeout(managerWindow, nameof(InstallOptiScalerThenRenoDx_RealGame_RenoDxRidesOnCoexistingReShade), output);
                    output.WriteLine(AutomationTreeDumper.Dump(managerWindow));
                }
                Assert.True(appeared, $"RenoDX (Unreal Engine, generic) install didn't complete for '{GameName}' — no .addon64/.addon32 appeared under '{resolvedDir}'.");
                output.WriteLine($"RenoDX installed as '{addonPath}'.");

                var reShadeCoexistPath = Path.Combine(resolvedDir, ReShadeCoexistName);
                Assert.True(File.Exists(reShadeCoexistPath),
                    $"RenoDX needs ReShade as a host — EnsureReShadeInstalledForRenoDxAsync should have auto-installed " +
                    $"it, landing at '{ReShadeCoexistName}' under '{resolvedDir}' since OptiScaler already occupies the " +
                    "normal proxy name. It never appeared.");
                output.WriteLine($"ReShade (auto-installed for RenoDX, coexisting with OptiScaler) found at '{reShadeCoexistPath}'.");

                managerWindow.Close();
                managerWindow = null;

                var logContents = GameLaunchAndLogCapture.LaunchWaitForLogsThenKill(
                    game.GameId, resolvedDir, new[] { "OptiScaler.log", "ReShade.log" }, output);

                foreach (var kvp in logContents)
                {
                    output.WriteLine($"--- {kvp.Key} (first 3000 chars) ---");
                    var content = kvp.Value ?? "(missing)";
                    output.WriteLine(content.Length > 3000 ? content.Substring(0, 3000) : content);
                    output.WriteLine("--- end ---");
                }

                var optiScalerLog = logContents[Path.Combine(resolvedDir, "OptiScaler.log")];
                var reShadeLog = logContents[Path.Combine(resolvedDir, "ReShade.log")];

                // Same confirmed substrings as OptiScalerOnlyLaunchLogUiTests/RenoDxMatchedLaunchLogUiTests —
                // both components genuinely loaded and did their jobs, not just wrote a non-empty file.
                Assert.Contains("OptiScaler working as dxgi.dll", optiScalerLog);
                Assert.Contains("Registered add-on \"RenoDX\"", reShadeLog);
            }
            finally
            {
                CleanUp(fixture, managerWindow, dir, resolvedDir, addonPath, testStartedUtc);
            }
        }

        private void GuardNoPreExistingInstall(string dir)
        {
            var record = FindOptiScalerRecordByGameName(GameName);
            var existingAddon = FindFileRecursive(dir, RenoDxAddonPatterns);
            var existingCoexist = FindFileRecursive(dir, ReShadeCoexistName);
            var existingIni = FindFileRecursive(dir, "OptiScaler.ini");

            if (record != null || existingAddon != null || existingCoexist != null || existingIni != null)
            {
                throw new InvalidOperationException(
                    $"'{GameName}' already has a real OptiScaler/RenoDX install before this test started " +
                    $"(record={record != null}, addon={existingAddon}, coexist={existingCoexist}, ini={existingIni}). " +
                    "This test installs and cleans up both components itself — remove any leftover manually first.");
            }
        }

        private void CleanUp(PlayniteFixture fixture, Window managerWindow, string dir, string resolvedDir, string addonPath, DateTime testStartedUtc)
        {
            try
            {
                if (managerWindow == null)
                {
                    // Reuses the outer PlayniteFixture — see LumaMatchedLaunchLogUiTests.CleanUp
                    // for why constructing a second one here was a real bug, not a flake.
                    managerWindow = ManagerWindowNavigation.Open(fixture, GameName, output);
                    CleanUpViaManagerWindow(managerWindow, dir, resolvedDir, addonPath, testStartedUtc);
                    try { managerWindow.Close(); } catch (Exception ex) { output.WriteLine($"managerWindow.Close() failed during cleanup: {ex.Message}"); }
                }
                else
                {
                    CleanUpViaManagerWindow(managerWindow, dir, resolvedDir, addonPath, testStartedUtc);
                    try { managerWindow.Close(); } catch (Exception ex) { output.WriteLine($"managerWindow.Close() failed during cleanup: {ex.Message}"); }
                }
            }
            catch (Exception ex)
            {
                output.WriteLine($"Cleanup failed — '{GameName}' may need manual cleanup: {ex.Message}");
                throw;
            }
        }

        private void CleanUpViaManagerWindow(Window managerWindow, string dir, string resolvedDir, string addonPath, DateTime testStartedUtc)
        {
            if (FindOptiScalerRecordByGameName(GameName) != null)
            {
                const int maxAttempts = 5;
                var recordGone = false;
                for (var attempt = 1; attempt <= maxAttempts && !recordGone; attempt++)
                {
                    ManagerSectionNavigation.ClickButton(managerWindow, "OptiScalerUninstall", output);
                    recordGone = Retry.WhileFalse(
                        () => FindOptiScalerRecordByGameName(GameName) == null,
                        timeout: TimeSpan.FromSeconds(3),
                        interval: TimeSpan.FromMilliseconds(250)).Result;
                }
                if (!recordGone) output.WriteLine($"OptiScaler record for '{GameName}' still present after {maxAttempts} Uninstall clicks.");
            }

            // OptiScaler's own Uninstall restores ReShade from ReShadeCoexistName back to its
            // normal proxy name (RestoreReShadeFromCoexistName) — ReShade itself has no
            // Uninstall action, so whatever's left of it (under either name) is this test's to
            // remove, same as OptiScalerReShadeCoexistenceLaunchLogUiTests already does.
            var cleanupDir = resolvedDir ?? dir;
            foreach (var candidate in new[] { "d3d9.dll", "dxgi.dll", "opengl32.dll", ReShadeCoexistName })
            {
                CleanUpFreshlyCreatedFile(Path.Combine(cleanupDir, candidate), testStartedUtc);
            }
            CleanUpFreshlyCreatedFile(Path.Combine(cleanupDir, "OptiScaler.log"), testStartedUtc);
            CleanUpFreshlyCreatedFile(Path.Combine(cleanupDir, "ReShade.log"), testStartedUtc);
            CleanUpFreshlyCreatedFile(Path.Combine(cleanupDir, "ReShade.ini"), testStartedUtc);
            CleanUpFreshlyCreatedFile(addonPath, testStartedUtc);
            GenericRenoDxRecordCleanup.RemoveByGameName(GameName, output);

            if (resolvedDir == null)
            {
                foreach (var candidate in new[] { "d3d9.dll", "dxgi.dll", "opengl32.dll", ReShadeCoexistName, "OptiScaler.ini", "OptiScaler.log", "ReShade.log", "ReShade.ini" })
                {
                    var found = FindFileRecursive(dir, candidate);
                    if (found != null) CleanUpFreshlyCreatedFile(found, testStartedUtc);
                }
                var strayAddon = FindFileRecursive(dir, RenoDxAddonPatterns);
                if (strayAddon != null) CleanUpFreshlyCreatedFile(strayAddon, testStartedUtc);
            }
        }

        private OptiScalerRecordDto ClickOptiScalerInstallUntilRecordAppears(Window managerWindow)
        {
            const int maxAttempts = 10;
            OptiScalerRecordDto record = null;

            for (var attempt = 1; attempt <= maxAttempts && record == null; attempt++)
            {
                ManagerSectionNavigation.ClickButton(managerWindow, OptiScalerInstallButton, output);
                Retry.WhileFalse(
                    () => (record = FindOptiScalerRecordByGameName(GameName)) != null,
                    timeout: TimeSpan.FromSeconds(6),
                    interval: TimeSpan.FromMilliseconds(500));
            }

            if (record == null)
            {
                throw new InvalidOperationException(
                    $"OptiScaler install didn't complete for '{GameName}' after {maxAttempts} Install clicks. " +
                    $"Record store checked: {PluginPaths.RecordStorePath(OptiScalerRecordStoreFileName)}");
            }

            return record;
        }

        private static OptiScalerRecordDto FindOptiScalerRecordByGameName(string gameName)
        {
            var path = PluginPaths.RecordStorePath(OptiScalerRecordStoreFileName);
            if (!File.Exists(path)) return null;

            try
            {
                var all = JsonConvert.DeserializeObject<Dictionary<string, OptiScalerRecordDto>>(File.ReadAllText(path));
                return all?.Values.FirstOrDefault(r => string.Equals(r.GameName, gameName, StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return null;
            }
        }

        private static string FindFileRecursive(string gameInstallDirectory, string pattern)
        {
            try
            {
                return Directory.EnumerateFiles(gameInstallDirectory, pattern, SearchOption.AllDirectories).FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }

        private static string FindFileRecursive(string gameInstallDirectory, string[] patterns) =>
            patterns.Select(p => FindFileRecursive(gameInstallDirectory, p)).FirstOrDefault(p => p != null);

        private void CleanUpFreshlyCreatedFile(string path, DateTime testStartedUtc)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

            if (File.GetCreationTimeUtc(path) >= testStartedUtc.AddSeconds(-2))
            {
                File.Delete(path);
                output.WriteLine($"Cleaned up '{path}' (created by this test run).");
            }
            else
            {
                output.WriteLine($"Leaving '{path}' in place — it predates this test run.");
            }
        }

        private class OptiScalerRecordDto
        {
            public string GameName { get; set; }
            public string InstallPath { get; set; }
            public string DllName { get; set; }
            public List<string> InstalledFiles { get; set; }
            public bool OptiPatcherInstalled { get; set; }
            public bool DlssDllInstalled { get; set; }
        }
    }
}
