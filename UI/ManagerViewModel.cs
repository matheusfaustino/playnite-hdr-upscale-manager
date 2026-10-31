using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using HdrUpscaleManager.Models;
using HdrUpscaleManager.Models.Addons;
using HdrUpscaleManager.Services;
using HdrUpscaleManager.Services.Addons;
using HdrUpscaleManager.Services.ReShade;
using Playnite.SDK;
using Playnite.SDK.Models;

namespace HdrUpscaleManager.UI
{
    /* Shared by both the Desktop and Fullscreen views (ManagerView.xaml / ManagerFullscreenView.xaml)
       — one per-game dialog covering every addon this plugin manages, per PLAN.md §5's original
       design. Every action logs on failure (Log.Error/Warn) and also surfaces a message to the
       user — via inline StatusText for expected outcomes (not found, blacklisted, etc.), and an
       additional ShowErrorMessage popup for unexpected exceptions, so a failure is never silent. */
    public class ManagerViewModel : ObservableObject
    {
        private static readonly HashSet<GraphicsApi> DxvkSupportedApis =
            new HashSet<GraphicsApi> { GraphicsApi.D3D8, GraphicsApi.D3D9, GraphicsApi.D3D10 };

        private readonly IPlayniteAPI api;
        private readonly string userDataPath;

        /* Detected at window-open time (install folder doesn't change while the window is open)
           — drives the RenoDX per-engine and DXVK IsEnabled bindings below. Local filesystem/PE
           detection runs synchronously first (instant); if it can't tell — usually because the
           game isn't installed yet — a PCGamingWiki lookup fills the gap in the background, and
           these fields update in place once it returns (see RefreshDetectionFromPcGamingWikiAsync). */
        private RenoDxEngine? detectedRenoDxEngine;
        private GraphicsApi detectedDxvkApi;
        /* Same underlying detection as detectedDxvkApi, but for ReShade/RenoDX rather than
           DXVK — kept separate because DXVK's PCGamingWiki confidence check deliberately
           rejects D3D11/12 answers (DXVK only supports up to D3D10), which would wrongly read
           as "unknown" for ReShade, which supports the full D3D8-D3D12 range. See
           KnownGraphicsApiOrNull and PcGamingWikiService.InferConfidentGraphicsApiForReShade. */
        private GraphicsApi reShadeGraphicsApi;
        private string pcgwEngineRaw;
        private bool pcgwLookupInProgress;
        private Task pcgwLookupTask;
        /* Set from PcGamingWikiService.InferConfidentlyAboveDxvkRange — true only when every
           listed Direct3D version is 11/12 (just not sure which), as opposed to detectedDxvkApi
           staying Unknown for "can't tell at all". See ShowDxvkSection. */
        private bool wikiConfidentlyAboveDxvkRange;
        private readonly RemoteManifest cachedManifest;
        private readonly LumaPrerequisitesManifest lumaPrerequisitesManifest;
        private OptiScalerWikiData optiScalerWikiData;
        private bool optiScalerWikiLookupInProgress;

        public Game Game { get; }
        public string GameName => Game.Name;

        private GamepadIconStyle gamepadIconStyle = GamepadIconStyle.Xbox;
        private string gamepadIconStyleSourceName;
        public bool IsXboxGamepadIcons => gamepadIconStyle == GamepadIconStyle.Xbox;
        public bool IsPlayStationGamepadIcons => gamepadIconStyle == GamepadIconStyle.PlayStation;

        public bool CanInstallUnrealRenoDx => detectedRenoDxEngine == null || detectedRenoDxEngine == RenoDxEngine.UnrealEngine;
        public bool CanInstallUnityRenoDx => detectedRenoDxEngine == null || detectedRenoDxEngine == RenoDxEngine.UnityEngine;
        public string RenoDxEngineDetectionText => detectedRenoDxEngine switch
        {
            RenoDxEngine.UnrealEngine => "Detected engine: Unreal Engine.",
            RenoDxEngine.UnityEngine => "Detected engine: Unity.",
            _ when pcgwLookupInProgress => "Engine not auto-detected locally — checking PCGamingWiki…",
            _ when !string.IsNullOrEmpty(pcgwEngineRaw) =>
                $"Engine not auto-detected locally — PCGamingWiki lists \"{pcgwEngineRaw}\" (not a RenoDX-supported engine); both options remain available.",
            _ => "Engine not auto-detected — both Unreal and Unity options are available."
        };

        public bool CanInstallDxvk => DxvkSupportedApis.Contains(detectedDxvkApi);
        /* CanInstallDxvk can't drive the button's Visibility directly: it's false both when
           the API is confidently unsupported AND when it's simply not known yet, but only the
           first case should actually hide the button — an Unknown API might still resolve via
           the PCGamingWiki fallback, and hiding the button on every "not detected yet" instant
           would flicker it in and out as detection completes. */
        public bool ShowDxvkInstallButton => detectedDxvkApi == GraphicsApi.Unknown || CanInstallDxvk;
        /* Fullscreen's single Installed toggle (see ManagerFullscreenView.xaml) has no separate
           Install/Uninstall targets to gate independently — it must stay interactive to turn
           OFF an existing install even when ShowDxvkInstallButton would otherwise hide/disable
           the Desktop view's Install button for an API DXVK doesn't support. */
        public bool CanToggleDxvk => IsDxvkInstalled || ShowDxvkInstallButton;
        /* Gates the whole DXVK+Lilium HDR section, not just its Install button — true whenever
           we're actually SURE the game's API is above DXVK's D3D8/9/10 range, either because
           local PE inspection found a definite D3D11/12/OpenGL/Vulkan import directly (a known,
           non-Unknown value that still fails CanInstallDxvk), or because PCGamingWiki confirms
           every listed version is 11/12 (Control's case: its own exe delegates rendering to an
           internal engine DLL, so PE inspection alone can't tell, but the wiki lists "11, 12" —
           ambiguous about which, but not about being too high either way). Unlike
           ShowDxvkInstallButton, there's no "flicker while still checking" concern here: hiding
           only ever happens once a check has actually concluded "definitely no", never while a
           lookup is still in progress. */
        public bool ShowDxvkSection =>
            !((detectedDxvkApi != GraphicsApi.Unknown && !CanInstallDxvk) || wikiConfidentlyAboveDxvkRange);
        public string DxvkApiDetectionText => detectedDxvkApi switch
        {
            GraphicsApi.D3D8 => "Detected graphics API: DirectX 8.",
            GraphicsApi.D3D9 => "Detected graphics API: DirectX 9.",
            GraphicsApi.D3D10 => "Detected graphics API: DirectX 10.",
            GraphicsApi.Unknown when pcgwLookupInProgress => "Graphics API not detected locally — checking PCGamingWiki…",
            GraphicsApi.Unknown => "Graphics API not detected (game not installed, or its executable couldn't be read, and PCGamingWiki had no confident answer) — install disabled.",
            _ => $"Detected graphics API: {detectedDxvkApi} — DXVK+Lilium HDR isn't offered for this API."
        };

