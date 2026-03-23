using System.IO;
using CanLinConfig.Models;
using CanLinConfig.Parsers;

namespace CanLinConfig.Services;

public class DatabaseManager
{
    private readonly object _lock = new();
    private readonly Dictionary<BusFrame.Bus, DbcFile> _databases = new();
    private readonly Dictionary<BusFrame.Bus, string> _dbPaths = new();
    private readonly Dictionary<(BusFrame.Bus, uint), IReadOnlyList<DbcSignal>> _signalCache = new();
    private readonly Dictionary<(BusFrame.Bus, uint), string> _nameCache = new();

    public void AssignDatabase(BusFrame.Bus bus, string filePath)
    {
        if (string.Equals(Path.GetExtension(filePath), ".ldf", StringComparison.OrdinalIgnoreCase))
        {
            AssignLdfDatabase(bus, filePath);
        }
        else
        {
            var dbc = DbcParser.Parse(filePath);
            lock (_lock)
            {
                _databases[bus] = dbc;
                _dbPaths[bus] = filePath;
                RebuildCache(bus, dbc);
            }
        }
    }

    public void RemoveDatabase(BusFrame.Bus bus)
    {
        lock (_lock)
        {
            _databases.Remove(bus);
            _dbPaths.Remove(bus);
            ClearCache(bus);
        }
    }

    public string? GetDatabasePath(BusFrame.Bus bus)
    {
        lock (_lock) return _dbPaths.GetValueOrDefault(bus);
    }

    public IReadOnlyList<DbcSignal> GetSignals(BusFrame.Bus bus, uint messageId)
    {
        lock (_lock) return _signalCache.GetValueOrDefault((bus, messageId), []);
    }

    public string? GetMessageName(BusFrame.Bus bus, uint messageId)
    {
        lock (_lock) return _nameCache.GetValueOrDefault((bus, messageId));
    }

    public IReadOnlyList<SignalValue> DecodeFrame(BusFrame frame)
    {
        var signals = GetSignals(frame.SourceBus, frame.Id);
        if (signals.Count == 0) return [];

        var values = new List<SignalValue>(signals.Count);
        foreach (var sig in signals)
        {
            double raw = SignalExtractor.ExtractRaw(frame.Data, sig);
            double physical = raw * sig.Factor + sig.Offset;
            values.Add(new SignalValue(
                sig.Name, raw, physical, sig.Unit,
                (byte)frame.SourceBus, frame.Id, frame.Timestamp));
        }
        return values;
    }

    public IReadOnlyList<(uint Id, string Name)> GetMessages(BusFrame.Bus bus)
    {
        lock (_lock)
        {
            if (!_databases.TryGetValue(bus, out var dbc)) return [];
            return dbc.Messages.Select(m => (m.Id, m.Name)).ToList();
        }
    }

    public IReadOnlyDictionary<BusFrame.Bus, string> GetAssignments()
    {
        lock (_lock) return new Dictionary<BusFrame.Bus, string>(_dbPaths);
    }

    private void AssignLdfDatabase(BusFrame.Bus bus, string filePath)
    {
        var ldf = LdfParser.Parse(filePath);
        lock (_lock)
        {
            _dbPaths[bus] = filePath;
            RebuildCacheFromLdf(bus, ldf);
        }
    }

    private void RebuildCacheFromLdf(BusFrame.Bus bus, LdfFile ldf)
    {
        ClearCache(bus);
        foreach (var frame in ldf.Frames)
        {
            var signals = new List<DbcSignal>(frame.Signals.Count);
            foreach (var frameSig in frame.Signals)
            {
                var sigDef = LdfParser.GetSignal(ldf, frameSig.Name);
                int bitLength = sigDef?.BitSize ?? 8;

                double factor = 1.0;
                double offset = 0.0;
                string unit = "";

                var encoding = LdfParser.GetEncodingForSignal(ldf, frameSig.Name);
                if (encoding != null)
                {
                    var physVal = encoding.Values.FirstOrDefault(v => v.IsPhysical);
                    if (physVal != null)
                    {
                        factor = physVal.Factor;
                        offset = physVal.Offset;
                        unit = physVal.Description;
                    }
                }

                signals.Add(new DbcSignal
                {
                    Name = frameSig.Name,
                    StartBit = frameSig.BitOffset,
                    BitLength = bitLength,
                    IsLittleEndian = true,
                    IsSigned = false,
                    Factor = factor,
                    Offset = offset,
                    Unit = unit,
                });
            }

            _signalCache[(bus, (uint)frame.Id)] = signals;
            _nameCache[(bus, (uint)frame.Id)] = frame.Name;
        }
    }

    private void RebuildCache(BusFrame.Bus bus, DbcFile dbc)
    {
        ClearCache(bus);
        foreach (var msg in dbc.Messages)
        {
            _signalCache[(bus, msg.Id)] = msg.Signals;
            _nameCache[(bus, msg.Id)] = msg.Name;
        }
    }

    private void ClearCache(BusFrame.Bus bus)
    {
        var keysToRemove = _signalCache.Keys.Where(k => k.Item1 == bus).ToList();
        foreach (var key in keysToRemove)
        {
            _signalCache.Remove(key);
            _nameCache.Remove(key);
        }
    }
}
