using System.Collections.Generic;
using HdrUpscaleManager.Services.Addons;
using HdrUpscaleManager.Services.ReShade;
using Xunit;

namespace HdrUpscaleManager.Tests
{
    public class PcGamingWikiServiceTests
    {
        [Theory]
        [InlineData("Engine:LS3D", "LS3D")]
        [InlineData("Engine:Unreal Engine 4, Engine:Havok", "Unreal Engine 4")]
        [InlineData("  Engine:Source  ", "Source")]
        [InlineData(null, null)]
        [InlineData("", null)]
        public void CleanEngineName_StripsNamespacePrefixAndTakesFirstListed(string raw, string expected)
        {
            Assert.Equal(expected, PcGamingWikiService.CleanEngineName(raw));
        }

        [Theory]
        [InlineData("Unreal Engine 4", RenoDxEngine.UnrealEngine)]
        [InlineData("unreal engine 5", RenoDxEngine.UnrealEngine)]
        [InlineData("Unity", RenoDxEngine.UnityEngine)]
        [InlineData("LS3D", null)]
        [InlineData(null, null)]
        public void MapEngine_RecognizesUnrealAndUnityCaseInsensitively(string engineName, RenoDxEngine? expected)
        {
            Assert.Equal(expected, PcGamingWikiService.MapEngine(engineName));
        }