        private string statusText = "Ready.";
        public string StatusText { get => statusText; private set => SetProperty(ref statusText, value); }

        private bool isBusy;
        public bool IsBusy { get => isBusy; private set => SetProperty(ref isBusy, value); }

        /* No installed-record backs ReShade (unlike RenoDX/Luma/DXVK/OptiScaler below) — this
           re-detects the deployed DLL live, same as ReShadeInstallService.IsInstalled itself. */
        public string ReShadeStatus =>
            ReShadeInstallService.IsInstalled(Game.InstallDirectory, KnownGraphicsApiOrNull) ? "Installed." : "Not installed.";
        public bool IsReShadeInstalled =>
            ReShadeInstallService.IsInstalled(Game.InstallDirectory, KnownGraphicsApiOrNull);

        private string renoDxStatus;
        public string RenoDxStatus { get => renoDxStatus; private set => SetProperty(ref renoDxStatus, value); }

        private bool isRenoDxInstalled;
        public bool IsRenoDxInstalled { get => isRenoDxInstalled; private set => SetProperty(ref isRenoDxInstalled, value); }

        private string renoDxExternalUrl;
        public string RenoDxExternalUrl { get => renoDxExternalUrl; private set => SetProperty(ref renoDxExternalUrl, value); }

        private string lumaStatus;
        public string LumaStatus { get => lumaStatus; private set => SetProperty(ref lumaStatus, value); }

        private bool isLumaInstalled;
        public bool IsLumaInstalled { get => isLumaInstalled; private set => SetProperty(ref isLumaInstalled, value); }

        /* dgVoodoo2 has no manual install/uninstall surface of its own — it's purely an
           automatic prerequisite step InstallLumaAsync runs first for whichever games the Luma
           prerequisites manifest lists (Vanquish today), the same way ReShade auto-installs
           ahead of RenoDX (EnsureReShadeInstalledForRenoDxAsync). Still tracked via
           Dgvoodoo2Records so a second Luma install/reinstall doesn't redundantly reinstall it. */
        private List<LumaPrerequisiteStep> Dgvoodoo2Steps =>
            lumaPrerequisitesManifest?.GetStepsFor(GameName)?.FindAll(s => s.Type == "dgvoodoo2") ?? new List<LumaPrerequisiteStep>();

        private string dxvkStatus;
        public string DxvkStatus { get => dxvkStatus; private set => SetProperty(ref dxvkStatus, value); }

        private bool isDxvkInstalled;
        public bool IsDxvkInstalled { get => isDxvkInstalled; private set => SetProperty(ref isDxvkInstalled, value); }

        private string optiScalerStatus;
        public string OptiScalerStatus { get => optiScalerStatus; private set => SetProperty(ref optiScalerStatus, value); }

        private bool isOptiScalerInstalled;
        public bool IsOptiScalerInstalled { get => isOptiScalerInstalled; private set => SetProperty(ref isOptiScalerInstalled, value); }

        private bool hdrToggleEnabled;
        public bool HdrToggleEnabled
        {
            get => hdrToggleEnabled;
            set
            {
                if (!SetProperty(ref hdrToggleEnabled, value)) return;
                try
                {
                    var store = new JsonRecordStore<bool>(userDataPath, "hdr_toggle_opt_in.json");
                    if (value) store.Set(Game.Id.ToString(), true); else store.Remove(Game.Id.ToString());
                    StatusText = value
                        ? "HDR will be toggled on when this game starts and restored when it exits."
                        : "HDR toggle disabled for this game.";
                }
                catch (Exception ex)
                {
                    Log.Error(ex, $"Failed to update HDR toggle opt-in for '{GameName}'.");
                    StatusText = $"Failed to update HDR toggle setting: {ex.Message}";
                    api.Dialogs.ShowErrorMessage(StatusText, "HDR Toggle");
                }
            }
        }

        public ICommand ReShadeInstallCommand { get; }
        public ICommand ReShadeUninstallCommand { get; }

        public ICommand RenoDxInstallCommand { get; }
        public ICommand RenoDxInstallUnrealGenericCommand { get; }
        public ICommand RenoDxInstallUnrealUeExtendedCommand { get; }
        public ICommand RenoDxInstallUnityCommand { get; }
        public ICommand RenoDxUpdateCommand { get; }
        public ICommand RenoDxUninstallCommand { get; }
        public ICommand OpenRenoDxExternalUrlCommand { get; }

        public ICommand LumaInstallCommand { get; }
        public ICommand LumaUpdateCommand { get; }
        public ICommand LumaUninstallCommand { get; }

        public ICommand DxvkInstallCommand { get; }
        public ICommand DxvkUpdateCommand { get; }
        public ICommand DxvkUninstallCommand { get; }

        public ICommand OptiScalerInstallDlssCommand { get; }
        public ICommand OptiScalerInstallNoDlssCommand { get; }
        public ICommand OptiScalerUpdateCommand { get; }
        public ICommand OptiScalerUninstallCommand { get; }

