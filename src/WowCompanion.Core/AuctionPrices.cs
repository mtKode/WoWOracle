using System.Globalization;
using System.Text;

namespace WowCompanion.Core;

/// <summary>An item's auction price from Auctionator's last scan of it: the lowest buyout per item, in copper.</summary>
/// <param name="Day">Auctionator's scan day (days since 1 January 2020).</param>
/// <param name="Available">How many were up for sale that day (0 when not recorded).</param>
public sealed record AuctionPrice(int ItemId, long Copper, int Day, int Available)
{
    public DateTime Date => AuctionPriceBook.DayZero.AddDays(Day);
}

/// <summary>The prices of one realm (Auctionator keeps one database per realm group).</summary>
public sealed class AuctionPriceBook(string realm, IReadOnlyDictionary<int, AuctionPrice> prices, IReadOnlyList<string> realms)
{
    /// <summary>Auctionator's day 0.</summary>
    public static readonly DateTime DayZero = new(2020, 1, 1);

    public string Realm { get; } = realm;
    public IReadOnlyDictionary<int, AuctionPrice> Prices { get; } = prices;
    /// <summary>Every realm with prices, newest scan first.</summary>
    public IReadOnlyList<string> Realms { get; } = realms;
    public int LatestDay { get; } = prices.Count == 0 ? 0 : prices.Values.Max(p => p.Day);

    public AuctionPrice? Get(int itemId) => Prices.GetValueOrDefault(itemId);

    public static int Today => (DateTime.Now.Date - DayZero).Days;

    /// <summary>"today", "yesterday", "3 days ago".</summary>
    public static string Age(int day) => (Today - day) switch
    {
        <= 0 => "today",
        1 => "yesterday",
        var n => $"{n} days ago",
    };
}

