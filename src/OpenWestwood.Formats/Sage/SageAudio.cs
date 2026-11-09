using System.Text;

namespace OpenWestwood.Formats.Sage;

/// <summary>
/// EA's audio as Kane's Wrath stores it (an AudioFile's .cdata): an SNR header (byte 0 = version &lt;&lt; 4 | codec,
/// byte 1 = (channels - 1) &lt;&lt; 2, bytes 2-3 sample rate, then big-endian uint32 = type 2 bits | loop 1 bit | sample count
/// 29 bits, plus a loop start if looped), followed by SNS blocks (big-endian uint32 = last-block flag 0x80 in the top byte |
/// block size including this 8-byte header, then the block's sample count, then data). Streamed files keep the SNR header in
/// the AudioFile asset and their .cdata holds only the blocks.
/// Codecs: 4 = EA-XAS v1 (ADPCM, every sound effect and unit voice), 5 = EALayer3 v1 (MPEG layer III granules with EA
/// headers: music, ambience, campaign speech). XAS becomes a 16-bit PCM WAV; EALayer3 is rebuilt into standard MPEG frames
/// (an .mp3 any decoder plays).
/// </summary>
public static class SageAudio
{
    public const int CodecXas = 4, CodecEaLayer3 = 5;

    public sealed record Header(int Codec, int Channels, int SampleRate, int SampleCount, int Size);

    public static Header? ParseHeader(ReadOnlySpan<byte> d)
    {
        if (d.Length < 8)
            return null;
        int codec = d[0] & 0x0F, channels = (d[1] >> 2) + 1, rate = d[2] << 8 | d[3];
        uint word = (uint)(d[4] << 24 | d[5] << 16 | d[6] << 8 | d[7]);
        bool loop = (word >> 29 & 1) != 0;
        if (rate < 4000 || rate > 96000 || channels > 8)
            return null;
        return new Header(codec, channels, rate, (int)(word & 0x1FFFFFFF), loop ? 12 : 8);
    }

    /// <summary>The data of a run of SNS blocks, concatenated.</summary>
    static byte[] Blocks(ReadOnlySpan<byte> d)
    {
        var output = new MemoryStream();
        int at = 0;
        while (at + 8 <= d.Length)
        {
            int flags = d[at];
            int size = d[at + 1] << 16 | d[at + 2] << 8 | d[at + 3];
            if (size < 8 || at + size > d.Length)
            {
                output.Write(d[(at + 8)..]);
                break;
            }
            output.Write(d.Slice(at + 8, size - 8));
            at += size;
            if ((flags & 0x80) != 0)
                break;
        }
        return output.ToArray();
    }

    /// <summary>
    /// Decodes a .cdata (unpacked) into a playable file: a RIFF WAVE for XAS, MPEG audio for EALayer3, or null if the codec
    /// is something else. <paramref name="streamedHeader"/> is the SNR header from the asset for streamed files.
    /// </summary>
    public static (byte[] Data, bool IsMp3)? Decode(ReadOnlySpan<byte> cdata, ReadOnlySpan<byte> streamedHeader)
    {
        var header = ParseHeader(streamedHeader.Length >= 8 ? streamedHeader : cdata);
        if (header == null)
            return null;
        var body = Blocks(streamedHeader.Length >= 8 ? cdata : cdata[header.Size..]);
        return header.Codec switch
        {
            CodecXas => (Wav(DecodeXas(body, header), Math.Min(header.Channels, 2), header.SampleRate), false),
            CodecEaLayer3 => (EaLayer3ToMpeg(body), true),
            _ => null,
        };
    }

    // ---- EA-XAS v1 ----

    static readonly int[] XaTable = [0, 240, 460, 392, 0, 0, -208, -220, 0, 1, 3, 4, 7, 8, 10, 11, 0, -1, -3, -4];

