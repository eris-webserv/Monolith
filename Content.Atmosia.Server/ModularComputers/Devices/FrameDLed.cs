using global::Spartan;

namespace Content.Atmosia.Server.ModularComputers.Devices;

public sealed class FrameDLed(int vramBytes = 4096) : StorageDevice(new byte[vramBytes])
{
    public override uint Id => 0x04331a04;
    public override uint RingWords => 24;
    public override uint ResponseWords => 24;
    public uint Pixel { get; private set; } = 0x000000ff;
    public ulong Generation { get; private set; }
    private uint _working = 0x000000ff;

    public override DeviceResponse MmioCommand(ReadOnlySpan<uint> request, Span<uint> response)
    {
        if (request.IsEmpty) return DeviceResponse.Fault;
        if (request[0] < 0x10) return DeviceResponse.Unhandled;
        if (request[0] == 0x10)
        {
            if (response.Length < 24) return DeviceResponse.Fault;
            response[..24].Clear();
            response[0] = Id;
            response[1] = 1;
            response[2] = response[3] = 1;
            response[4] = 32;
            response[5] = 4;
            response[6] = (uint)Storage.Length;
            response[7] = 0x83;
            response[9] = RingWords;
            response[10] = 2;
            response[11] = 256;
            response[12] = 8192;
            response[16] = 65536;
            response[20] = (uint)Generation;
            response[21] = (uint)(Generation >> 32);
            return DeviceResponse.Reply(24);
        }
        if (response.IsEmpty || !Draw(request)) return DeviceResponse.Fault;
        response[0] = 0;
        return DeviceResponse.Reply(1);
    }

    public override bool RingCommand(DeviceRing ring, ReadOnlySpan<uint> entry) => Draw(entry);

    private bool Draw(ReadOnlySpan<uint> command)
    {
        if (command.IsEmpty) return false;
        switch (command[0])
        {
            case 0x11 when command.Length >= 2:
                _working = command[1];
                return true;
            case 0x12 when command.Length >= 6:
                long x = (int)command[1], y = (int)command[2];
                if (x <= 0 && y <= 0 && x + command[3] > 0 && y + command[4] > 0)
                    _working = command[5];
                return true;
            case 0x19:
                Pixel = _working;
                Generation++;
                return true;
            default:
                return false;
        }
    }

    public override void Reset()
    {
        Array.Clear(Storage);
        Pixel = _working = 0x000000ff;
        Generation = 0;
    }
}
