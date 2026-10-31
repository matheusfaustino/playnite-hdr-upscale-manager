using System;
using System.IO;
using System.Text;
using HdrUpscaleManager.Services.ReShade;
using Xunit;

namespace HdrUpscaleManager.Tests
{
    public class PeInspectorTests : IDisposable
    {
        private readonly string tempFile;

        public PeInspectorTests()
        {
            tempFile = Path.Combine(Path.GetTempPath(), "PeInspectorTests_" + Guid.NewGuid().ToString("N") + ".exe");
        }

        public void Dispose()
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }

        private string WritePe(bool is64Bit, params string[] importedDlls)
        {
            File.WriteAllBytes(tempFile, BuildSyntheticPe(is64Bit, importedDlls));
            return tempFile;
        }

        [Fact]
        public void Inspect_32BitD3D11Import_DetectsD3D11AndBitness()
        {
            var path = WritePe(false, "d3d11.dll");

            var result = PeInspector.Inspect(path);

            Assert.NotNull(result);
            Assert.False(result.Is64Bit);
            Assert.Equal(GraphicsApi.D3D11, result.Api);
        }

        [Fact]
        public void Inspect_32BitD3D8Import_DetectsD3D8()
        {
            var path = WritePe(false, "d3d8.dll");

            var result = PeInspector.Inspect(path);

            Assert.Equal(GraphicsApi.D3D8, result.Api);
        }

        [Fact]
        public void Inspect_D3D8AndD3D9Imports_PrefersD3D9()
        {
            var path = WritePe(false, "d3d8.dll", "d3d9.dll");

            var result = PeInspector.Inspect(path);

            Assert.Equal(GraphicsApi.D3D9, result.Api);
        }

        [Fact]
        public void Inspect_64BitVulkanImport_DetectsVulkanAndBitness()
        {
            var path = WritePe(true, "vulkan-1.dll");

            var result = PeInspector.Inspect(path);

            Assert.NotNull(result);
            Assert.True(result.Is64Bit);
            Assert.Equal(GraphicsApi.Vulkan, result.Api);
        }

        [Fact]
        public void Inspect_DxgiOnlyImport_InfersD3D12()
        {
            var path = WritePe(true, "dxgi.dll");

            var result = PeInspector.Inspect(path);

            Assert.Equal(GraphicsApi.D3D12, result.Api);
        }

        [Fact]
        public void Inspect_MultipleImports_PicksHighestPriorityApi()
        {
            var path = WritePe(false, "opengl32.dll", "d3d11.dll");

            var result = PeInspector.Inspect(path);

            Assert.Equal(GraphicsApi.D3D11, result.Api);
        }

        [Fact]
        public void Inspect_NoRecognizedGraphicsImport_ReturnsUnknown()
        {
            var path = WritePe(false, "kernel32.dll");

            var result = PeInspector.Inspect(path);

            Assert.Equal(GraphicsApi.Unknown, result.Api);
        }

        [Fact]
        public void Inspect_NotAPeFile_ReturnsNull()
        {
            File.WriteAllBytes(tempFile, new byte[] { 0, 0, 0, 0, 0, 0, 0, 0 });

            var result = PeInspector.Inspect(tempFile);

            Assert.Null(result);
        }

        [Fact]
        public void Inspect_MissingFile_ReturnsNull()
        {
            var result = PeInspector.Inspect(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid() + ".exe"));

            Assert.Null(result);
        }

        // Builds a minimal-but-valid PE image: DOS header -> PE/COFF header -> optional
        // header with a single import-table data directory entry -> one section holding
        // the import directory table and the imported DLL name strings.
        internal static byte[] BuildSyntheticPe(bool is64Bit, params string[] importedDlls)
        {
            const int peOffset = 0x80;
            const int coffOffset = peOffset + 4;
            const int optionalHeaderOffset = coffOffset + 20;
            var sizeOfOptionalHeader = (is64Bit ? 120 : 104) + 8;
            var sectionTableOffset = optionalHeaderOffset + sizeOfOptionalHeader;
            const int sectionVirtualAddress = 0x1000;
            const int sectionRawPointer = 0x400;

            var importDirSize = (importedDlls.Length + 1) * 20;
            var nameOffsets = new int[importedDlls.Length];
            var cursor = importDirSize;
            for (var i = 0; i < importedDlls.Length; i++)
            {
                nameOffsets[i] = cursor;
                cursor += importedDlls[i].Length + 1;
            }

            var totalSize = sectionRawPointer + cursor + 16;
            var buffer = new byte[totalSize];

            buffer[0] = (byte)'M';
            buffer[1] = (byte)'Z';
            BitConverter.GetBytes(peOffset).CopyTo(buffer, 0x3C);

            buffer[peOffset] = (byte)'P';
            buffer[peOffset + 1] = (byte)'E';

            var machine = is64Bit ? (ushort)0x8664 : (ushort)0x14c;
            BitConverter.GetBytes(machine).CopyTo(buffer, coffOffset);
            BitConverter.GetBytes((ushort)1).CopyTo(buffer, coffOffset + 2);
            BitConverter.GetBytes((ushort)sizeOfOptionalHeader).CopyTo(buffer, coffOffset + 16);

            var magic = is64Bit ? (ushort)0x20B : (ushort)0x10B;
            BitConverter.GetBytes(magic).CopyTo(buffer, optionalHeaderOffset);
            var importDirOffset = optionalHeaderOffset + (is64Bit ? 120 : 104);
            BitConverter.GetBytes((uint)sectionVirtualAddress).CopyTo(buffer, importDirOffset);
            BitConverter.GetBytes((uint)importDirSize).CopyTo(buffer, importDirOffset + 4);

            BitConverter.GetBytes((uint)cursor).CopyTo(buffer, sectionTableOffset + 8);
            BitConverter.GetBytes((uint)sectionVirtualAddress).CopyTo(buffer, sectionTableOffset + 12);
            BitConverter.GetBytes((uint)sectionRawPointer).CopyTo(buffer, sectionTableOffset + 20);

            for (var i = 0; i < importedDlls.Length; i++)
            {
                var entryFileOffset = sectionRawPointer + (i * 20);
                var nameRva = (uint)(sectionVirtualAddress + nameOffsets[i]);
                BitConverter.GetBytes(nameRva).CopyTo(buffer, entryFileOffset + 12);

                var nameFileOffset = sectionRawPointer + nameOffsets[i];
                Encoding.ASCII.GetBytes(importedDlls[i]).CopyTo(buffer, nameFileOffset);
            }

            return buffer;
        }
    }
}