    /// <summary>
    /// EA-XAS v1: frames of 0x4C bytes per channel (interleaved by frame) holding 128 samples: 4 groups of 32, each with a
    /// little-endian header pair (coefficient index | first sample, shift | second sample) and 15 rows of 4 bytes (one byte
    /// per group per row, two 4-bit deltas, high nibble first). More than two channels are folded into stereo.
    /// </summary>
    static short[] DecodeXas(byte[] data, Header h)
    {
        int channels = h.Channels;
        int frames = data.Length / (0x4C * channels);
        int total = Math.Min(h.SampleCount > 0 ? h.SampleCount : int.MaxValue, frames * 128);
        var decoded = new short[channels][];
        for (int c = 0; c < channels; c++)
            decoded[c] = new short[frames * 128];
        Span<int> coef1 = stackalloc int[4], coef2 = stackalloc int[4], shift = stackalloc int[4];
        for (int f = 0; f < frames; f++)
            for (int c = 0; c < channels; c++)
            {
                int at = (f * channels + c) * 0x4C;
                var s = decoded[c].AsSpan(f * 128, 128);
                for (int g = 0; g < 4; g++)
                {
                    int v0 = (short)(data[at + g * 4] | data[at + g * 4 + 1] << 8);
                    int v1 = (short)(data[at + g * 4 + 2] | data[at + g * 4 + 3] << 8);
                    coef1[g] = XaTable[v0 & 0x0F];
                    coef2[g] = XaTable[(v0 & 0x0F) + 4];
                    s[g * 32] = (short)(v0 & ~0x0F);
                    shift[g] = 20 - (v1 & 0x0F);
                    s[g * 32 + 1] = (short)(v1 & ~0x0F);
                }
                for (int row = 0; row < 15; row++)
                    for (int g = 0; g < 4; g++)
                    {
                        int b = data[at + 0x10 + row * 4 + g];
                        int k = g * 32 + 2 + row * 2;
                        for (int n = 0; n < 2; n++)
                        {
                            int nibble = n == 0 ? b >> 4 : b & 0x0F;
                            int level = (nibble << 28 >> 28) * (1 << shift[g]);
                            int pred = s[k + n - 1] * coef1[g] + s[k + n - 2] * coef2[g];
                            s[k + n] = (short)Math.Clamp((level + pred + 0x80) >> 8, short.MinValue, short.MaxValue);
                        }
                    }
            }
        int outChannels = Math.Min(channels, 2);
        var pcm = new short[total * outChannels];
        for (int i = 0; i < total; i++)
        {
            if (channels <= 2)
            {
                for (int c = 0; c < channels; c++)
                    pcm[i * outChannels + c] = decoded[c][i];
                continue;
            }
            // Quad (FL FR RL RR) or 5.1 (FL FR C LFE RL RR): fold into left and right.
            int left = 0, right = 0, nl = 0, nr = 0;
            for (int c = 0; c < channels; c++)
            {
                if (channels >= 6 && c == 3)
                    continue;
                bool center = channels >= 6 && c == 2;
                if (center || c % 2 == 0) { left += decoded[c][i]; nl++; }
                if (center || c % 2 == 1) { right += decoded[c][i]; nr++; }
            }
            pcm[i * 2] = (short)(left / Math.Max(1, nl));
            pcm[i * 2 + 1] = (short)(right / Math.Max(1, nr));
        }
        return pcm;
    }

