using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using HdrUpscaleManager.Services.ReShade;
using Newtonsoft.Json.Linq;

namespace HdrUpscaleManager.Services.Addons
{
    public class PcGamingWikiLookupResult
    {
        public string PageTitle { get; set; }
        /* Raw "Engines" text from PCGamingWiki's Infobox_game table (e.g. "Engine:LS3D") — kept
           even when it doesn't map to a RenoDX-supported engine, so the UI can still show it. */
        public string EngineRaw { get; set; }
        public RenoDxEngine? MappedEngine { get; set; }
        public List<string> Direct3DVersionsRaw { get; set; }
        /* Only set when every listed Direct3D version (across all patches/re-releases) falls
           within DXVK+Lilium's supported range — see InferConfidentGraphicsApi. A game that ever
           shipped an 11/12 path is left null: on an uninstalled game there's no way to tell which
           path its executable actually loads, and a false "yes" would be worse than no answer. */
        public GraphicsApi? ConfidentGraphicsApi { get; set; }

        /* Broader counterpart to ConfidentGraphicsApi for ReShade/RenoDX rather than DXVK —
           ReShade supports the full D3D8-D3D12 range (plus OpenGL), so unlike DXVK there's no
           version ceiling to reject above. See InferConfidentGraphicsApiForReShade. */
        public GraphicsApi? ConfidentGraphicsApiForReShade { get; set; }

        /* Distinct from ConfidentGraphicsApi being null: that null collapses two different
           situations — "can't tell at all" and "definitely above DXVK's range, just not sure
           which of 11/12 it is" — into the same "not confident" bucket, which is why a game
           like Control (PCGamingWiki lists "11, 12") sits there showing a disabled/greyed
           install button instead of hiding the whole section. This is true only for the second
           case, where the section can hide outright since no answer within 8/9/10 is possible
           no matter what. See InferConfidentlyAboveDxvkRange. */
        public bool ConfidentlyAboveDxvkRange { get; set; }
    }

    /* Fallback engine/graphics-API lookup via PCGamingWiki's public Cargo query API, used only
       when local detection (RenoDxEngineDetector's folder scan, PeInspector's PE-header import
       scan) can't tell — typically because the game isn't installed yet. Not addon-dependent:
       the actual Playnite "PCGamingWiki Metadata Provider" addon doesn't import this data (its
       source only maps controller/multiplayer/VR features), but PCGamingWiki's own `API` and
       `Infobox_game` Cargo tables carry it directly and are free to query without any addon.
       Cached to disk per normalized game name — the same one-file-per-store convention as
       JsonRecordStore's install records, just keyed by name instead of Playnite game Id since
       there's no install record to key off yet. */
    public class PcGamingWikiService
    {
        private const string ApiUrl = "https://www.pcgamingwiki.com/w/api.php";
        private static readonly HttpClient Http = CreateHttpClient();
        private static readonly Regex LeadingVersionNumber = new Regex(@"^(\d+)");

        private readonly JsonRecordStore<PcGamingWikiLookupResult> cache;

        public PcGamingWikiService(string cacheDirectory)
        {
            cache = new JsonRecordStore<PcGamingWikiLookupResult>(cacheDirectory, "pcgamingwiki_lookup_cache.json");
        }

        public async Task<PcGamingWikiLookupResult> LookupAsync(string gameName)
        {
            var cacheKey = GameNameMatcher.NormalizeName(gameName);
            var cached = cache.Get(cacheKey);
            if (IsUsableCachedResult(cached)) return cached;

            var result = new PcGamingWikiLookupResult();
            try
            {
                var pageTitle = await ResolvePageTitleAsync(gameName).ConfigureAwait(false);
                if (pageTitle != null)
                {
                    result.PageTitle = pageTitle;
                    await FillFromWikiAsync(result, pageTitle).ConfigureAwait(false);
                }

                cache.Set(cacheKey, result);
            }
            catch (Exception ex)
            {
                /* Deliberately not cached — an empty result written here would be served forever,
                   hiding data the wiki has (or will have) from every later lookup. */
                Log.Warn(ex, $"PCGamingWiki lookup failed for '{gameName}'.");
            }

            return result;
        }