        public ManagerViewModel(IPlayniteAPI api, string userDataPath, Game game)
        {
            this.api = api;
            this.userDataPath = userDataPath;
            Game = game;

            cachedManifest = new ManifestService(userDataPath).TryGetCachedManifest();
            lumaPrerequisitesManifest = new LumaPrerequisitesManifestService(userDataPath).TryGetCachedManifest();
            detectedRenoDxEngine = RenoDxEngineDetector.Detect(
                Game.InstallDirectory, GameName, cachedManifest?.InstallPathOverrides, cachedManifest?.EngineHintOverrides).Engine;

            string graphicsApiOverrideRaw = null;
            cachedManifest?.GraphicsApiOverrides?.TryGetValue(GameName, out graphicsApiOverrideRaw);
            var graphicsApiOverride = PeInspector.ParseApiOverride(graphicsApiOverrideRaw);

            if (graphicsApiOverride != null)
            {
                detectedDxvkApi = graphicsApiOverride.Value;
            }
            else
            {
                var exePath = PeInspector.FindGameExecutable(Game.InstallDirectory);
                detectedDxvkApi = exePath != null ? (PeInspector.Inspect(exePath)?.Api ?? GraphicsApi.Unknown) : GraphicsApi.Unknown;
            }

            /* Starts equal to detectedDxvkApi — manifest override and PE inspection have no
               DXVK-specific ceiling, so both are equally valid for ReShade at this point. Only
               diverges once RefreshDetectionFromPcGamingWikiAsync applies each field's own
               correctly-scoped confidence function to the same underlying lookup. */
            reShadeGraphicsApi = detectedDxvkApi;

            ReShadeInstallCommand = new RelayCommand(() => Run(InstallReShadeAsync), () => !IsBusy);
            ReShadeUninstallCommand = new RelayCommand(
                () => Run(UninstallReShadeAsync),
                () => !IsBusy && ReShadeInstallService.IsInstalled(Game.InstallDirectory, KnownGraphicsApiOrNull));

            RenoDxInstallCommand = new RelayCommand(() => Run(InstallRenoDxAsync), () => !IsBusy);
            RenoDxInstallUnrealGenericCommand = new RelayCommand(
                () => Run(() => InstallRenoDxEngineAsync(RenoDxEngine.UnrealEngine, forceUeExtended: null)),
                () => !IsBusy && CanInstallUnrealRenoDx);
            RenoDxInstallUnrealUeExtendedCommand = new RelayCommand(
                () => Run(() => InstallRenoDxEngineAsync(RenoDxEngine.UnrealEngine, forceUeExtended: true)),
                () => !IsBusy && CanInstallUnrealRenoDx);
            RenoDxInstallUnityCommand = new RelayCommand(
                () => Run(() => InstallRenoDxEngineAsync(RenoDxEngine.UnityEngine, forceUeExtended: false)),
                () => !IsBusy && CanInstallUnityRenoDx);
            RenoDxUpdateCommand = new RelayCommand(() => Run(CheckRenoDxUpdateAsync), () => !IsBusy && RenoDxRecords.Get(GameId) != null);
            RenoDxUninstallCommand = new RelayCommand(() => Run(UninstallRenoDxAsync), () => !IsBusy && RenoDxRecords.Get(GameId) != null);
            OpenRenoDxExternalUrlCommand = new RelayCommand(OpenRenoDxExternalUrl, () => RenoDxExternalUrl != null);

            LumaInstallCommand = new RelayCommand(() => Run(InstallLumaAsync), () => !IsBusy);
            LumaUpdateCommand = new RelayCommand(() => Run(CheckLumaUpdateAsync), () => !IsBusy && LumaRecords.Get(GameId) != null);
            LumaUninstallCommand = new RelayCommand(() => Run(UninstallLumaAsync), () => !IsBusy && LumaRecords.Get(GameId) != null);

            DxvkInstallCommand = new RelayCommand(() => Run(InstallDxvkAsync), () => !IsBusy && CanInstallDxvk);
            DxvkUpdateCommand = new RelayCommand(() => Run(CheckDxvkUpdateAsync), () => !IsBusy && DxvkRecords.Get(GameId) != null);
            DxvkUninstallCommand = new RelayCommand(() => Run(UninstallDxvkAsync), () => !IsBusy && DxvkRecords.Get(GameId) != null);

            OptiScalerInstallDlssCommand = new RelayCommand(() => Run(() => InstallOptiScalerAsync(dlssInputs: true)), () => !IsBusy);
            OptiScalerInstallNoDlssCommand = new RelayCommand(() => Run(() => InstallOptiScalerAsync(dlssInputs: false)), () => !IsBusy);
            OptiScalerUpdateCommand = new RelayCommand(() => Run(CheckOptiScalerUpdateAsync), () => !IsBusy && OptiScalerRecords.Get(GameId) != null);
            OptiScalerUninstallCommand = new RelayCommand(() => Run(UninstallOptiScalerAsync), () => !IsBusy && OptiScalerRecords.Get(GameId) != null);

            RefreshStatuses();

            // NOT called here: RefreshGamepadIconStyle() calls api.GetConnectedControllers(),
            // which throws NullReferenceException from inside Playnite's own Desktop-mode
            // implementation (confirmed live 2026-08-10 — Playnite.API.PlayniteApiRoot's
            // controller subsystem isn't initialized outside Fullscreen). ManagerView (Desktop)
            // never binds to IsXboxGamepadIcons/IsPlayStationGamepadIcons anyway, so
            // HdrUpscaleManagerPlugin.OpenManagerWindow calls this only when opening Fullscreen.

            if (detectedRenoDxEngine == null || detectedDxvkApi == GraphicsApi.Unknown)
            {
                pcgwLookupInProgress = true;
                pcgwLookupTask = RefreshDetectionFromPcGamingWikiAsync();
            }

            optiScalerWikiData = new OptiScalerWikiService(Path.Combine(userDataPath, "OptiScalerWikiCache")).TryGetCachedData();
            optiScalerWikiLookupInProgress = true;
            _ = RefreshOptiScalerWikiAsync();
        }

        /* Fire-and-forget from the constructor, same shape as RefreshDetectionFromPcGamingWikiAsync
           — the cached answer (if any) already showed instantly via the sync TryGetCachedData
           call above; this only refreshes it. Purely informational (see OptiScalerWikiCompatText),
           so nothing else needs to await this the way EnsurePcGamingWikiLookupCompletedAsync's
           callers do for the PCGamingWiki lookup. */
        private async Task RefreshOptiScalerWikiAsync()
        {
            try
            {
                var cacheDir = Path.Combine(userDataPath, "OptiScalerWikiCache");
                optiScalerWikiData = await new OptiScalerWikiService(cacheDir).FetchAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Log.Warn(ex, $"OptiScaler wiki fetch failed for '{GameName}'.");
            }
            finally
            {
                optiScalerWikiLookupInProgress = false;
                OnPropertyChanged(nameof(OptiScalerWikiCompatText));
            }
        }

        public string OptiScalerWikiCompatText
        {
            get
            {
                if (optiScalerWikiData == null)
                {
                    return optiScalerWikiLookupInProgress
                        ? "Checking the OptiScaler compatibility wiki…"
                        : "Could not check the OptiScaler compatibility wiki (no network and nothing cached yet).";
                }

                var wikiName = OptiScalerWikiService.ResolveWikiName(GameName, cachedManifest);
                var entry = FindOptiScalerWikiEntry(wikiName);

                return entry != null && entry.Upscalers.Count > 0
                    ? $"OptiScaler wiki lists this game's native upscaler(s) as: {string.Join(", ", entry.Upscalers)}."
                    : "Not listed on the OptiScaler compatibility wiki — check the game's own graphics settings instead.";
            }
        }

        private OptiScalerCompatEntry FindOptiScalerWikiEntry(string wikiName)
        {
            if (optiScalerWikiData.StandardCompat.TryGetValue(wikiName, out var stdEntry)) return stdEntry;
            if (optiScalerWikiData.Fsr4Compat.TryGetValue(wikiName, out var fsr4Entry)) return fsr4Entry;
            return null;
        }

