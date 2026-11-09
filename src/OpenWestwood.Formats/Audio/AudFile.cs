namespace OpenWestwood.Formats.Audio;

/// <summary>
/// Westwood's .AUD sound files (Tiberian Dawn's SOUNDS.MIX and SPEECH.MIX, Dune 2000's GAMESFX): a 12-byte
/// header (sample rate, sizes, flags, compression), then chunks (compressed size, output size, 0x0000DEAF, data).
/// Compression 99 is IMA ADPCM (16-bit, its state carried from chunk to chunk); 1 is Westwood's own 8-bit
/// WS-ADPCM. Decoded to 16-bit PCM.
/// </summary>
public static class AudFile
{
    static readonly int[] ImaIndexAdjust = [-1, -1, -1, -1, 2, 4, 6, 8, -1, -1, -1, -1, 2, 4, 6, 8];

    static readonly int[] ImaStep =
    [
        7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97, 107, 118,
        130, 143, 157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796, 876, 963, 1060,
        1166, 1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871, 5358, 5894, 6484,
        7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899, 15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767,
    ];

    static readonly int[] Ws2Bit = [-2, -1, 0, 1];
    static readonly int[] Ws4Bit = [-9, -8, -6, -5, -4, -3, -2, -1, 0, 1, 2, 3, 4, 5, 6, 8];

    public static bool IsAud(ReadOnlySpan<byte> data) =>
        data.Length >= 20 && data[11] is 1 or 99 && BitConverter.ToUInt32(data[16..20]) == 0x0000DEAF;

    public static PcmSound Decode(ReadOnlySpan<byte> data)
    {
        int rate = BitConverter.ToUInt16(data);
        int outputSize = BitConverter.ToInt32(data[6..]);
        byte flags = data[10], compression = data[11];
        int channels = (flags & 1) != 0 ? 2 : 1;
        var samples = new List<short>(compression == 99 ? outputSize / 2 : outputSize);
        int pos = 12;
        int index = 0, predictor = 0; // IMA state, carried across chunks
        while (pos + 8 <= data.Length)
        {
            int chunkIn = BitConverter.ToUInt16(data[pos..]), chunkOut = BitConverter.ToUInt16(data[(pos + 2)..]);
            if (BitConverter.ToUInt32(data[(pos + 4)..]) != 0x0000DEAF)
                break;
            pos += 8;
            if (pos + chunkIn > data.Length)
                break;
            var chunk = data.Slice(pos, chunkIn);
            pos += chunkIn;
            if (compression == 99)
            {
                foreach (byte b in chunk)
                {
                    samples.Add(Ima(b & 0xF, ref index, ref predictor));
                    samples.Add(Ima(b >> 4, ref index, ref predictor));
                }
            }
            else
            {
                var bytes = new byte[chunkOut];
                WsAdpcm(chunk, bytes);
                foreach (byte b in bytes)
                    samples.Add((short)((b - 128) << 8));
            }
        }
        return new PcmSound { Samples = samples.ToArray(), Channels = channels, SampleRate = Math.Max(1, rate) };
    }

    static short Ima(int code, ref int index, ref int predictor)
    {
        int step = ImaStep[index];
        int diff = step >> 3;
        if ((code & 4) != 0) diff += step;
        if ((code & 2) != 0) diff += step >> 1;
        if ((code & 1) != 0) diff += step >> 2;
        predictor = Math.Clamp((code & 8) != 0 ? predictor - diff : predictor + diff, short.MinValue, short.MaxValue);
        index = Math.Clamp(index + ImaIndexAdjust[code], 0, ImaStep.Length - 1);
        return (short)predictor;
    }

    static void WsAdpcm(ReadOnlySpan<byte> input, byte[] output)
    {
        if (input.Length == output.Length)
        {
            input.CopyTo(output);
            return;
        }
        int sample = 0x80, r = 0, w = 0;
        static int Clamp(int v) => Math.Clamp(v, 0, 255);
        while (w < output.Length && r < input.Length)
        {
            int count = input[r] & 0x3F;
            switch (input[r++] >> 6)
            {
                case 0: // 2-bit deltas, four to a byte
                    for (count++; count > 0 && r < input.Length; count--)
                    {
                        int code = input[r++];
                        for (int shift = 0; shift < 8 && w < output.Length; shift += 2)
                            output[w++] = (byte)(sample = Clamp(sample + Ws2Bit[(code >> shift) & 3]));
                    }
                    break;
                case 1: // 4-bit deltas, two to a byte
                    for (count++; count > 0 && r < input.Length; count--)
                    {
                        int code = input[r++];
                        if (w < output.Length) output[w++] = (byte)(sample = Clamp(sample + Ws4Bit[code & 0xF]));
                        if (w < output.Length) output[w++] = (byte)(sample = Clamp(sample + Ws4Bit[code >> 4]));
                    }
                    break;
                case 2:
                    if ((count & 0x20) != 0) // one 5-bit signed delta
                        output[w++] = (byte)(sample = Clamp(sample + ((sbyte)(count << 3) >> 3)));
                    else // raw bytes
                    {
                        for (count++; count > 0 && r < input.Length && w < output.Length; count--)
                            output[w++] = input[r++];
                        sample = output[w - 1];
                    }
                    break;
                default: // the last sample again
                    for (count++; count > 0 && w < output.Length; count--)
                        output[w++] = (byte)sample;
                    break;
            }
        }
    }
}