        /* A page that resolved but carries neither engine nor API data is the shape an earlier
           build cached when Cargo rejected its queries (see FillFromWikiAsync) — re-query rather
           than serve it. A game with no wiki page at all stays cached as a legitimate miss. */
        public static bool IsUsableCachedResult(PcGamingWikiLookupResult cached)
        {
            if (cached == null) return false;
            if (cached.PageTitle == null) return true;

            return !string.IsNullOrEmpty(cached.EngineRaw)
                || (cached.Direct3DVersionsRaw != null && cached.Direct3DVersionsRaw.Count > 0);
        }

        /* Cargo is the structured source, but PCGamingWiki started rejecting its queries with
           "permissiondenied" (confirmed live 2026-10-31) — action=parse of the page's own
           wikitext still works, so it's the fallback when Cargo errors out. */
        private static async Task FillFromWikiAsync(PcGamingWikiLookupResult result, string pageTitle)
        {
            string engineRaw;
            try
            {
                result.Direct3DVersionsRaw = await QueryDirect3DVersionsAsync(pageTitle).ConfigureAwait(false);
                engineRaw = CleanEngineName(await QueryEnginesAsync(pageTitle).ConfigureAwait(false));
            }
            catch (InvalidOperationException ex)
            {
                Log.Warn(ex, $"PCGamingWiki Cargo query rejected for '{pageTitle}' — falling back to page wikitext.");
                var wikitext = await QueryWikitextAsync(pageTitle).ConfigureAwait(false);
                result.Direct3DVersionsRaw = ParseWikitextDirect3DVersions(wikitext);
                engineRaw = ParseWikitextEngine(wikitext);
            }

            result.ConfidentGraphicsApi = InferConfidentGraphicsApi(result.Direct3DVersionsRaw);
            result.ConfidentGraphicsApiForReShade = InferConfidentGraphicsApiForReShade(result.Direct3DVersionsRaw);
            result.ConfidentlyAboveDxvkRange = InferConfidentlyAboveDxvkRange(result.Direct3DVersionsRaw);
            result.EngineRaw = engineRaw;
            result.MappedEngine = MapEngine(engineRaw);
        }

        private static async Task<string> QueryWikitextAsync(string pageTitle)
        {
            var url = $"{ApiUrl}?action=parse&page={Uri.EscapeDataString(pageTitle)}&prop=wikitext&redirects=1&format=json&formatversion=2";
            var json = JObject.Parse(await Http.GetStringAsync(url).ConfigureAwait(false));
            if (json["error"] != null)
                throw new InvalidOperationException($"PCGamingWiki parse failed: {json["error"]["info"]}");
            return (string)json["parse"]?["wikitext"];
        }

        /* Retries with a known edition suffix stripped (e.g. "Control Ultimate Edition" ->
           "Control") when the plain name comes back empty — opensearch does a prefix match, and
           a store/Playnite edition suffix is rarely part of the wiki's actual page title.
           Confirmed live 2026-08-08: searching "Control Ultimate Edition" returns zero results,
           "Control" resolves immediately. */
        private static async Task<string> ResolvePageTitleAsync(string gameName)
        {
            var direct = await QueryOpenSearchAsync(gameName).ConfigureAwait(false);
            if (direct != null) return direct;

            var stripped = GameNameMatcher.StripKnownEditionSuffix(gameName);
            return stripped != gameName ? await QueryOpenSearchAsync(stripped).ConfigureAwait(false) : null;
        }

