using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using NUnit.Framework;

namespace Content.Atmosia.IntegrationTests;

[TestFixture, NonParallelizable]
public sealed class SpartanBindingsTest
{
    [Test]
    public void ServerBindingsLoadNativeRuntimeAndDriveCSharpDevice()
    {
        Assume.That(RuntimeInformation.ProcessArchitecture == Architecture.X64
                    && (OperatingSystem.IsLinux() || OperatingSystem.IsWindows()),
            "Spartan native runtime requires Linux/Windows x64.");
        var library = OperatingSystem.IsWindows() ? "spartan_ffi.dll" : "libspartan_ffi.so";
        Assume.That(File.Exists(Path.Combine(AppContext.BaseDirectory, library)),
            "Stage the pinned Spartan native runtime before running this test.");
        uint[] program = [
            0xfffff2b7, 0x00c28293,
            0x00800313, 0x0062a023,
            0x01100313, 0x0062a023,
            0x12345337, 0x6ff30313, 0x0062a023,
            0x00400313, 0x0062a023,
            0x01900313, 0x0062a023,
            0x10500073
        ];
        var rom = new byte[program.Length * 4];
        for (var i = 0; i < program.Length; ++i)
            BinaryPrimitives.WriteUInt32LittleEndian(rom.AsSpan(i * 4), program[i]);
        using var machine = new global::Spartan.Machine(rom);
        var led = new Content.Atmosia.Server.ModularComputers.Devices.FrameDLed();
        machine.Attach(3, led);
        var result = machine.Run(1000);
        Assert.Multiple(() =>
        {
            Assert.That(result.sleeping, Is.EqualTo(1));
            Assert.That(led.Pixel, Is.EqualTo(0x123456ff));
            Assert.That(led.Generation, Is.EqualTo(1));
        });
        machine.WriteMemory(0x100000, [1, 2, 3, 4]);
        var bytes = new byte[4];
        machine.ReadMemory(0x100000, bytes);
        Assert.That(bytes, Is.EqualTo(new byte[] { 1, 2, 3, 4 }));
    }
}