    static byte[] Wav(short[] pcm, int channels, int rate)
    {
        var output = new MemoryStream(44 + pcm.Length * 2);
        using (var w = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true))
        {
            w.Write("RIFF"u8);
            w.Write(36 + pcm.Length * 2);
            w.Write("WAVEfmt "u8);
            w.Write(16);
            w.Write((short)1);
            w.Write((short)channels);
            w.Write(rate);
            w.Write(rate * channels * 2);
            w.Write((short)(channels * 2));
            w.Write((short)16);
            w.Write("data"u8);
            w.Write(pcm.Length * 2);
            foreach (short s in pcm)
                w.Write(s);
        }
        return output.ToArray();
    }

    // ---- EALayer3 ----

    sealed class BitReader(byte[] data)
    {
        public long Position; // in bits
        public long Length => data.LongLength * 8;

        public uint Read(int bits)
        {
            uint v = 0;
            for (int i = 0; i < bits; i++)
            {
                long p = Position++;
                int bit = p < Length ? data[p >> 3] >> (7 - (int)(p & 7)) & 1 : 0;
                v = v << 1 | (uint)bit;
            }
            return v;
        }
    }

    sealed class BitWriter
    {
        readonly List<byte> bytes = new();
        int bitCount;

        public void Write(uint value, int bits)
        {
            for (int i = bits - 1; i >= 0; i--)
            {
                if ((bitCount & 7) == 0)
                    bytes.Add(0);
                if ((value >> i & 1) != 0)
                    bytes[^1] |= (byte)(0x80 >> (bitCount & 7));
                bitCount++;
            }
        }

        public int ByteCount => bytes.Count;
        public void PadTo(int byteCount) { while (bytes.Count < byteCount) bytes.Add(0); bitCount = bytes.Count * 8; }
        public byte[] ToArray() => bytes.ToArray();
    }

    /// <summary>One EA frame: an MPEG header's fields, one granule's side info and where its main data is.</summary>
    sealed class EaFrame
    {
        public int VersionIndex, RateIndex, ChannelMode, ModeExtension, Granule, Channels;
        public bool Mpeg1;
        public uint[] Scfsi = new uint[2], DataBits = new uint[2], Side1 = new uint[2], Side2 = new uint[2];
        public long DataStart;
    }

    static readonly int[] Mpeg1Rates = [44100, 48000, 32000], Mpeg2Rates = [22050, 24000, 16000], Mpeg25Rates = [11025, 12000, 8000];
    static readonly int[] Mpeg1Bitrates = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];
    static readonly int[] Mpeg2Bitrates = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160];

    /// <summary>Reads one EALayer3 v1 frame (as in SNS streams); null at the end or on bad data.</summary>
    static EaFrame? ReadFrame(BitReader r)
    {
        if (r.Position + 16 > r.Length)
            return null;
        uint pcmFlag = r.Read(8);
        if (pcmFlag != 0x00 && pcmFlag != 0xEE)
            return null;
        long start = r.Position;
        var f = new EaFrame
        {
            VersionIndex = (int)r.Read(2), RateIndex = (int)r.Read(2), ChannelMode = (int)r.Read(2), ModeExtension = (int)r.Read(2),
        };
        if (f.VersionIndex == 1 || f.RateIndex == 3 || f.VersionIndex == 0 && f.RateIndex == 0 && f.ChannelMode == 0 && f.ModeExtension == 0)
            return null;
        f.Mpeg1 = f.VersionIndex == 3;
        f.Channels = f.ChannelMode == 3 ? 1 : 2;
        f.Granule = (int)r.Read(1);
        if (f.Mpeg1 && f.Granule == 1)
            for (int c = 0; c < f.Channels; c++)
                f.Scfsi[c] = r.Read(4);
        for (int c = 0; c < f.Channels; c++)
        {
            f.DataBits[c] = r.Read(12);
            f.Side1[c] = r.Read(32);
            f.Side2[c] = r.Read(f.Mpeg1 ? 15 : 19);
        }
        f.DataStart = r.Position;
        long bits = r.Position - start + f.DataBits[0] + (f.Channels > 1 ? f.DataBits[1] : 0);
        r.Position = start + (bits + 7) / 8 * 8;
        if (pcmFlag == 0xEE)
        {
            r.Read(16); // samples to discard
            uint pcmSamples = r.Read(16);
            r.Read(32);
            r.Position += pcmSamples * 2 * f.Channels * 8; // (a few PCM samples that start the stream; skipped)
        }
        return r.Position <= r.Length ? f : null;
    }

    /// <summary>Rebuilds EALayer3 v1 frames into MPEG layer III frames (no bit reservoir, smallest bitrate that fits).</summary>
    public static byte[] EaLayer3ToMpeg(byte[] data)
    {
        var r = new BitReader(data);
        var output = new MemoryStream();
        var frames = new List<EaFrame>();
        while (ReadFrame(r) is { } f)
            frames.Add(f);
        for (int i = 0; i < frames.Count; i++)
        {
            var f0 = frames[i];
            EaFrame? f1 = null;
            if (f0.Mpeg1)
            {
                if (f0.Granule != 0 || i + 1 >= frames.Count || frames[i + 1].Granule != 1 || frames[i + 1].Channels != f0.Channels)
                    continue;
                f1 = frames[++i];
            }
            WriteMpegFrame(r, f0, f1, output);
        }
        return output.ToArray();
    }

    static void WriteMpegFrame(BitReader r, EaFrame f0, EaFrame? f1, Stream output)
    {
        int channels = f0.Channels;
        int sideBytes = f0.Mpeg1 ? (channels == 1 ? 17 : 32) : (channels == 1 ? 9 : 17);
        long dataBits = 0;
        foreach (var f in f1 == null ? [f0] : new[] { f0, f1 })
            for (int c = 0; c < channels; c++)
                dataBits += f.DataBits[c];
        int needed = 4 + sideBytes + (int)((dataBits + 7) / 8);
        int rate = (f0.VersionIndex switch { 3 => Mpeg1Rates, 2 => Mpeg2Rates, _ => Mpeg25Rates })[f0.RateIndex];
        var bitrates = f0.Mpeg1 ? Mpeg1Bitrates : Mpeg2Bitrates;
        int coefficient = f0.Mpeg1 ? 144 : 72;
        int bitrateIndex = 14, frameSize = coefficient * bitrates[14] * 1000 / rate;
        for (int b = 1; b <= 14; b++)
        {
            int size = coefficient * bitrates[b] * 1000 / rate;
            if (size >= needed)
            {
                bitrateIndex = b;
                frameSize = size;
                break;
            }
        }
        if (frameSize < needed)
            return; // (cannot happen for real data: more than 320 kbps)

        var w = new BitWriter();
        w.Write(0x7FF, 11);
        w.Write((uint)f0.VersionIndex, 2);
        w.Write(1, 2); // layer III
        w.Write(1, 1); // no CRC
        w.Write((uint)bitrateIndex, 4);
        w.Write((uint)f0.RateIndex, 2);
        w.Write(0, 1); // padding
        w.Write(0, 1);
        w.Write((uint)f0.ChannelMode, 2);
        w.Write((uint)f0.ModeExtension, 2);
        w.Write(0, 1);
        w.Write(1, 1);
        w.Write(0, 2);
        if (f0.Mpeg1)
        {
            w.Write(0, 9); // main_data_begin
            w.Write(0, channels == 1 ? 5 : 3);
            for (int c = 0; c < channels; c++)
                w.Write(f1!.Scfsi[c], 4);
            foreach (var f in new[] { f0, f1! })
                for (int c = 0; c < channels; c++)
                {
                    w.Write(f.DataBits[c], 12);
                    w.Write(f.Side1[c], 32);
                    w.Write(f.Side2[c], 15);
                }
        }
        else
        {
            w.Write(0, 8);
            w.Write(0, channels == 1 ? 1 : 2);
            for (int c = 0; c < channels; c++)
            {
                w.Write(f0.DataBits[c], 12);
                w.Write(f0.Side1[c], 32);
                w.Write(f0.Side2[c], 19);
            }
        }
        foreach (var f in f1 == null ? [f0] : new[] { f0, f1 })
        {
            r.Position = f.DataStart;
            for (int c = 0; c < channels; c++)
                for (uint left = f.DataBits[c]; left > 0;)
                {
                    int n = (int)Math.Min(left, 32u);
                    w.Write(r.Read(n), n);
                    left -= (uint)n;
                }
        }
        w.PadTo(frameSize);
        output.Write(w.ToArray());
    }
}