        private static async Task<string> QueryOpenSearchAsync(string searchTerm)
        {
            var url = $"{ApiUrl}?action=opensearch&search={Uri.EscapeDataString(searchTerm)}&limit=1&namespace=0&format=json";
            var json = await Http.GetStringAsync(url).ConfigureAwait(false);
            var titles = JArray.Parse(json).ElementAtOrDefault(1) as JArray;
            return titles != null && titles.Count > 0 ? (string)titles[0] : null;
        }

        private static async Task<List<string>> QueryDirect3DVersionsAsync(string pageTitle)
        {
            var url = $"{ApiUrl}?action=cargoquery&tables=API" +
                "&fields=API.Direct3D_versions=D3D" +
                $"&where=API._pageName=\"{EscapeCargoValue(pageTitle)}\"&format=json";
            var json = await Http.GetStringAsync(url).ConfigureAwait(false);
            var raw = (string)FirstRow(json)?["D3D"];
            return ParseCargoList(raw);
        }

        private static async Task<string> QueryEnginesAsync(string pageTitle)
        {
            var url = $"{ApiUrl}?action=cargoquery&tables=Infobox_game" +
                "&fields=Infobox_game.Engines=Engines" +
                $"&where=Infobox_game._pageName=\"{EscapeCargoValue(pageTitle)}\"&format=json";
            var json = await Http.GetStringAsync(url).ConfigureAwait(false);
            return (string)FirstRow(json)?["Engines"];
        }

        private static JToken FirstRow(string cargoQueryJson) => ParseCargoFirstRow(cargoQueryJson);

        /* Throws on an API error body instead of reading it as "no rows" — an error response has
           no "cargoquery" key, which used to look identical to a page with no data. */
        public static JToken ParseCargoFirstRow(string cargoQueryJson)
        {
            var json = JObject.Parse(cargoQueryJson);
            if (json["error"] != null)
                throw new InvalidOperationException($"PCGamingWiki Cargo query failed: {json["error"]["info"]}");

            return (json["cargoquery"] as JArray)?.FirstOrDefault()?["title"];
        }

        private static readonly Regex WikitextEngineRow = new Regex(
            @"\{\{Infobox game/row/engine\|([^|}\r\n]+)([^\r\n]*)", RegexOptions.IgnoreCase);
        private static readonly Regex WikitextDirect3DVersions = new Regex(
            @"\|\s*direct3d versions\s*=([^\r\n]*)", RegexOptions.IgnoreCase);

        /* First listed engine that isn't marked |Former — e.g. Hollow Knight's page lists an
           old Unity 5 row ahead of the current one. Falls back to the first row regardless. */
        public static string ParseWikitextEngine(string wikitext)
        {
            if (string.IsNullOrEmpty(wikitext)) return null;

            string first = null;
            foreach (Match match in WikitextEngineRow.Matches(wikitext))
            {
                var name = match.Groups[1].Value.Trim();
                if (first == null) first = name;
                if (match.Groups[2].Value.IndexOf("|Former", StringComparison.OrdinalIgnoreCase) < 0) return name;
            }

            return first;
        }

        public static List<string> ParseWikitextDirect3DVersions(string wikitext)
        {
            var match = string.IsNullOrEmpty(wikitext) ? null : WikitextDirect3DVersions.Match(wikitext);
            return ParseCargoList(match != null && match.Success ? match.Groups[1].Value : null);
        }

        public static List<string> ParseCargoList(string raw) =>
            string.IsNullOrWhiteSpace(raw)
                ? new List<string>()
                : raw.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

        /* PCGamingWiki's "Engines" field is a wikitext-link string like "Engine:LS3D" or
           "Engine:Unreal Engine 4, Engine:Havok" — strip the namespace prefix, keep the first
           listed engine (good enough for the "generic Unreal/Unity install" gating this feeds).
           Public/static so tests can feed it raw Cargo field values without a network call. */
        public static string CleanEngineName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var first = raw.Split(',')[0].Trim();
            var colon = first.IndexOf(':');
            return colon >= 0 ? first.Substring(colon + 1).Trim() : first;
        }