        /* Fire-and-forget from the constructor — local detection already ran synchronously above,
           this only fills in whichever of the two came back inconclusive. Every bound property
           that depends on the two detected-* fields gets an explicit OnPropertyChanged, and
           CommandManager is nudged directly since RelayCommand's CanExecute re-evaluation is tied
           to CommandManager.RequerySuggested (fires on focus/keyboard/mouse), which nothing raises
           on its own when a background task finishes with no user interaction. */
        private async Task RefreshDetectionFromPcGamingWikiAsync()
        {
            try
            {
                var cacheDir = Path.Combine(userDataPath, "PcGamingWikiCache");
                var lookup = await new PcGamingWikiService(cacheDir).LookupAsync(GameName).ConfigureAwait(true);

                if (detectedRenoDxEngine == null && lookup.MappedEngine != null)
                    detectedRenoDxEngine = lookup.MappedEngine;
                pcgwEngineRaw = lookup.EngineRaw;

                if (detectedDxvkApi == GraphicsApi.Unknown && lookup.ConfidentGraphicsApi != null)
                    detectedDxvkApi = lookup.ConfidentGraphicsApi.Value;
                if (detectedDxvkApi == GraphicsApi.Unknown && lookup.ConfidentlyAboveDxvkRange)
                    wikiConfidentlyAboveDxvkRange = true;

                if (reShadeGraphicsApi == GraphicsApi.Unknown && lookup.ConfidentGraphicsApiForReShade != null)
                    reShadeGraphicsApi = lookup.ConfidentGraphicsApiForReShade.Value;
            }
            catch (Exception ex)
            {
                Log.Warn(ex, $"PCGamingWiki fallback detection failed for '{GameName}'.");
            }
            finally
            {
                pcgwLookupInProgress = false;
                OnPropertyChanged(nameof(ReShadeStatus));
                OnPropertyChanged(nameof(IsReShadeInstalled));
                OnPropertyChanged(nameof(RenoDxEngineDetectionText));
                OnPropertyChanged(nameof(CanInstallUnrealRenoDx));
                OnPropertyChanged(nameof(CanInstallUnityRenoDx));
                OnPropertyChanged(nameof(DxvkApiDetectionText));
                OnPropertyChanged(nameof(CanInstallDxvk));
                OnPropertyChanged(nameof(ShowDxvkInstallButton));
                OnPropertyChanged(nameof(ShowDxvkSection));
                OnPropertyChanged(nameof(CanToggleDxvk));
                CommandManager.InvalidateRequerySuggested();
            }
        }

        private string GameId => Game.Id.ToString();

        private JsonRecordStore<RenoDxInstalledRecord> RenoDxRecords => new JsonRecordStore<RenoDxInstalledRecord>(userDataPath, "renodx_installs.json");
        private JsonRecordStore<LumaInstalledRecord> LumaRecords => new JsonRecordStore<LumaInstalledRecord>(userDataPath, "luma_installs.json");
        private JsonRecordStore<Dgvoodoo2InstalledRecord> Dgvoodoo2Records => new JsonRecordStore<Dgvoodoo2InstalledRecord>(userDataPath, "dgvoodoo2_installs.json");
        private JsonRecordStore<DxvkInstalledRecord> DxvkRecords => new JsonRecordStore<DxvkInstalledRecord>(userDataPath, "dxvk_installs.json");
        private JsonRecordStore<OptiScalerInstalledRecord> OptiScalerRecords => new JsonRecordStore<OptiScalerInstalledRecord>(userDataPath, "optiscaler_installs.json");

        private void RefreshStatuses()
        {
            OnPropertyChanged(nameof(ReShadeStatus));
            OnPropertyChanged(nameof(IsReShadeInstalled));

            var renoDx = RenoDxRecords.Get(GameId);
            RenoDxStatus = renoDx != null ? $"Installed ({renoDx.MatchedWikiName ?? renoDx.AddonFileName})" : "Not installed.";
            IsRenoDxInstalled = renoDx != null;

            var luma = LumaRecords.Get(GameId);
            var dgvoodoo2 = Dgvoodoo2Records.Get(GameId);
            LumaStatus = luma != null
                ? $"Installed (build {luma.InstalledBuildNumber})" + (dgvoodoo2 != null ? $" + dgVoodoo2 {dgvoodoo2.Version}" : "")
                : "Not installed.";
            IsLumaInstalled = luma != null;

            var dxvk = DxvkRecords.Get(GameId);
            DxvkStatus = dxvk != null ? $"Installed ({dxvk.Version}, {string.Join("+", dxvk.InstalledDlls)})" : "Not installed.";
            IsDxvkInstalled = dxvk != null;
            OnPropertyChanged(nameof(CanToggleDxvk));

            var optiScaler = OptiScalerRecords.Get(GameId);
            OptiScalerStatus = optiScaler != null
                ? $"Installed ({optiScaler.Version}, {(optiScaler.DlssInputs ? "DLSS-only mode" : "native upscaler mode")})"
                : "Not installed.";
            IsOptiScalerInstalled = optiScaler != null;

            hdrToggleEnabled = new JsonRecordStore<bool>(userDataPath, "hdr_toggle_opt_in.json").Get(GameId);
            OnPropertyChanged(nameof(HdrToggleEnabled));
        }

        /* Called only at construction — re-derives from the live connected-controller list,
           since there's no specific "just connected" controller to read a name from yet. Live
           updates while the window is open go through SetGamepadIconStyle/HandleControllerDisconnected
           below instead of calling this again. */
        public void RefreshGamepadIconStyle()
        {
            var controllerName = api.GetConnectedControllers()?.FirstOrDefault(c => c.Enabled)?.Name;
            SetGamepadIconStyle(controllerName);
        }

        /* Called from HdrUpscaleManagerPlugin.OnControllerConnected with the newly-connected
           controller's own name — not a fresh GetConnectedControllers() query, which confirmed
           live 2026-08-10 can still return a just-disconnected controller for a moment (see
           HandleControllerDisconnected below for the matching problem on the other side). */
        public void SetGamepadIconStyle(string controllerName)
        {
            gamepadIconStyleSourceName = controllerName;
            gamepadIconStyle = GamepadIconStyleDetector.Detect(controllerName);
            OnPropertyChanged(nameof(IsXboxGamepadIcons));
            OnPropertyChanged(nameof(IsPlayStationGamepadIcons));
        }

        /* Called from HdrUpscaleManagerPlugin.OnControllerDisconnected. Confirmed live 2026-08-10
           swapping a controller fires OnControllerConnected for the new one immediately followed
           by OnControllerDisconnected for the old one (2ms apart) — a disconnect handler that
           unconditionally re-queries GetConnectedControllers() can catch it in a stale state and
           clobber the icon style the connect event JUST set correctly. Only re-deriving when the
           controller that disconnected is the one actually driving the current icon avoids that:
           an unrelated controller disconnecting is a no-op, and the real "last controller unplugged"
           case (disconnected name matches) still correctly falls through to Xbox's default. */
        public void HandleControllerDisconnected(string disconnectedControllerName)
        {
            if (!string.Equals(disconnectedControllerName, gamepadIconStyleSourceName, StringComparison.Ordinal)) return;

            RefreshGamepadIconStyle();
        }

        private async void Run(Func<Task> action)
        {
            IsBusy = true;
            try
            {
                await action().ConfigureAwait(true);
            }
            finally
            {
                IsBusy = false;
                RefreshStatuses();
            }
        }

