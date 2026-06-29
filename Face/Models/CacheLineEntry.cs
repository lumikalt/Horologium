using Orrery.Cache;

namespace Face.Models;

public sealed class CacheLineEntry {
    public string Set { get; }
    public string Way { get; }
    public string Valid { get; }
    public string Tag { get; }
    public string Lru { get; }
    public string Bytes { get; }
    public bool IsLastAccessed { get; }

    public CacheLineEntry(CacheLine line, bool isLastAccessed = false) {
        Set = line.Set.ToString();
        Way = line.Way.ToString();
        Valid = line.Valid ? "●" : "○";
        Tag = line.Valid ? $"0x{line.Tag:X}" : "-";
        Lru = line.Valid ? line.LruAge.ToString() : "-";
        IsLastAccessed = isLastAccessed;

        if (!line.Valid) { Bytes = "-"; }
        else {
            // Show block as little-endian 32-bit words
            byte[] b = line.Block;
            int wordCount = b.Length / 4;
            var parts = new string[wordCount];
            for (var i = 0; i < wordCount; i++) {
                var w = (uint)(b[i * 4] | (b[i * 4 + 1] << 8) | (b[i * 4 + 2] << 16) | (b[i * 4 + 3] << 24));
                parts[i] = $"0x{w:X8}";
            }

            Bytes = string.Join("  ", parts);
        }
    }
}