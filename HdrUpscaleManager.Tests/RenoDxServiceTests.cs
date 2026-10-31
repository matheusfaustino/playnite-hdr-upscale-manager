using System;
using System.Collections.Generic;
using System.IO;
using HdrUpscaleManager.Models;
using HdrUpscaleManager.Models.Addons;
using HdrUpscaleManager.Services.Addons;
using Xunit;

namespace HdrUpscaleManager.Tests
{
    public class RenoDxServiceTests
    {
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ResolveGenericAddon_UnrealEngine_AlwaysReturns64Bit(bool is64Bit)
        {
            var addon = RenoDxService.ResolveGenericAddon(RenoDxEngine.UnrealEngine, is64Bit);

            Assert.Equal("renodx-unrealengine.addon64", addon.FileName);
            Assert.EndsWith(".addon64", addon.Url);
        }

        [Fact]
        public void ResolveGenericAddon_UnityEngine64Bit_ReturnsAddon64()
        {
            var addon = RenoDxService.ResolveGenericAddon(RenoDxEngine.UnityEngine, is64Bit: true);

            Assert.Equal("renodx-unityengine.addon64", addon.FileName);
            Assert.EndsWith(".addon64", addon.Url);
        }

        [Fact]
        public void ResolveGenericAddon_UnityEngine32Bit_ReturnsAddon32()
        {
            var addon = RenoDxService.ResolveGenericAddon(RenoDxEngine.UnityEngine, is64Bit: false);

            Assert.Equal("renodx-unityengine.addon32", addon.FileName);
            Assert.EndsWith(".addon32", addon.Url);
        }

        [Fact]
        public void ResolveGenericAddon_UnrealEngineUeExtended_ReturnsUeExtendedAddon()
        {
            var addon = RenoDxService.ResolveGenericAddon(RenoDxEngine.UnrealEngine, is64Bit: true, useUeExtended: true);

            Assert.Equal(RenoDxService.UeExtendedAddonFileName, addon.FileName);
            Assert.Contains("ue-extended", addon.Url);
        }

        [Fact]
        public void ResolveGenericAddon_UeExtendedWithManifestOverride_UsesOverrideUrl()
        {
            var manifest = new RemoteManifest
            {
                ComponentUrls = new Dictionary<string, string> { ["ueExtended"] = "https://example.com/custom-ue-ext.addon64" }
            };

            var addon = RenoDxService.ResolveGenericAddon(RenoDxEngine.UnrealEngine, is64Bit: true, useUeExtended: true, manifest: manifest);

            Assert.Equal("https://example.com/custom-ue-ext.addon64", addon.Url);
        }

        [Fact]
        public void ResolveGenericAddon_UnityEngineIgnoresUeExtendedFlag()
        {
            var addon = RenoDxService.ResolveGenericAddon(RenoDxEngine.UnityEngine, is64Bit: true, useUeExtended: true);

            Assert.Equal("renodx-unityengine.addon64", addon.FileName);
        }

        [Fact]
        public void ShouldUseUeExtended_NullManifest_DefaultsTrue()
        {
            Assert.True(RenoDxService.ShouldUseUeExtended("Any Game", null));
        }

        [Fact]
        public void ShouldUseUeExtended_NoOverrides_DefaultsTrue()
        {
            var manifest = new RemoteManifest();

            Assert.True(RenoDxService.ShouldUseUeExtended("Some Unreal Game", manifest));
        }

        [Fact]
        public void ShouldUseUeExtended_NoUeExtendedGames_ReturnsFalse()
        {
            var manifest = new RemoteManifest { NoUeExtendedGames = new List<string> { "Picky Game" } };

            Assert.False(RenoDxService.ShouldUseUeExtended("Picky Game", manifest));
        }

        [Fact]
        public void ShouldUseUeExtended_NativeHdrGameBeatsNoUeExtendedGames()
        {
            var manifest = new RemoteManifest
            {
                NativeHdrGames = new List<string> { "Returnal" },
                NoUeExtendedGames = new List<string> { "Returnal" }
            };

            Assert.True(RenoDxService.ShouldUseUeExtended("Returnal", manifest));
        }

        [Fact]
        public void CreateGenericRecord_SuccessfulInstall_FlagsGenericAndKeepsInstallPath()
        {
            /* Deep Rock Galactic: Survivor, confirmed live 2026-10-31: "Unity, generic" installed
               fine but wrote no record, so the window kept saying "Not installed." and left
               Uninstall greyed out. */
            var install = new AddonInstallResult
            {
                Success = true,
                InstalledPath = Path.Combine("game", "renodx-unityengine.addon64"),
                FileHash = "abc",
            };

            var record = RenoDxService.CreateGenericRecord("id-1", "Deep Rock Galactic: Survivor", install);

            Assert.True(record.IsGeneric);
            Assert.Equal("id-1", record.GameId);
            Assert.Equal("Deep Rock Galactic: Survivor", record.GameName);
            Assert.Equal(install.InstalledPath, record.InstallPath);
            Assert.Equal("renodx-unityengine.addon64", record.AddonFileName);
            Assert.Equal("abc", record.FileHash);
            Assert.Null(record.MatchedWikiName);
        }

        [Fact]
        public async System.Threading.Tasks.Task CheckForUpdateAsync_GenericRecord_ReturnsGenericNotTrackedWithoutWikiLookup()
        {
            var record = new RenoDxInstalledRecord { GameName = "Some Game", IsGeneric = true };

            /* A cache directory that can't be created would throw if the wiki were consulted. */
            var result = await RenoDxService.CheckForUpdateAsync(record, "\0:invalid");

            Assert.Equal(RenoDxUpdateStatus.GenericNotTracked, result.Status);
        }

        [Fact]
        public void Uninstall_GenericRecord_DeletesTheAddonFile()
        {
            var file = Path.Combine(Path.GetTempPath(), "renodx-generic-" + Guid.NewGuid() + ".addon64");
            File.WriteAllBytes(file, new byte[4]);
            var record = RenoDxService.CreateGenericRecord("id", "Game", new AddonInstallResult { Success = true, InstalledPath = file });

            var result = RenoDxService.Uninstall(record);

            Assert.True(result.Success);
            Assert.False(File.Exists(file));
        }
    }
}
