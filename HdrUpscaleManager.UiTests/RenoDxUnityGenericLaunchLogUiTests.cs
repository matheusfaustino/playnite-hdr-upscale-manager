using System;
using System.IO;
using System.Linq;
using FlaUI.Core.Tools;
using Xunit;
using Xunit.Abstractions;

namespace HdrUpscaleManager.UiTests
{
    /* Scenario 3 of PLAN.md §9 — real-launch log verification for RenoDX's engine-auto-detect
       path (the "Unity, generic" fallback button, RenoDxInstallUnityCommand), never covered by
       RenoDxInstallUiTests (which only exercises the plain wiki-matched "Install" button — see
       that class's own doc comment / GAMES-TO-TEST.md's "Not yet covered" section). Same
       calibration-first principle as RenoDxMatchedLaunchLogUiTests: confirms ReShade actually
       loads and registers the generic RenoDX build at runtime, not just file presence. Asserts on
       `Registered add-on "RenoDX"` — confirmed live 2026-08-10 against a real ReShade.log.

       Deliberately does NOT poll renodx_installs.json for the install itself — the addon file
       is found via recursive search. Generic installs do write an IsGeneric record (since
       v0.3.4), so cleanup removes the addon file directly and then drops that record through
       GenericRenoDxRecordCleanup, same as it already has to do for ReShade (which has no
       Uninstall action).

       Game: Against the Storm — confirmed Unity via real folder scan (UnityPlayer.dll present),
       not on RenoDX's wiki-matched list, so this exercises the auto-detect button specifically.
       Real exe (Against the Storm.exe) sits at the install root. */
    [Collection(PlayniteUiCollection.Name)]
    public class RenoDxUnityGenericLaunchLogUiTests
    {
        private const string GameName = "Against the Storm";
        private const string GameConfigKey = "AGAINSTTHESTORM";

        private static readonly string[] ReShadeProxyDllCandidates = { "d3d9.dll", "dxgi.dll", "opengl32.dll" };
        private static readonly string[] RenoDxAddonPatterns = { "*.addon64", "*.addon32" };

        private readonly ITestOutputHelper output;

        public RenoDxUnityGenericLaunchLogUiTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        [Fact]
        public void InstallRenoDxUnityGenericThenLaunch_RealGame_ReShadeLogShowsAddonScan()
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
                ManagerSectionNavigation.ClickButton(managerWindow, "RenoDxInstallUnity", output);

                var appeared = Retry.WhileFalse(
                    () => (addonPath = FindFileRecursive(dir, RenoDxAddonPatterns)) != null,
                    timeout: TimeSpan.FromSeconds(60),
                    interval: TimeSpan.FromMilliseconds(500)).Result;

                if (!appeared)
                {
                    WindowScreenshot.CaptureOnTimeout(managerWindow, nameof(InstallRenoDxUnityGenericThenLaunch_RealGame_ReShadeLogShowsAddonScan), output);
                    output.WriteLine(AutomationTreeDumper.Dump(managerWindow));
                }
                Assert.True(appeared, $"RenoDX (Unity, generic) install didn't complete for '{GameName}' within 60s — no .addon64/.addon32 appeared anywhere under '{dir}'.");

                resolvedDir = Path.GetDirectoryName(addonPath);
                output.WriteLine($"RenoDX (Unity, generic) installed as '{addonPath}'.");

                /* Regression: generic installs used to write no record, so the window kept saying
                   "Not installed." and Uninstall stayed disabled (found live 2026-10-31). */
                var recordWritten = Retry.WhileFalse(
                    () => GenericRenoDxRecordCleanup.ExistsForGameName(GameName),
                    timeout: TimeSpan.FromSeconds(10),
                    interval: TimeSpan.FromMilliseconds(250)).Result;
                Assert.True(recordWritten, $"RenoDX (Unity, generic) installed for '{GameName}' but no IsGeneric record was written to renodx_installs.json.");

                var reShadeProxyPath = FindExistingReShadeProxyRecursive(resolvedDir);
                Assert.True(reShadeProxyPath != null,
                    $"RenoDX needs ReShade as a host, but no ReShade proxy DLL was found under '{resolvedDir}' — " +
                    "EnsureReShadeInstalledForRenoDxAsync should have auto-installed it first.");
                output.WriteLine($"ReShade (auto-installed for RenoDX) found at '{reShadeProxyPath}'.");

                managerWindow.Close();
                managerWindow = null;

                var logContents = GameLaunchAndLogCapture.LaunchWaitForLogsThenKill(
                    game.GameId, resolvedDir, new[] { "ReShade.log" }, output);

                var reShadeLogPath = Path.Combine(resolvedDir, "ReShade.log");
                var content = logContents[reShadeLogPath];

                output.WriteLine("--- ReShade.log content (first 4000 chars) ---");
                output.WriteLine(content.Length > 4000 ? content.Substring(0, 4000) : content);
                output.WriteLine("--- end ReShade.log content ---");

                // Same confirmed line as RenoDxMatchedLaunchLogUiTests — proves the generic addon
                // build was actually recognized and loaded, not just that ReShade scanned for one.
                Assert.Contains("Registered add-on \"RenoDX\"", content);
            }
            finally
            {
                try { managerWindow?.Close(); } catch (Exception ex) { output.WriteLine($"managerWindow.Close() failed during cleanup: {ex.Message}"); }

                var cleanupDir = resolvedDir ?? dir;
                foreach (var candidate in ReShadeProxyDllCandidates)
                {
                    CleanUpFreshlyCreatedFile(Path.Combine(cleanupDir, candidate), testStartedUtc);
                }
                CleanUpFreshlyCreatedFile(Path.Combine(cleanupDir, "ReShade.log"), testStartedUtc);
                CleanUpFreshlyCreatedFile(Path.Combine(cleanupDir, "ReShade.ini"), testStartedUtc);
                CleanUpFreshlyCreatedFile(addonPath, testStartedUtc);
                GenericRenoDxRecordCleanup.RemoveByGameName(GameName, output);

                if (resolvedDir == null)
                {
                    foreach (var candidate in ReShadeProxyDllCandidates.Append("ReShade.log").Append("ReShade.ini"))
                    {
                        var found = FindFileRecursive(dir, candidate);
                        if (found != null) CleanUpFreshlyCreatedFile(found, testStartedUtc);
                    }
                    var strayAddon = FindFileRecursive(dir, RenoDxAddonPatterns);
                    if (strayAddon != null) CleanUpFreshlyCreatedFile(strayAddon, testStartedUtc);
                }
            }
        }

        private void GuardNoPreExistingInstall(string dir)
        {
            var existingAddon = FindFileRecursive(dir, RenoDxAddonPatterns);
            var existingReShade = FindExistingReShadeProxyRecursive(dir);

            if (existingAddon != null || existingReShade != null)
            {
                throw new InvalidOperationException(
                    $"'{GameName}' already has a real RenoDX/ReShade install before this test started " +
                    $"(addon={existingAddon}, reShadeProxy={existingReShade}). This test installs and cleans " +
                    "up both components itself — remove any leftover manually first.");
            }
        }

        private static string FindExistingReShadeProxyRecursive(string gameInstallDirectory) =>
            ReShadeProxyDllCandidates
                .Select(name => FindFileRecursive(gameInstallDirectory, name))
                .FirstOrDefault(p => p != null);

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
    }
}