        private void OpenRenoDxExternalUrl()
        {
            if (RenoDxExternalUrl == null) return;
            try
            {
                Process.Start(new ProcessStartInfo(RenoDxExternalUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"Failed to open RenoDX external link for '{GameName}'.");
                StatusText = $"Failed to open link: {ex.Message}";
                api.Dialogs.ShowErrorMessage(StatusText, "Install RenoDX");
            }
        }

        // ── ReShade ──────────────────────────────────────────────────────────────

        /* GraphicsApi.Unknown means "no known API," not a real value to force — treating it as
           an override would skip ReShadeInstallService's own PE-inspection fallback for no
           reason. detectedDxvkApi already folds in the manifest's GraphicsApiOverrides (see the
           constructor) ahead of PE inspection, so reusing it here covers ReShade the same way
           without a second, separate lookup. */
        private GraphicsApi? KnownGraphicsApiOrNull => reShadeGraphicsApi == GraphicsApi.Unknown ? (GraphicsApi?)null : reShadeGraphicsApi;

        /* Same idea as KnownGraphicsApiOrNull, scoped to detectedDxvkApi instead: DxvkService
           re-inspects the game's PE header itself and has no way to see what CanInstallDxvk
           already resolved (manifest override, or a PCGamingWiki fallback confident within
           DXVK's own D3D8-D3D10 ceiling) — confirmed live for Mafia (2002), whose Game.exe import
           layout reads as GraphicsApi.Unknown on its own, same class of gap as The Witcher 3. */
        private GraphicsApi? KnownDxvkApiOrNull => detectedDxvkApi == GraphicsApi.Unknown ? (GraphicsApi?)null : detectedDxvkApi;

        /* The constructor kicks off RefreshDetectionFromPcGamingWikiAsync fire-and-forget when
           local detection (manifest override, then PE inspection) came back inconclusive — fine
           for the window just rendering "checking…" text, but a RenoDX install triggered right
           after the window opens can easily race ahead of that lookup finishing (confirmed live
           2026-08-08 against Hollow Knight — a Unity game, whose player binary loads its
           graphics DLL dynamically at runtime rather than importing it statically, so PE
           inspection can never see it no matter how correct the parser is; only the PCGamingWiki
           tier can resolve it). Awaiting the same in-flight task here — not starting a second,
           redundant lookup — means RenoDX's ReShade-first check always sees the best answer
           this addon is capable of producing, not whatever was known at construction time. */
        private async Task EnsurePcGamingWikiLookupCompletedAsync()
        {
            if (pcgwLookupTask != null) await pcgwLookupTask.ConfigureAwait(true);
        }

        private async Task InstallReShadeAsync()
        {
            StatusText = "Installing ReShade…";
            try
            {
                // Local PE inspection alone reads GraphicsApi.Unknown for a game whose player
                // binary loads its graphics DLL dynamically rather than importing it statically
                // (e.g. Unity — see KnownGraphicsApiOrNull's own doc comment) — awaiting the
                // in-flight PCGamingWiki lookup here, same as EnsureReShadeInstalledForRenoDxAsync
                // already does, avoids racing a click against that fallback still resolving.
                if (KnownGraphicsApiOrNull == null) await EnsurePcGamingWikiLookupCompletedAsync().ConfigureAwait(true);

                var cacheDir = Path.Combine(userDataPath, "ReShadeCache");
                var result = await new ReShadeInstallService(cacheDir).InstallAsync(Game.InstallDirectory, KnownGraphicsApiOrNull).ConfigureAwait(true);
                if (!result.Success)
                {
                    Log.Error($"ReShade install failed for '{GameName}': {result.Error}");
                    StatusText = result.Error;
                    return;
                }

                StatusText = $"Installed ReShade {result.Version} ({result.Api}).";
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"ReShade install failed for '{GameName}'.");
                StatusText = $"ReShade install failed: {ex.Message}";
                api.Dialogs.ShowErrorMessage(StatusText, "Install ReShade");
            }
        }

        private async Task UninstallReShadeAsync()
        {
            StatusText = "Uninstalling ReShade…";
            try
            {
                if (KnownGraphicsApiOrNull == null) await EnsurePcGamingWikiLookupCompletedAsync().ConfigureAwait(true);

                var result = ReShadeInstallService.Uninstall(Game.InstallDirectory, KnownGraphicsApiOrNull);
                if (!result.Success)
                {
                    Log.Error($"ReShade uninstall failed for '{GameName}': {result.Error}");
                    StatusText = result.Error;
                    return;
                }

                StatusText = "Uninstalled ReShade.";
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"ReShade uninstall failed for '{GameName}'.");
                StatusText = $"ReShade uninstall failed: {ex.Message}";
                api.Dialogs.ShowErrorMessage(StatusText, "Uninstall ReShade");
            }
        }