/// <summary>
/// Reads the Auctionator addon's saved prices (WTF\Account\*\SavedVariables\Auctionator.lua), when it is installed.
/// AUCTIONATOR_PRICE_DATABASE holds one Lua string per realm, each a CBOR map: item key → { m = lowest price at the
/// last scan, h / l = high / low per scan day, a = how many were up per day }. Only plain item ids are read.
/// </summary>
public static class AuctionatorData
{
    public static List<string> FindFiles(string appRoot)
    {
        var files = new List<string>();
        foreach (var root in WowInstall.Roots(appRoot))
        {
            try
            {
                foreach (var client in WowInstall.ForeverClients(root))
                {
                    var accounts = Path.Combine(client, "WTF", "Account");
                    if (!Directory.Exists(accounts)) continue;
                    foreach (var account in Directory.EnumerateDirectories(accounts))
                    {
                        var file = Path.Combine(account, "SavedVariables", "Auctionator.lua");
                        if (File.Exists(file)) files.Add(file);
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return files;
    }

    /// <summary>
    /// The prices of the chosen realm (or the most recently scanned one) across every Auctionator file found;
    /// null when there are none.
    /// </summary>
    public static AuctionPriceBook? Load(string appRoot, string? preferredRealm)
    {
        var realms = new Dictionary<string, Dictionary<int, AuctionPrice>>(StringComparer.Ordinal);
        foreach (var file in FindFiles(appRoot))
        {
            try
            {
                foreach (var (realm, prices) in Read(File.ReadAllBytes(file)))
                {
                    if (!realms.TryGetValue(realm, out var merged)) realms[realm] = merged = [];
                    foreach (var p in prices.Values)
                        if (!merged.TryGetValue(p.ItemId, out var have) || p.Day > have.Day) merged[p.ItemId] = p;
                }
            }
            catch (Exception)
            {
                // A file mid-write or from another Auctionator version: skip it.
            }
        }
        var ordered = realms.Where(r => r.Value.Count > 0)
            .OrderByDescending(r => r.Value.Values.Max(p => p.Day)).ThenByDescending(r => r.Value.Count)
            .Select(r => r.Key).ToList();
        if (ordered.Count == 0) return null;
        var pick = preferredRealm is not null && realms.ContainsKey(preferredRealm) ? preferredRealm : ordered[0];
        return new AuctionPriceBook(pick, realms[pick], ordered);
    }

    /// <summary>Realm → prices from one Auctionator.lua file's bytes.</summary>
    public static Dictionary<string, Dictionary<int, AuctionPrice>> Read(byte[] file)
    {
        var result = new Dictionary<string, Dictionary<int, AuctionPrice>>(StringComparer.Ordinal);
        var start = IndexOf(file, "AUCTIONATOR_PRICE_DATABASE = {"u8, 0);
        if (start < 0) return result;
        var i = start + "AUCTIONATOR_PRICE_DATABASE = {"u8.Length;
        // Entries: ["key"] = "value", or ["__dbversion"] = 8, until the closing brace at the start of a line.
        while (i < file.Length)
        {
            while (i < file.Length && file[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)',') i++;
            if (i >= file.Length || file[i] == (byte)'}') break;
            if (file[i] != (byte)'[') break;
            var (key, afterKey) = LuaString(file, i + 1);
            i = afterKey;
            while (i < file.Length && file[i] != (byte)'=') i++;
            i++;
            while (i < file.Length && file[i] == (byte)' ') i++;
            if (i < file.Length && file[i] == (byte)'"')
            {
                var (value, after) = LuaString(file, i);
                i = after;
                var realm = Encoding.UTF8.GetString(key);
                if (realm.StartsWith("__", StringComparison.Ordinal)) continue;
                try { result[realm] = Decode(value); }
                catch (Exception) { } // not the CBOR this reader knows
            }
            else
            {
                while (i < file.Length && file[i] != (byte)'\n') i++; // a number (__dbversion) or an older table format
            }
        }
        return result;
    }

    private static Dictionary<int, AuctionPrice> Decode(byte[] cbor)
    {
        var prices = new Dictionary<int, AuctionPrice>();
        var pos = 0;
        if (Cbor.Read(cbor, ref pos) is not Dictionary<string, object?> items) return prices;
        foreach (var (key, value) in items)
        {
            if (!int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var itemId) || itemId <= 0) continue;
            if (value is not Dictionary<string, object?> entry) continue;
            if (entry.GetValueOrDefault("m") is not long min || min <= 0) continue;
            var days = DayMap(entry.GetValueOrDefault("h"));
            var day = days.Count > 0 ? days.Keys.Max() : 0;
            var available = DayMap(entry.GetValueOrDefault("a")).GetValueOrDefault(day);
            prices[itemId] = new AuctionPrice(itemId, min, day, (int)available);
        }
        return prices;
    }

    private static Dictionary<int, long> DayMap(object? value)
    {
        var map = new Dictionary<int, long>();
        if (value is Dictionary<string, object?> d)
            foreach (var (k, v) in d)
                if (int.TryParse(k, NumberStyles.None, CultureInfo.InvariantCulture, out var day) && v is long n) map[day] = n;
        return map;
    }

    /// <summary>A Lua string literal starting at the opening quote: its bytes, and the index after the closing quote.</summary>
    private static (byte[] Value, int After) LuaString(byte[] b, int quote)
    {
        var bytes = new List<byte>();
        var i = quote + 1;
        while (i < b.Length && b[i] != (byte)'"')
        {
            if (b[i] != (byte)'\\') { bytes.Add(b[i++]); continue; }
            var n = i + 1 < b.Length ? b[i + 1] : (byte)0;
            if (n is >= (byte)'0' and <= (byte)'9')
            {
                var j = i + 1;
                var value = 0;
                while (j < b.Length && j < i + 4 && b[j] is >= (byte)'0' and <= (byte)'9') value = value * 10 + (b[j++] - '0');
                bytes.Add((byte)value);
                i = j;
                continue;
            }
            switch (n)
            {
                case (byte)'n': bytes.Add(10); break;
                case (byte)'r': bytes.Add(13); break;
                case (byte)'t': bytes.Add(9); break;
                case (byte)'a': bytes.Add(7); break;
                case (byte)'b': bytes.Add(8); break;
                case (byte)'f': bytes.Add(12); break;
                case (byte)'v': bytes.Add(11); break;
                case (byte)'\r': // backslash before a real line break: a newline in the string
                    bytes.Add(10);
                    if (i + 2 < b.Length && b[i + 2] == (byte)'\n') i++;
                    break;
                default: bytes.Add(n); break; // \\, \", \', \ + newline
            }
            i += 2;
        }
        return (bytes.ToArray(), i + 1);
    }

    private static int IndexOf(byte[] haystack, ReadOnlySpan<byte> needle, int from)
    {
        var at = haystack.AsSpan(from).IndexOf(needle);
        return at < 0 ? -1 : at + from;
    }

    /// <summary>Just enough CBOR for Auctionator: integers, byte/text strings, arrays, maps (keys as text), floats, simple values.</summary>
    private static class Cbor
    {
        public static object? Read(byte[] b, ref int pos)
        {
            var head = b[pos++];
            var major = head >> 5;
            var info = head & 31;
            switch (major)
            {
                case 0: return (long)Argument(b, ref pos, info);
                case 1: return -1 - (long)Argument(b, ref pos, info);
                case 2:
                case 3:
                {
                    var length = (int)Argument(b, ref pos, info);
                    var text = Encoding.UTF8.GetString(b, pos, length);
                    pos += length;
                    return text;
                }
                case 4:
                {
                    var list = new List<object?>();
                    if (info == 31) { while (b[pos] != 0xFF) list.Add(Read(b, ref pos)); pos++; return list; }
                    var count = (int)Argument(b, ref pos, info);
                    for (var k = 0; k < count; k++) list.Add(Read(b, ref pos));
                    return list;
                }
                case 5:
                {
                    var map = new Dictionary<string, object?>(StringComparer.Ordinal);
                    if (info == 31)
                    {
                        while (b[pos] != 0xFF) { var key = Key(Read(b, ref pos)); map[key] = Read(b, ref pos); }
                        pos++;
                        return map;
                    }
                    var count = (int)Argument(b, ref pos, info);
                    for (var k = 0; k < count; k++) { var key = Key(Read(b, ref pos)); map[key] = Read(b, ref pos); }
                    return map;
                }
                case 6: return Read(b, ref pos); // tag: the value it wraps
                default:
                    switch (info)
                    {
                        case 20: return false;
                        case 21: return true;
                        case 22: case 23: return null;
                        case 25: pos += 2; return null; // half float: not used for prices
                        case 26: { var v = BitConverter.Int32BitsToSingle((int)Big(b, ref pos, 4)); return (double)v; }
                        case 27: return BitConverter.Int64BitsToDouble((long)Big(b, ref pos, 8));
                        default: throw new FormatException($"CBOR simple value {info}");
                    }
            }
        }

        private static string Key(object? key) => key switch
        {
            string s => s,
            long n => n.ToString(CultureInfo.InvariantCulture),
            _ => Convert.ToString(key, CultureInfo.InvariantCulture) ?? "",
        };

        private static ulong Argument(byte[] b, ref int pos, int info) => info switch
        {
            < 24 => (ulong)info,
            24 => Big(b, ref pos, 1),
            25 => Big(b, ref pos, 2),
            26 => Big(b, ref pos, 4),
            27 => Big(b, ref pos, 8),
            _ => throw new FormatException($"CBOR length {info}"),
        };

        private static ulong Big(byte[] b, ref int pos, int bytes)
        {
            ulong v = 0;
            for (var k = 0; k < bytes; k++) v = v << 8 | b[pos++];
            return v;
        }
    }
}