        [Fact]
        public void ParseCargoList_SplitsAndTrimsCommaDelimitedValues()
        {
            var result = PcGamingWikiService.ParseCargoList(" 8.1 , 9.0c ,10 ");

            Assert.Equal(new[] { "8.1", "9.0c", "10" }, result);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void ParseCargoList_BlankInput_ReturnsEmptyList(string raw)
        {
            Assert.Empty(PcGamingWikiService.ParseCargoList(raw));
        }

        [Fact]
        public void InferConfidentGraphicsApi_SingleD3D8Version_ReturnsD3D8()
        {
            var result = PcGamingWikiService.InferConfidentGraphicsApi(new List<string> { "8.1" });

            Assert.Equal(GraphicsApi.D3D8, result);
        }

        [Fact]
        public void InferConfidentGraphicsApi_MixOfD3D8And9_ReturnsHighestTier()
        {
            var result = PcGamingWikiService.InferConfidentGraphicsApi(new List<string> { "8.1", "9.0c" });

            Assert.Equal(GraphicsApi.D3D9, result);
        }

        [Fact]
        public void InferConfidentGraphicsApi_IncludesD3D11_ReturnsNull()
        {
            // e.g. BioShock: base DX9 with an optional DX10 path shipped later — but a game that
            // ever lists 11/12 can't be confidently recommended without knowing which path an
            // actual installed executable would load.
            var result = PcGamingWikiService.InferConfidentGraphicsApi(new List<string> { "9.0c", "10", "11" });

            Assert.Null(result);
        }

        [Fact]
        public void InferConfidentGraphicsApi_EmptyList_ReturnsNull()
        {
            Assert.Null(PcGamingWikiService.InferConfidentGraphicsApi(new List<string>()));
        }

        [Fact]
        public void InferConfidentGraphicsApi_UnrecognizedVersionString_ReturnsNull()
        {
            var result = PcGamingWikiService.InferConfidentGraphicsApi(new List<string> { "n/a" });

            Assert.Null(result);
        }

        [Fact]
        public void InferConfidentGraphicsApiForReShade_SingleD3D11Version_ReturnsD3D11()
        {
            // Hollow Knight's real PCGamingWiki entry, confirmed live 2026-08-08 — a single
            // unambiguous "11" that InferConfidentGraphicsApi (DXVK-scoped, ≤10 ceiling) wrongly
            // discards as "not confident" even though there's nothing ambiguous about it.
            var result = PcGamingWikiService.InferConfidentGraphicsApiForReShade(new List<string> { "11" });

            Assert.Equal(GraphicsApi.D3D11, result);
        }

        [Fact]
        public void InferConfidentGraphicsApiForReShade_SingleD3D12Version_ReturnsD3D12()
        {
            var result = PcGamingWikiService.InferConfidentGraphicsApiForReShade(new List<string> { "12" });

            Assert.Equal(GraphicsApi.D3D12, result);
        }

        [Fact]
        public void InferConfidentGraphicsApiForReShade_MultipleDistinctVersions_ReturnsNull()
        {
            // Genuinely ambiguous — a game offering more than one selectable render path with
            // no installed executable to say which one actually loads.
            var result = PcGamingWikiService.InferConfidentGraphicsApiForReShade(new List<string> { "9.0c", "11" });

            Assert.Null(result);
        }

        [Fact]
        public void InferConfidentGraphicsApiForReShade_SameVersionRepeated_ReturnsThatVersion()
        {
            var result = PcGamingWikiService.InferConfidentGraphicsApiForReShade(new List<string> { "11", "11" });

            Assert.Equal(GraphicsApi.D3D11, result);
        }

        [Fact]
        public void InferConfidentGraphicsApiForReShade_EmptyList_ReturnsNull()
        {
            Assert.Null(PcGamingWikiService.InferConfidentGraphicsApiForReShade(new List<string>()));
        }

        [Fact]
        public void InferConfidentGraphicsApiForReShade_UnrecognizedVersionString_ReturnsNull()
        {
            var result = PcGamingWikiService.InferConfidentGraphicsApiForReShade(new List<string> { "n/a" });

            Assert.Null(result);
        }

        [Fact]
        public void InferConfidentlyAboveDxvkRange_OnlyD3D11And12Listed_ReturnsTrue()
        {
            // Control's real PCGamingWiki entry — ambiguous about which of 11/12 actually loads,
            // but that ambiguity doesn't matter for DXVK, which supports neither.
            var result = PcGamingWikiService.InferConfidentlyAboveDxvkRange(new List<string> { "11", "12" });

            Assert.True(result);
        }

        [Fact]
        public void InferConfidentlyAboveDxvkRange_SingleD3D11Version_ReturnsTrue()
        {
            Assert.True(PcGamingWikiService.InferConfidentlyAboveDxvkRange(new List<string> { "11" }));
        }

        [Fact]
        public void InferConfidentlyAboveDxvkRange_IncludesD3D10_ReturnsFalse()
        {
            // A DXVK-eligible path exists, even if the game also offers 11/12 — not "above range".
            var result = PcGamingWikiService.InferConfidentlyAboveDxvkRange(new List<string> { "10", "11" });

            Assert.False(result);
        }

        [Fact]
        public void InferConfidentlyAboveDxvkRange_EmptyList_ReturnsFalse()
        {
            Assert.False(PcGamingWikiService.InferConfidentlyAboveDxvkRange(new List<string>()));
        }

        [Fact]
        public void InferConfidentlyAboveDxvkRange_UnrecognizedVersionString_ReturnsFalse()
        {
            var result = PcGamingWikiService.InferConfidentlyAboveDxvkRange(new List<string> { "n/a" });

            Assert.False(result);
        }

        [Fact]
        public void ParseCargoFirstRow_PermissionDeniedError_Throws()
        {
            /* Confirmed live 2026-10-31: PCGamingWiki now rejects Cargo queries with this body.
               It used to parse as "no rows" and got cached as an empty lookup forever. */
            const string json = "{\"error\":{\"code\":\"permissiondenied\",\"info\":\"You don't have permission to run arbitrary Cargo queries.\"}}";

            Assert.Throws<System.InvalidOperationException>(() => PcGamingWikiService.ParseCargoFirstRow(json));
        }

        [Fact]
        public void ParseCargoFirstRow_NoRows_ReturnsNull()
        {
            Assert.Null(PcGamingWikiService.ParseCargoFirstRow("{\"cargoquery\":[]}"));
        }

        [Fact]
        public void ParseCargoFirstRow_Row_ReturnsTitleObject()
        {
            var row = PcGamingWikiService.ParseCargoFirstRow("{\"cargoquery\":[{\"title\":{\"D3D\":\"11\"}}]}");

            Assert.Equal("11", (string)row["D3D"]);
        }

        [Fact]
        public void IsUsableCachedResult_PageFoundButNoEngineOrApiData_IsStale()
        {
            /* The Deep Rock Galactic: Survivor entry written while Cargo was rejecting queries. */
            var poisoned = new PcGamingWikiLookupResult
            {
                PageTitle = "Deep Rock Galactic: Survivor",
                Direct3DVersionsRaw = new List<string>(),
            };

            Assert.False(PcGamingWikiService.IsUsableCachedResult(poisoned));
        }

        [Fact]
        public void IsUsableCachedResult_NullResult_IsStale()
        {
            Assert.False(PcGamingWikiService.IsUsableCachedResult(null));
        }

        [Fact]
        public void IsUsableCachedResult_EngineOnly_IsUsable()
        {
            Assert.True(PcGamingWikiService.IsUsableCachedResult(
                new PcGamingWikiLookupResult { PageTitle = "X", EngineRaw = "Unity" }));
        }

        [Fact]
        public void IsUsableCachedResult_D3DVersionsOnly_IsUsable()
        {
            Assert.True(PcGamingWikiService.IsUsableCachedResult(
                new PcGamingWikiLookupResult { PageTitle = "X", Direct3DVersionsRaw = new List<string> { "11" } }));
        }

        [Fact]
        public void IsUsableCachedResult_NoWikiPageAtAll_IsUsable()
        {
            /* A game with no PCGamingWiki page is legitimately empty — re-querying it on every
               open would only repeat the same miss. */
            Assert.True(PcGamingWikiService.IsUsableCachedResult(new PcGamingWikiLookupResult()));
        }

        [Fact]
        public void ParseWikitextEngine_UnityRow_ReturnsUnity()
        {
            const string wikitext =
                "|engines      = \n{{Infobox game/row/engine|Unity|name=Unity 6|build=6000.4.7f1|ref=<ref>{{Refcheck|user=x|date=2026-09-22}}</ref>}}\n|release dates= \n";

            Assert.Equal("Unity", PcGamingWikiService.ParseWikitextEngine(wikitext));
        }

        [Fact]
        public void ParseWikitextEngine_FormerEngineListedFirst_PrefersCurrentEngine()
        {
            const string wikitext =
                "|engines      = \n{{Infobox game/row/engine|Unity|name=Unity 5|Former|build=5.4.3f1}}\n" +
                "{{Infobox game/row/engine|Unity|name=Unity 2020|build=2020.2}}\n|release dates= \n";

            Assert.Equal("Unity", PcGamingWikiService.ParseWikitextEngine(wikitext));
        }

        [Fact]
        public void ParseWikitextEngine_NoEnginesRow_ReturnsNull()
        {
            Assert.Null(PcGamingWikiService.ParseWikitextEngine("|engines      = \n|release dates= \n"));
        }

        [Fact]
        public void ParseWikitextDirect3DVersions_SingleVersion_ReturnsIt()
        {
            const string wikitext = "{{API\n|direct3d versions      = 11\n|direct3d notes         = \n}}";

            Assert.Equal(new[] { "11" }, PcGamingWikiService.ParseWikitextDirect3DVersions(wikitext));
        }

        [Fact]
        public void ParseWikitextDirect3DVersions_MultipleVersions_SplitsThem()
        {
            const string wikitext = "{{API\n|direct3d versions      = 11, 12\n|direct3d notes         = \n}}";

            Assert.Equal(new[] { "11", "12" }, PcGamingWikiService.ParseWikitextDirect3DVersions(wikitext));
        }

        [Fact]
        public void ParseWikitextDirect3DVersions_EmptyOrMissing_ReturnsEmptyList()
        {
            Assert.Empty(PcGamingWikiService.ParseWikitextDirect3DVersions("{{API\n|direct3d versions      = \n}}"));
            Assert.Empty(PcGamingWikiService.ParseWikitextDirect3DVersions("no api table here"));
        }
    }
}
