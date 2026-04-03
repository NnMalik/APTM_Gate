using APTM.Gate.Core.Models;

namespace APTM.Gate.Workers;

/// <summary>
/// Implements the UHF RFID Reader Series V2.20 binary protocol.
/// Frame format: [Len][Adr][Cmd][Data...][CRC-LSB][CRC-MSB]
/// CRC16: preset 0xFFFF, polynomial 0x8408 (reflected).
/// </summary>
public static class UhfFrameParser
{
    private const byte TagReportCmd = 0xEE;
    private const byte HeartbeatStatus = 0x28;
    private const byte SuccessStatus = 0x00;
    private const ushort CrcPreset = 0xFFFF;
    private const ushort CrcPolynomial = 0x8408;

    /// <summary>
    /// Builds a command frame: [Len][Adr][Cmd][Data...][CRC-LSB][CRC-MSB].
    /// Len = Adr(1) + Cmd(1) + Data(n) + CRC(2).
    /// </summary>
    public static byte[] BuildCommandFrame(byte address, byte cmd, ReadOnlySpan<byte> data)
    {
        int dataLen = data.Length;
        byte frameLen = (byte)(dataLen + 4); // Adr + Cmd + Data + CRC(2)

        var frame = new byte[frameLen + 1]; // +1 for the Len byte itself
        frame[0] = frameLen;
        frame[1] = address;
        frame[2] = cmd;
        if (dataLen > 0)
            data.CopyTo(frame.AsSpan(3));

        // CRC16 calculated from index 0 (Len) to end of Data (excludes CRC bytes)
        ushort crc = CalculateCRC16(frame, frame.Length - 2);
        frame[^2] = (byte)(crc & 0xFF);        // LSB
        frame[^1] = (byte)((crc >> 8) & 0xFF); // MSB

        return frame;
    }

    /// <summary>
    /// Extracts complete frames from an accumulation buffer.
    /// Returns parsed tag frames and total bytes consumed.
    /// </summary>
    public static (List<RawTagFrame> Frames, int BytesConsumed) ExtractTagFrames(ReadOnlySpan<byte> buffer)
    {
        var frames = new List<RawTagFrame>();
        int offset = 0;

        while (offset < buffer.Length)
        {
            byte lenField = buffer[offset];
            int totalFrameSize = lenField + 1;

            // Minimum valid frame: [Len][Adr][Cmd][Status][CRC][CRC] = Len >= 5
            if (lenField < 5)
            {
                offset++; // Skip invalid byte, try to resync
                continue;
            }

            if (offset + totalFrameSize > buffer.Length)
                break; // Incomplete frame — wait for more data

            var frame = buffer.Slice(offset, totalFrameSize);

            // Verify CRC before processing
            if (!VerifyCRC(frame))
            {
                offset++; // CRC fail — skip byte and resync
                continue;
            }

            byte reCmd = frame[2];
            byte status = frame[3];

            // Only process real-time inventory tag reports (0xEE)
            if (reCmd == TagReportCmd && status == SuccessStatus)
            {
                var tagFrame = ParseTagFrame(frame);
                if (tagFrame is not null)
                    frames.Add(tagFrame);
            }
            // Heartbeat (0xEE with status 0x28) — silently skip

            offset += totalFrameSize;
        }

        return (frames, offset);
    }

    /// <summary>
    /// Parses a validated 0xEE tag frame into a RawTagFrame.
    /// Frame: [Len][Adr][0xEE][0x00][AntennaId][EpcLen][EPC...][RSSI][CRC][CRC]
    /// </summary>
    private static RawTagFrame? ParseTagFrame(ReadOnlySpan<byte> frame)
    {
        // Minimum: Len(1) + Adr(1) + Cmd(1) + Status(1) + Ant(1) + EpcLen(1) + EPC(1) + CRC(2) = 9
        if (frame.Length < 9) return null;

        byte antennaId = frame[4];
        byte epcLength = frame[5];

        // Validate frame has enough bytes for EPC + CRC
        // Total = 1(Len) + 1(Adr) + 1(Cmd) + 1(Status) + 1(Ant) + 1(EpcLen) + epcLength + 2(CRC) = 8 + epcLength
        if (frame.Length < 8 + epcLength)
            return null;

        // Extract EPC bytes → hex string
        var epcBytes = frame.Slice(6, epcLength);
        string tagEPC = Convert.ToHexString(epcBytes);

        // RSSI: signed byte right after EPC, before CRC
        int? rssi = null;
        int rssiIndex = 6 + epcLength;
        if (frame.Length > 8 + epcLength) // RSSI byte present
        {
            rssi = (sbyte)frame[rssiIndex];
        }

        return new RawTagFrame
        {
            TagEPC = tagEPC,
            AntennaPort = antennaId,
            RSSI = rssi,
            ReadTime = DateTimeOffset.UtcNow
        };
    }

    /// <summary>
    /// Verifies CRC16 of a complete frame.
    /// CRC is computed over all bytes except the last two (which are CRC-LSB, CRC-MSB).
    /// </summary>
    public static bool VerifyCRC(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 5) return false;

        int dataLength = frame.Length - 2;
        ushort calculated = CalculateCRC16(frame, dataLength);

        byte receivedLsb = frame[^2];
        byte receivedMsb = frame[^1];

        return receivedLsb == (byte)(calculated & 0xFF)
            && receivedMsb == (byte)((calculated >> 8) & 0xFF);
    }

    /// <summary>
    /// CRC16 with preset 0xFFFF and reflected polynomial 0x8408.
    /// Direct port of the reader manual's C algorithm.
    /// </summary>
    public static ushort CalculateCRC16(ReadOnlySpan<byte> data, int length)
    {
        ushort crc = CrcPreset;

        for (int i = 0; i < length; i++)
        {
            crc ^= data[i];
            for (int j = 0; j < 8; j++)
            {
                if ((crc & 0x0001) != 0)
                    crc = (ushort)((crc >> 1) ^ CrcPolynomial);
                else
                    crc >>= 1;
            }
        }

        return crc;
    }
}