        public static RenoDxEngine? MapEngine(string engineName)
        {
            if (string.IsNullOrEmpty(engineName)) return null;
            if (engineName.IndexOf("unreal", StringComparison.OrdinalIgnoreCase) >= 0) return RenoDxEngine.UnrealEngine;
            if (engineName.IndexOf("unity", StringComparison.OrdinalIgnoreCase) >= 0) return RenoDxEngine.UnityEngine;
            return null;
        }

        /* Every version PCGamingWiki lists for the game (across all releases/patches) must fall
           within 8/9/10 for this to return non-null — see ConfidentGraphicsApi's doc comment. */
        public static GraphicsApi? InferConfidentGraphicsApi(List<string> versions)
        {
            if (versions == null || versions.Count == 0) return null;

            GraphicsApi? best = null;
            foreach (var version in versions)
            {
                var match = LeadingVersionNumber.Match(version);
                if (!match.Success) return null;

                switch (match.Groups[1].Value)
                {
                    case "8": best = Max(best, GraphicsApi.D3D8); break;
                    case "9": best = Max(best, GraphicsApi.D3D9); break;
                    case "10": best = Max(best, GraphicsApi.D3D10); break;
                    default: return null; // 11, 12, or anything unrecognized — not confident.
                }
            }

            return best;
        }

        private static GraphicsApi Max(GraphicsApi? current, GraphicsApi candidate) =>
            current == null || candidate > current.Value ? candidate : current.Value;

        /* Every listed version has to be a recognized 11 or 12 for this to be true — a version
           that doesn't parse at all is left as "don't know" (false), same caution as
           InferConfidentGraphicsApi, rather than assumed to be above range. */
        public static bool InferConfidentlyAboveDxvkRange(List<string> versions)
        {
            if (versions == null || versions.Count == 0) return false;

            var sawHighApi = false;
            foreach (var version in versions)
            {
                var match = LeadingVersionNumber.Match(version);
                if (!match.Success) return false;

                switch (match.Groups[1].Value)
                {
                    case "8":
                    case "9":
                    case "10":
                        return false; // a DXVK-eligible path exists — not "above range".
                    case "11":
                    case "12":
                        sawHighApi = true;
                        break;
                    default:
                        return false; // unrecognized — don't know, not "confidently" anything.
                }
            }

            return sawHighApi;
        }

        /* Unlike InferConfidentGraphicsApi, ReShade supports the full D3D8-D3D12 range, so
           there's no version ceiling to reject above — confirmed live 2026-08-08 against Hollow
           Knight, whose PCGamingWiki page lists exactly one value ("11"), which
           InferConfidentGraphicsApi discarded as "not confident" purely because of DXVK's ≤10
           ceiling, even though a single listed value is unambiguous no matter what number it
           is. Only genuinely ambiguous when more than one DISTINCT version is listed (a game
           with multiple selectable render paths) — that still returns null, same caution as
           InferConfidentGraphicsApi, just not triggered by D3D11/12 alone anymore. */
        public static GraphicsApi? InferConfidentGraphicsApiForReShade(List<string> versions)
        {
            if (versions == null || versions.Count == 0) return null;

            GraphicsApi? result = null;
            foreach (var version in versions)
            {
                var match = LeadingVersionNumber.Match(version);
                if (!match.Success) return null;

                GraphicsApi api;
                switch (match.Groups[1].Value)
                {
                    case "8": api = GraphicsApi.D3D8; break;
                    case "9": api = GraphicsApi.D3D9; break;
                    case "10": api = GraphicsApi.D3D10; break;
                    case "11": api = GraphicsApi.D3D11; break;
                    case "12": api = GraphicsApi.D3D12; break;
                    default: return null;
                }

                if (result != null && result != api) return null;
                result = api;
            }

            return result;
        }

        private static string EscapeCargoValue(string value) => value.Replace("\"", "\\\"");

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent", "playnite-hdr-upscale-manager (PCGamingWiki Cargo API client)");
            return client;
        }
    }
}
