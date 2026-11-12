using OpenWestwood.Core.FileSystem;
using OpenWestwood.Formats.Audio;
using Silk.NET.OpenAL;

namespace OpenWestwood.Client.Audio;

/// <summary>
/// Speech and music played as a stream: WAV speech is decoded whole into one buffer; MP3 music is
/// decoded a little at a time into a queue of buffers, so long tracks never sit in memory decoded.
/// The MP3 is opened and decoded on a worker thread, half a second ahead: opening a track (the decoder reads through
/// the whole file) and decoding it on the game's thread stalled the game for a tenth of a second each time music
/// started.
/// </summary>
public sealed unsafe class MusicStream
{
    const int BufferCount = 4;
    const int SamplesPerBuffer = 44100 / 2 * 2; // about half a second of stereo

    readonly byte[] data;
    readonly bool isMp3;
    PcmSound? wav;
    readonly uint[] buffers = new uint[BufferCount];
    readonly Queue<uint> freeBuffers = new();
    bool endOfData;
    bool buffersCreated;

    // The worker's side: the decoder (only touched by the one decoding task running at a time) and its format.
    NLayer.MpegFile? mp3;
    int channels = 2, sampleRate = 44100;
    /// <summary>The next half second being decoded (null samples: the end of the track).</summary>
    Task<short[]?>? decoding;

    MusicStream(byte[] data, bool isMp3)
    {
        this.data = data;
        this.isMp3 = isMp3;
        if (!isMp3)
            wav = WavFile.Decode(data);
    }

    public static MusicStream? Open(VirtualFileSystem files, string path)
    {
        if (!files.Exists(path))
            return null;
        var bytes = files.ReadAllBytes(path);
        bool isMp3 = path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) || !(bytes.Length > 4 && bytes[0] == 'R' && bytes[1] == 'I');
        var stream = new MusicStream(bytes, isMp3);
        if (isMp3)
            stream.StartDecoding();
        return stream;
    }

    void StartDecoding() => decoding = Task.Run(DecodeNext);

    short[]? DecodeNext()
    {
        mp3 ??= new NLayer.MpegFile(new MemoryStream(data, writable: false));
        channels = mp3.Channels;
        sampleRate = mp3.SampleRate;
        var floats = new float[SamplesPerBuffer];
        int read = mp3.ReadSamples(floats, 0, floats.Length);
        if (read <= 0)
            return null;
        var samples = new short[read];
        for (int i = 0; i < read; i++)
            samples[i] = (short)Math.Clamp((int)(floats[i] * 32767f), short.MinValue, short.MaxValue);
        return samples;
    }

    public void Start(AL al, uint source)
    {
        if (!buffersCreated)
        {
            for (int i = 0; i < BufferCount; i++)
                buffers[i] = al.GenBuffer();
            buffersCreated = true;
        }
        freeBuffers.Clear();
        foreach (uint b in buffers)
            freeBuffers.Enqueue(b);
        if (wav != null)
        {
            var format = wav.Channels == 2 ? BufferFormat.Stereo16 : BufferFormat.Mono16;
            uint b = freeBuffers.Dequeue();
            fixed (short* p = wav.Samples)
                al.BufferData(b, format, p, wav.Samples.Length * sizeof(short), wav.SampleRate);
            al.SourceQueueBuffers(source, 1, &b);
            endOfData = true;
            al.SourcePlay(source);
            return;
        }
        // (The first half second plays as soon as the worker has it; Pump queues it.)
        Pump(al, source);
    }

    /// <summary>The next decoded half second, if the worker has it ready (and the worker set on the one after).</summary>
    short[]? TakeDecoded()
    {
        if (endOfData || decoding == null || !decoding.IsCompleted)
            return null;
        short[]? samples;
        try
        {
            samples = decoding.Result;
        }
        catch (AggregateException e)
        {
            Console.Error.WriteLine($"Music: {e.InnerException?.Message}");
            samples = null;
        }
        if (samples == null)
        {
            decoding = null;
            endOfData = true;
            return null;
        }
        StartDecoding();
        return samples;
    }

    /// <summary>Refills buffers the source has finished with; restarts the source if it ran dry.</summary>
    public void Pump(AL al, uint source)
    {
        if (al == null || !isMp3)
            return;
        al.GetSourceProperty(source, GetSourceInteger.BuffersProcessed, out int processed);
        while (processed-- > 0)
        {
            uint b;
            al.SourceUnqueueBuffers(source, 1, &b);
            freeBuffers.Enqueue(b);
        }
        while (freeBuffers.Count > 0 && TakeDecoded() is { } samples)
        {
            uint b = freeBuffers.Dequeue();
            var format = channels == 2 ? BufferFormat.Stereo16 : BufferFormat.Mono16;
            fixed (short* p = samples)
                al.BufferData(b, format, p, samples.Length * sizeof(short), sampleRate);
            al.SourceQueueBuffers(source, 1, &b);
        }
        al.GetSourceProperty(source, GetSourceInteger.SourceState, out int state);
        al.GetSourceProperty(source, GetSourceInteger.BuffersQueued, out int queued);
        if (state != (int)SourceState.Playing && queued > 0)
            al.SourcePlay(source);
    }

    /// <summary>All data played and nothing left queued.</summary>
    public bool IsFinished(AL? al, uint source)
    {
        if (al == null)
            return true;
        al.GetSourceProperty(source, GetSourceInteger.SourceState, out int state);
        if (state == (int)SourceState.Playing)
            return false;
        al.GetSourceProperty(source, GetSourceInteger.BuffersQueued, out int queued);
        al.GetSourceProperty(source, GetSourceInteger.BuffersProcessed, out int processed);
        return endOfData && queued - processed <= 0;
    }

    /// <summary>Plays again from the start (music loops forever).</summary>
    public void Restart(AL? al, uint source)
    {
        if (al == null)
            return;
        al.SourceStop(source);
        al.SetSourceProperty(source, SourceInteger.Buffer, 0);
        endOfData = false;
        if (isMp3)
        {
            // (Let the worker finish its half second, then start the decoder again from the top.)
            WaitForWorker();
            mp3?.Dispose();
            mp3 = null;
            StartDecoding();
        }
        Start(al, source);
    }

    void WaitForWorker()
    {
        try
        {
            decoding?.Wait();
        }
        catch (AggregateException) { }
        decoding = null;
    }

    public void Dispose(AL al, uint source)
    {
        al.SourceStop(source);
        al.SetSourceProperty(source, SourceInteger.Buffer, 0);
        if (buffersCreated)
            foreach (uint b in buffers)
                al.DeleteBuffer(b);
        buffersCreated = false;
        WaitForWorker();
        mp3?.Dispose();
        mp3 = null;
    }
}
