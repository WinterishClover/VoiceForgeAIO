using System.Text;

namespace VoiceForgeAIO;

/// <summary>
/// Some WAV producers write the RIFF/data chunk size fields as a placeholder
/// (commonly 0xFFFFFFFF) when streaming output without seeking back to patch in
/// the real final size once writing completes. Lenient readers (NAudio, most media
/// players) ignore the declared size and just read to EOF, but stricter tools like
/// xWMAEncode take it literally and fail outright ("File data size (4294967295) not
/// divisible by frame size").
///
/// This rewrites those two size fields to match the array's actual length,
/// regardless of which provider produced the bytes -- cheap insurance against any
/// provider (now or in the future) handing back a technically-malformed header.
/// </summary>
internal static class WavHeaderFixup
{
    public static byte[] NormalizeHeader(byte[] wav)
    {
        if (wav.Length < 12) return wav; // too short to plausibly be a WAV; leave alone
        if (wav[0] != 'R' || wav[1] != 'I' || wav[2] != 'F' || wav[3] != 'F') return wav;
        if (wav[8] != 'W' || wav[9] != 'A' || wav[10] != 'V' || wav[11] != 'E') return wav;

        int pos = 12;
        while (pos + 8 <= wav.Length)
        {
            string chunkId = Encoding.ASCII.GetString(wav, pos, 4);
            uint declaredSize = BitConverter.ToUInt32(wav, pos + 4);
            int chunkDataStart = pos + 8;

            if (chunkId == "data")
            {
                long actualDataSize = Math.Max(0, wav.Length - chunkDataStart);
                var sizeBytes = BitConverter.GetBytes((uint)actualDataSize);
                Array.Copy(sizeBytes, 0, wav, pos + 4, 4);
                break; // "data" is always the last chunk we care about
            }

            // Non-data chunks (fmt, fact, LIST, etc.) are written with their real
            // size known upfront, so it's safe to trust declaredSize to skip past
            // them while walking toward "data". Chunks are word-aligned: an odd
            // size has one padding byte after it.
            long advance = declaredSize + (declaredSize % 2 == 1 ? 1 : 0);
            int next = (int)(chunkDataStart + advance);
            if (advance < 0 || advance > int.MaxValue || next <= chunkDataStart) break; // corrupt/garbage guard
            pos = next;
        }

        // Patch the top-level RIFF size field (bytes 4-7): total file size minus 8.
        var riffSizeBytes = BitConverter.GetBytes((uint)(wav.Length - 8));
        Array.Copy(riffSizeBytes, 0, wav, 4, 4);

        return wav;
    }
}
