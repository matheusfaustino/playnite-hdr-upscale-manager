using System;
using System.IO;
using HdrUpscaleManager.Services.ReShade;
using Xunit;

namespace HdrUpscaleManager.Tests
{
    public class PeInspectorUnityTests : IDisposable
    {
        private readonly string gameDirectory = Path.Combine(Path.GetTempPath(), "hdrum-unity-tests-" + Guid.NewGuid());

        public PeInspectorUnityTests() => Directory.CreateDirectory(gameDirectory);

        public void Dispose()
        {
            if (Directory.Exists(gameDirectory)) Directory.Delete(gameDirectory, recursive: true);
        }

        private string WriteExe(bool is64Bit, params string[] importedDlls)
        {
            var path = Path.Combine(gameDirectory, "Game.exe");
            File.WriteAllBytes(path, PeInspectorTests.BuildSyntheticPe(is64Bit, importedDlls));
            return path;
        }

        [Fact]
        public void Inspect_UnityStubExeWithNoGraphicsImports_FallsBackToD3D11()
        {
            /* Deep Rock Galactic: Survivor shape, confirmed live 2026-10-31: DRG Survivor.exe
               (~650KB) imports no graphics DLL at all, since Unity's player loads them from
               UnityPlayer.dll at runtime, so ReShade failed with "Unknown isn't supported by a
               drop-in ReShade install". Unity on Windows defaults to D3D11. */
            var exe = WriteExe(true, "kernel32.dll");
            File.WriteAllBytes(Path.Combine(gameDirectory, "UnityPlayer.dll"), new byte[16]);

            var result = PeInspector.Inspect(exe);

            Assert.NotNull(result);
            Assert.True(result.Is64Bit);
            Assert.Equal(GraphicsApi.D3D11, result.Api);
        }

        [Fact]
        public void Inspect_NoGraphicsImportsAndNoUnityPlayer_StaysUnknown()
        {
            var exe = WriteExe(true, "kernel32.dll");

            var result = PeInspector.Inspect(exe);

            Assert.Equal(GraphicsApi.Unknown, result.Api);
        }

        [Fact]
        public void Inspect_UnityPlayerPresentButExeImportsD3D12_KeepsRealImport()
        {
            var exe = WriteExe(true, "d3d12.dll");
            File.WriteAllBytes(Path.Combine(gameDirectory, "UnityPlayer.dll"), new byte[16]);

            var result = PeInspector.Inspect(exe);

            Assert.Equal(GraphicsApi.D3D12, result.Api);
        }
    }
}
