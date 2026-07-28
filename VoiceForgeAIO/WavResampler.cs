using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace VoiceForgeAIO;

/// <summary>
/// Produces a 16kHz, 16-bit, mono copy of a WAV file -- what FaceFXWrapper actually
/// expects for lip-sync analysis, per its own README ("FaceFXWrapper expects a 16khz
/// 16 bit-per-sample single-channel voice file"). TTS providers export at whatever
/// rate they like (Cartesia at 44.1kHz, MiniMax at 32kHz, etc.), and FaceFXWrapper's
/// own built-in resampling doesn't reliably handle all of those inputs -- so instead
/// of relying on it, we resample ourselves and hand FaceFXWrapper an
/// already-correct file, skipping its internal resample step entirely.
///
/// This only ever touches a throwaway temp copy used for LIP analysis -- the actual
/// WAV that ships with the mod (and gets encoded to XWM for FUZ) keeps its original
/// quality untouched.
/// </summary>
internal static class WavResampler
{
    public static void ResampleTo16kHzMono(string inputPath, string outputPath)
    {
        using var reader = new AudioFileReader(inputPath);
        ISampleProvider source = reader;

        if (reader.WaveFormat.Channels > 1)
            source = new StereoToMonoSampleProvider(reader) { LeftVolume = 0.5f, RightVolume = 0.5f };

        if (reader.WaveFormat.SampleRate != 16000)
            source = new WdlResamplingSampleProvider(source, 16000);

        WaveFileWriter.CreateWaveFile16(outputPath, source);
    }
}