        /* RenoDX ships as a ReShade addon (.addon64/.addon32) — AddonInstallService drops it
           "next to the ReShade DLL" and relies on ReShade to actually load it, so a RenoDX
           install does nothing if ReShade isn't already present. Installs ReShade first when
           missing, silent on success (just logged) so a single "Install" click doesn't pop two
           dialogs; surfaces an error and aborts the RenoDX install if this step fails. Luma
           isn't gated the same way — every Luma mod inspected so far (see PLAN.md's backlog)
           ships its own self-hosted dxgi.dll/.addon rather than depending on an existing
           ReShade install. */
        private async Task<bool> EnsureReShadeInstalledForRenoDxAsync()
        {
            if (KnownGraphicsApiOrNull == null) await EnsurePcGamingWikiLookupCompletedAsync().ConfigureAwait(true);

            if (ReShadeInstallService.IsInstalled(Game.InstallDirectory, KnownGraphicsApiOrNull)) return true;

            try
            {
                var cacheDir = Path.Combine(userDataPath, "ReShadeCache");
                var result = await new ReShadeInstallService(cacheDir).InstallAsync(Game.InstallDirectory, KnownGraphicsApiOrNull).ConfigureAwait(true);
                if (!result.Success)
                {
                    StatusText = $"RenoDX needs ReShade installed first, and that failed: {result.Error}";
                    api.Dialogs.ShowErrorMessage(StatusText, "Install RenoDX");
                    return false;
                }

                Log.Info($"Auto-installed ReShade {result.Version} ({result.Api}) for '{GameName}' ahead of RenoDX.");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"Auto ReShade install failed for '{GameName}' ahead of RenoDX.");
                StatusText = $"RenoDX needs ReShade installed first, and that failed: {ex.Message}";
                api.Dialogs.ShowErrorMessage(StatusText, "Install RenoDX");
                return false;
            }
        }

        // ── RenoDX ───────────────────────────────────────────────────────────────

        /* Best-effort single button: try the wiki-matched build first, and if there's simply no
           wiki entry for this game (as opposed to a blacklist/external-link outcome), fall back
           to auto-detecting the engine and installing the generic build — the granular per-mode
           menu items (matched/auto/Unreal/UE-Extended/Unity) remain available from the game's
           right-click menu for anyone who wants explicit control. */
        private async Task InstallRenoDxAsync()
        {
            StatusText = "Installing RenoDX (matched game)…";
            RenoDxExternalUrl = null;
            try
            {
                if (!await EnsureReShadeInstalledForRenoDxAsync().ConfigureAwait(true)) return;

                var manifestResult = await new ManifestService(userDataPath).FetchAsync().ConfigureAwait(true);
                var manifest = manifestResult?.Manifest;
                var wikiCacheDir = Path.Combine(userDataPath, "RenoDxWikiCache");

                var matched = await RenoDxService
                    .InstallForGameAsync(Game.InstallDirectory, GameName, wikiCacheDir, manifest?.WikiNameOverrides)
                    .ConfigureAwait(true);

                if (matched.Success)
                {
                    SaveRenoDxRecord(matched);
                    StatusText = $"Installed RenoDX ({matched.MatchedGameName})." + (matched.Notes != null ? $" Note: {matched.Notes}" : "");
                    return;
                }

                if (matched.ExternalUrl != null)
                {
                    RenoDxExternalUrl = matched.ExternalUrl;
                    StatusText = $"{matched.Error} Use \"Open link\" to get it manually.";
                    return;
                }

                if (matched.MatchedGameName != null)
                {
                    // Matched the wiki but had no download at all (not even external) — nothing more to try.
                    StatusText = matched.Error;
                    return;
                }

                StatusText = "No wiki match — trying engine auto-detect for a generic install…";
                var detection = RenoDxEngineDetector.Detect(
                    Game.InstallDirectory, GameName, manifest?.InstallPathOverrides, manifest?.EngineHintOverrides);
                if (detection.Engine == null)
                {
                    StatusText = "No RenoDX wiki match and couldn't auto-detect the engine — use the right-click menu's engine-specific install options instead.";
                    return;
                }

                var useUeExtended = detection.Engine == RenoDxEngine.UnrealEngine && RenoDxService.ShouldUseUeExtended(GameName, manifest);
                var generic = await RenoDxService
                    .InstallGenericAsync(detection.InstallPath, detection.Engine.Value, useUeExtended, manifest)
                    .ConfigureAwait(true);

                if (!generic.Success)
                {
                    StatusText = generic.Error;
                    return;
                }

                RenoDxRecords.Set(GameId, RenoDxService.CreateGenericRecord(GameId, GameName, generic));
                StatusText = $"Detected {detection.Engine} — installed generic RenoDX build.";
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"RenoDX install failed for '{GameName}'.");
                StatusText = $"RenoDX install failed: {ex.Message}";
                api.Dialogs.ShowErrorMessage(StatusText, "Install RenoDX");
            }
        }

        /* Explicit-control counterpart to InstallRenoDxAsync's best-effort button — forces a
           specific engine build (and, for Unreal, a specific UE-Extended choice) rather than
           auto-detecting. The record written here is flagged IsGeneric: there's no wiki version
           to track, so update-check just says so, but status and uninstall still work. */
        private async Task InstallRenoDxEngineAsync(RenoDxEngine engine, bool? forceUeExtended)
        {
            var engineLabel = engine == RenoDxEngine.UnrealEngine
                ? (forceUeExtended == true ? "Unreal Engine, UE-Extended" : "Unreal Engine, generic")
                : "Unity, generic";
            StatusText = $"Installing RenoDX ({engineLabel})…";
            try
            {
                if (!await EnsureReShadeInstalledForRenoDxAsync().ConfigureAwait(true)) return;

                RemoteManifest manifest = null;
                if (engine == RenoDxEngine.UnrealEngine)
                    manifest = (await new ManifestService(userDataPath).FetchAsync().ConfigureAwait(true))?.Manifest;

                var useUeExtended = forceUeExtended ?? RenoDxService.ShouldUseUeExtended(GameName, manifest);
                var result = await RenoDxService
                    .InstallGenericAsync(Game.InstallDirectory, engine, useUeExtended, manifest)
                    .ConfigureAwait(true);

                if (!result.Success)
                {
                    StatusText = result.Error;
                    return;
                }

                RenoDxRecords.Set(GameId, RenoDxService.CreateGenericRecord(GameId, GameName, result));
                StatusText = $"Installed RenoDX ({engineLabel}).";
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"RenoDX {engineLabel} install failed for '{GameName}'.");
                StatusText = $"RenoDX install failed: {ex.Message}";
                api.Dialogs.ShowErrorMessage(StatusText, "Install RenoDX");
            }
        }

        private void SaveRenoDxRecord(RenoDxMatchedInstallResult result)
        {
            RenoDxRecords.Set(GameId, new RenoDxInstalledRecord
            {
                GameId = GameId,
                GameName = GameName,
                InstallPath = result.InstalledPath,
                AddonFileName = result.AddonFileName,
                MatchedWikiName = result.MatchedGameName,
                DownloadUrl = result.DownloadUrl,
                FileHash = result.FileHash,
                RemoteContentLength = result.RemoteContentLength,
                RemoteLastModified = result.RemoteLastModified,
                InstalledAtUtc = DateTimeOffset.UtcNow
            });
        }

        private async Task CheckRenoDxUpdateAsync()
        {
            var record = RenoDxRecords.Get(GameId);
            if (record == null) return;

            StatusText = "Checking for a RenoDX update…";
            try
            {
                var manifestResult = await new ManifestService(userDataPath).FetchAsync().ConfigureAwait(true);
                var wikiCacheDir = Path.Combine(userDataPath, "RenoDxWikiCache");
                var check = await RenoDxService
                    .CheckForUpdateAsync(record, wikiCacheDir, manifestResult?.Manifest?.WikiNameOverrides)
                    .ConfigureAwait(true);

                StatusText = check.Status switch
                {
                    RenoDxUpdateStatus.UpToDate => "RenoDX is already up to date.",
                    RenoDxUpdateStatus.UpdateAvailable => "A newer RenoDX snapshot is available — reinstall to update.",
                    RenoDxUpdateStatus.NoLongerListed => "This game no longer has a matching RenoDX wiki entry.",
                    RenoDxUpdateStatus.GenericNotTracked => "Generic engine installs have no tracked version — reinstall to get the latest build.",
                    _ => "Unknown update status."
                };
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"RenoDX update check failed for '{GameName}'.");
                StatusText = $"RenoDX update check failed: {ex.Message}";
                api.Dialogs.ShowErrorMessage(StatusText, "RenoDX Update Check");
            }
        }

        private async Task UninstallRenoDxAsync()
        {
            var record = RenoDxRecords.Get(GameId);
            if (record == null) return;

            try
            {
                var result = RenoDxService.Uninstall(record);
                if (!result.Success)
                {
                    StatusText = result.Error;
                    return;
                }

                RenoDxRecords.Remove(GameId);
                StatusText = "Uninstalled RenoDX.";
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"RenoDX uninstall failed for '{GameName}'.");
                StatusText = $"RenoDX uninstall failed: {ex.Message}";
                api.Dialogs.ShowErrorMessage(StatusText, "Uninstall RenoDX");
            }

            await Task.CompletedTask;
        }

        // ── Luma ─────────────────────────────────────────────────────────────────

        private async Task InstallLumaAsync()
        {
            StatusText = "Installing Luma (matched game)…";
            try
            {
                if (!await EnsureDgvoodoo2InstalledForLumaAsync().ConfigureAwait(true)) return;

                var manifestResult = await new ManifestService(userDataPath).FetchAsync().ConfigureAwait(true);
                var manifest = manifestResult?.Manifest;

                var renoDxRecord = RenoDxRecords.Get(GameId);
                if (renoDxRecord != null && manifest?.IsLumaRenodxCompatible(GameName) != true)
                {
                    StatusText = "RenoDX is currently installed — uninstall it first before installing Luma (this game isn't on the manifest's dual-compat list).";
                    return;
                }

                var wikiCacheDir = Path.Combine(userDataPath, "LumaWikiCache");
                var result = await LumaService
                    .InstallForGameAsync(Game.InstallDirectory, GameName, wikiCacheDir, manifest?.LumaNameOverrides)
                    .ConfigureAwait(true);

                if (!result.Success)
                {
                    StatusText = result.Error;
                    return;
                }

                LumaRecords.Set(GameId, new LumaInstalledRecord
                {
                    GameId = GameId,
                    GameName = GameName,
                    InstallPath = result.InstalledPath,
                    InstalledFiles = result.InstalledFiles,
                    DownloadUrl = result.DownloadUrl,
                    InstalledBuildNumber = await LumaService.GetLatestBuildNumberAsync().ConfigureAwait(true) ?? 0,
                    InstalledAtUtc = DateTimeOffset.UtcNow
                });

                var dgvoodoo2Note = Dgvoodoo2Records.Get(GameId) != null ? " Also installed dgVoodoo2, a required prerequisite for this game." : "";
                StatusText = $"Installed Luma ({result.MatchedGameName})." + (result.Notes != null ? $" Note: {result.Notes}" : "") + dgvoodoo2Note;
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"Luma install failed for '{GameName}'.");
                StatusText = $"Luma install failed: {ex.Message}";
                api.Dialogs.ShowErrorMessage(StatusText, "Install Luma");
            }
        }

        /* Same shape as EnsureReShadeInstalledForRenoDxAsync — runs before the real Luma install
           whenever the Luma prerequisites manifest lists a "dgvoodoo2" step for this game
           (Vanquish today; see luma_prerequisites.json). Already-installed is a no-op, not a
           reinstall. Only aborts the Luma install if this actually fails; if the game has no
           listed prerequisite at all, this is an instant no-op. */
        private async Task<bool> EnsureDgvoodoo2InstalledForLumaAsync()
        {
            if (Dgvoodoo2Steps.Count == 0) return true;
            if (Dgvoodoo2Records.Get(GameId) != null) return true;

            try
            {
                if (KnownDxvkApiOrNull == null) await EnsurePcGamingWikiLookupCompletedAsync().ConfigureAwait(true);

                var cacheDir = Path.Combine(userDataPath, "Dgvoodoo2Cache");
                var result = await Dgvoodoo2Service.InstallAsync(Game.InstallDirectory, cacheDir, KnownDxvkApiOrNull).ConfigureAwait(true);
                if (!result.Success)
                {
                    StatusText = $"Luma needs dgVoodoo2 installed first, and that failed: {result.Error}";
                    api.Dialogs.ShowErrorMessage(StatusText, "Install Luma");
                    return false;
                }

                Dgvoodoo2Records.Set(GameId, new Dgvoodoo2InstalledRecord
                {
                    GameId = GameId,
                    GameName = GameName,
                    InstallPath = result.DeployDirectory,
                    InstalledFiles = result.InstalledFiles,
                    BackedUpFiles = result.BackedUpFiles,
                    Version = result.Version,
                    InstalledAtUtc = DateTimeOffset.UtcNow
                });

                Log.Info($"Auto-installed dgVoodoo2 {result.Version} for '{GameName}' ahead of Luma.");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"Auto dgVoodoo2 install failed for '{GameName}' ahead of Luma.");
                StatusText = $"Luma needs dgVoodoo2 installed first, and that failed: {ex.Message}";
                api.Dialogs.ShowErrorMessage(StatusText, "Install Luma");
                return false;
            }
        }

        private async Task CheckLumaUpdateAsync()
        {
            var record = LumaRecords.Get(GameId);
            if (record == null) return;

            try
            {
                var check = await LumaService.CheckForUpdateAsync(record).ConfigureAwait(true);
                StatusText = check.Status switch
                {
                    LumaUpdateStatus.UpToDate => $"Luma is already up to date (build {record.InstalledBuildNumber}).",
                    LumaUpdateStatus.UpdateAvailable => $"A newer Luma build is available ({record.InstalledBuildNumber} -> {check.LatestBuildNumber}).",
                    LumaUpdateStatus.UnknownLocalInstall => "This Luma install has no known build number to compare.",
                    LumaUpdateStatus.CouldNotCheck => "Could not reach the Luma Framework release feed.",
                    _ => "Unknown update status."
                };
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"Luma update check failed for '{GameName}'.");
                StatusText = $"Luma update check failed: {ex.Message}";
                api.Dialogs.ShowErrorMessage(StatusText, "Luma Update Check");
            }
        }

        private async Task UninstallLumaAsync()
        {
            var record = LumaRecords.Get(GameId);
            if (record == null) return;

            try
            {
                var result = LumaService.Uninstall(record);
                if (!result.Success)
                {
                    StatusText = result.Error;
                    return;
                }

                LumaRecords.Remove(GameId);
                StatusText = "Uninstalled Luma." + UninstallDgvoodoo2IfPresent();
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"Luma uninstall failed for '{GameName}'.");
                StatusText = $"Luma uninstall failed: {ex.Message}";
                api.Dialogs.ShowErrorMessage(StatusText, "Uninstall Luma");
            }

            await Task.CompletedTask;
        }

        /* Mirror of EnsureDgvoodoo2InstalledForLumaAsync on the way out — dgVoodoo2 was only ever
           installed here as Luma's own prerequisite, not as something the user chose
           independently, so uninstalling Luma should clean it up too rather than leaving it
           behind as an orphaned, addon-tracked install. A failure here is logged but doesn't
           turn the overall "Uninstall Luma" action into a failure — Luma itself is already gone. */
        private string UninstallDgvoodoo2IfPresent()
        {
            var record = Dgvoodoo2Records.Get(GameId);
            if (record == null) return "";

            try
            {
                var result = Dgvoodoo2Service.Uninstall(record);
                if (!result.Success)
                {
                    Log.Error($"dgVoodoo2 uninstall failed for '{GameName}': {result.Error}");
                    return $" (Also tried to remove dgVoodoo2, but that failed: {result.Error})";
                }

                Dgvoodoo2Records.Remove(GameId);
                return " Also removed dgVoodoo2.";
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"dgVoodoo2 uninstall failed for '{GameName}'.");
                return $" (Also tried to remove dgVoodoo2, but that failed: {ex.Message})";
            }
        }

        // ── DXVK+Lilium ──────────────────────────────────────────────────────────

        private async Task InstallDxvkAsync()
        {
            StatusText = "Installing DXVK+Lilium HDR…";
            try
            {
                // Same race as InstallReShadeAsync — local PE inspection can still read
                // GraphicsApi.Unknown while the PCGamingWiki fallback lookup is in flight.
                if (KnownDxvkApiOrNull == null) await EnsurePcGamingWikiLookupCompletedAsync().ConfigureAwait(true);

                var cacheDir = Path.Combine(userDataPath, "DxvkCache");
                var result = await DxvkService.InstallAsync(Game.InstallDirectory, cacheDir, KnownDxvkApiOrNull).ConfigureAwait(true);
                if (!result.Success)
                {
                    StatusText = result.Error;
                    return;
                }

                DxvkRecords.Set(GameId, new DxvkInstalledRecord
                {
                    GameId = GameId,
                    GameName = GameName,
                    InstallPath = result.DeployDirectory,
                    InstalledDlls = result.InstalledDlls,
                    BackedUpFiles = result.BackedUpFiles,
                    DeployedConf = result.DeployedConf,
                    Version = result.Version,
                    InstalledAtUtc = DateTimeOffset.UtcNow
                });

                StatusText = $"Installed DXVK+Lilium HDR {result.Version}. {result.Warning}";
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"DXVK+Lilium install failed for '{GameName}'.");
                StatusText = $"DXVK+Lilium install failed: {ex.Message}";
                api.Dialogs.ShowErrorMessage(StatusText, "Install DXVK+Lilium HDR");
            }
        }

        private async Task CheckDxvkUpdateAsync()
        {
            var record = DxvkRecords.Get(GameId);
            if (record == null) return;

            try
            {
                var check = await DxvkService.CheckForUpdateAsync(record).ConfigureAwait(true);
                StatusText = check.Status switch
                {
                    DxvkUpdateStatus.UpToDate => $"DXVK+Lilium is already up to date ({record.Version}).",
                    DxvkUpdateStatus.UpdateAvailable => $"A newer DXVK+Lilium build is available ({record.Version} -> {check.LatestVersion}).",
                    DxvkUpdateStatus.CouldNotCheck => "Could not reach the Lilium HDR DXVK release feed.",
                    _ => "Unknown update status."
                };
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"DXVK+Lilium update check failed for '{GameName}'.");
                StatusText = $"DXVK+Lilium update check failed: {ex.Message}";
                api.Dialogs.ShowErrorMessage(StatusText, "DXVK+Lilium Update Check");
            }
        }

        private async Task UninstallDxvkAsync()
        {
            var record = DxvkRecords.Get(GameId);
            if (record == null) return;

            try
            {
                var result = DxvkService.Uninstall(record);
                if (!result.Success)
                {
                    StatusText = result.Error;
                    return;
                }

                DxvkRecords.Remove(GameId);
                StatusText = "Uninstalled DXVK+Lilium.";
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"DXVK+Lilium uninstall failed for '{GameName}'.");
                StatusText = $"DXVK+Lilium uninstall failed: {ex.Message}";
                api.Dialogs.ShowErrorMessage(StatusText, "Uninstall DXVK+Lilium");
            }

            await Task.CompletedTask;
        }

        // ── OptiScaler ───────────────────────────────────────────────────────────

        private async Task InstallOptiScalerAsync(bool dlssInputs)
        {
            StatusText = "Installing OptiScaler…";
            try
            {
                var manifestResult = await new ManifestService(userDataPath).FetchAsync().ConfigureAwait(true);
                var cacheDir = Path.Combine(userDataPath, "OptiScalerCache");

                var result = await OptiScalerService
                    .InstallAsync(Game.InstallDirectory, GameName, dlssInputs, cacheDir, manifestResult?.Manifest)
                    .ConfigureAwait(true);

                if (!result.Success)
                {
                    StatusText = result.Error;
                    return;
                }

                OptiScalerRecords.Set(GameId, new OptiScalerInstalledRecord
                {
                    GameId = GameId,
                    GameName = GameName,
                    InstallPath = result.DeployDirectory,
                    DllName = result.DllName,
                    InstalledFiles = result.InstalledFiles,
                    DlssInputs = dlssInputs,
                    OptiPatcherInstalled = result.OptiPatcherInstalled,
                    DlssDllInstalled = result.DlssDllInstalled,
                    Version = result.Version,
                    InstalledAtUtc = DateTimeOffset.UtcNow
                });

                StatusText = $"Installed OptiScaler {result.Version} ({result.DllName}). " +
                    $"OptiPatcher: {(result.OptiPatcherInstalled ? "yes" : "no")}. " +
                    $"DLSS-input DLL: {(result.DlssDllInstalled ? "staged" : "not staged")}.";
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"OptiScaler install failed for '{GameName}'.");
                StatusText = $"OptiScaler install failed: {ex.Message}";
                api.Dialogs.ShowErrorMessage(StatusText, "Install OptiScaler");
            }
        }

        private async Task CheckOptiScalerUpdateAsync()
        {
            var record = OptiScalerRecords.Get(GameId);
            if (record == null) return;

            try
            {
                var check = await OptiScalerService.CheckForUpdateAsync(record).ConfigureAwait(true);
                StatusText = check.Status switch
                {
                    OptiScalerUpdateStatus.UpToDate => $"OptiScaler is already up to date ({record.Version}).",
                    OptiScalerUpdateStatus.UpdateAvailable => $"A newer OptiScaler build is available ({record.Version} -> {check.LatestVersion}).",
                    OptiScalerUpdateStatus.CouldNotCheck => "Could not reach the OptiScaler release feed.",
                    _ => "Unknown update status."
                };
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"OptiScaler update check failed for '{GameName}'.");
                StatusText = $"OptiScaler update check failed: {ex.Message}";
                api.Dialogs.ShowErrorMessage(StatusText, "OptiScaler Update Check");
            }
        }

        private async Task UninstallOptiScalerAsync()
        {
            var record = OptiScalerRecords.Get(GameId);
            if (record == null) return;

            try
            {
                var result = OptiScalerService.Uninstall(record);
                if (!result.Success)
                {
                    StatusText = result.Error;
                    return;
                }

                OptiScalerRecords.Remove(GameId);
                StatusText = "Uninstalled OptiScaler.";
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"OptiScaler uninstall failed for '{GameName}'.");
                StatusText = $"OptiScaler uninstall failed: {ex.Message}";
                api.Dialogs.ShowErrorMessage(StatusText, "Uninstall OptiScaler");
            }

            await Task.CompletedTask;
        }
    }
}
