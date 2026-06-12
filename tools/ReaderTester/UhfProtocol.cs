namespace ReaderTester;

/// <summary>
/// UHF RFID Reader Series V2.20 binary protocol.
/// Frame format: [Len][Adr][Cmd][Data...][CRC-LSB][CRC-MSB]
/// CRC16: preset 0xFFFF, polynomial 0x8408 (reflected).
/// </summary>
public static class UhfProtocol
{
    public const byte TagReportCmd = 0xEE;
    public const byte HeartbeatStatus = 0x28;
    public const byte SuccessStatus = 0x00;

    // Command bytes
    public const byte CmdGetReaderInfo = 0x21;
    public const byte CmdSetRfPower = 0x2F;
    public const byte CmdBuzzer = 0x33;
    public const byte CmdSetWorkMode = 0x76;
    public const byte CmdGetWorkMode = 0x77;

    public static byte[] BuildCommand(byte address, byte cmd, ReadOnlySpan<byte> data)
    {
        byte frameLen = (byte)(data.Length + 4); // Adr + Cmd + Data + CRC(2)
        var frame = new byte[frameLen + 1];      // +1 for the Len byte itself
        frame[0] = frameLen;
        frame[1] = address;
        frame[2] = cmd;
        if (data.Length > 0)
            data.CopyTo(frame.AsSpan(3));

        ushort crc = Crc16(frame, frame.Length - 2);
        frame[^2] = (byte)(crc & 0xFF);
        frame[^1] = (byte)((crc >> 8) & 0xFF);
        return frame;
    }

    /// <summary>
    /// Extracts ALL complete, CRC-valid frames from an accumulation buffer.
    /// Returns the frames and how many bytes were consumed (caller removes them).
    /// Invalid bytes are skipped one at a time to resync.
    /// </summary>
    public static (List<byte[]> Frames, int BytesConsumed) ExtractFrames(ReadOnlySpan<byte> buffer)
    {
        var frames = new List<byte[]>();
        int offset = 0;

        while (offset < buffer.Length)
        {
            byte lenField = buffer[offset];
            int totalFrameSize = lenField + 1;

            // Minimum valid frame: [Len][Adr][Cmd][Status][CRC][CRC] => Len >= 5
            if (lenField < 5)
            {
                offset++;
                continue;
            }

            if (offset + totalFrameSize > buffer.Length)
                break; // incomplete — wait for more data

            var frame = buffer.Slice(offset, totalFrameSize);
            if (!VerifyCrc(frame))
            {
                offset++;
                continue;
            }

            frames.Add(frame.ToArray());
            offset += totalFrameSize;
        }

        return (frames, offset);
    }

    /// <summary>
    /// Parses a 0xEE tag report: [Len][Adr][0xEE][0x00][Ant][EpcLen][EPC...][RSSI][CRC][CRC]
    /// </summary>
    public static (string Epc, byte Antenna, int? Rssi)? ParseTagFrame(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 9) return null;

        byte antennaId = frame[4];
        byte epcLength = frame[5];
        if (frame.Length < 8 + epcLength) return null;

        string epc = Convert.ToHexString(frame.Slice(6, epcLength));

        int? rssi = null;
        if (frame.Length > 8 + epcLength)
            rssi = (sbyte)frame[6 + epcLength];

        return (epc, antennaId, rssi);
    }

    public static bool VerifyCrc(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 5) return false;
        ushort calc = Crc16(frame, frame.Length - 2);
        return frame[^2] == (byte)(calc & 0xFF) && frame[^1] == (byte)((calc >> 8) & 0xFF);
    }

    public static ushort Crc16(ReadOnlySpan<byte> data, int length)
    {
        ushort crc = 0xFFFF;
        for (int i = 0; i < length; i++)
        {
            crc ^= data[i];
            for (int j = 0; j < 8; j++)
                crc = (crc & 0x0001) != 0 ? (ushort)((crc >> 1) ^ 0x8408) : (ushort)(crc >> 1);
        }
        return crc;
    }

    public static string ToHex(ReadOnlySpan<byte> data)
    {
        var sb = new System.Text.StringBuilder(data.Length * 3);
        foreach (byte b in data)
            sb.Append(b.ToString("X2")).Append(' ');
        return sb.ToString().TrimEnd();
    }
}
