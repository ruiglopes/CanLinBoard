using CanLinConfig.Adapters;
using CanLinConfig.Models;

namespace CanLinConfig.Services;

public class BusDataService
{
    private readonly DatabaseManager _dbManager;
    private readonly int _maxHistory;
    private readonly List<BusFrame> _history;
    private readonly object _historyLock = new();

    public event EventHandler<BusFrame>? FrameReceived;
    public event EventHandler<IReadOnlyList<SignalValue>>? SignalsDecoded;

    public BusDataService(DatabaseManager dbManager, int maxHistory = 50_000)
    {
        _dbManager = dbManager;
        _maxHistory = maxHistory;
        _history = new List<BusFrame>(Math.Min(maxHistory, 1024));
    }

    public IReadOnlyList<BusFrame> FrameHistory
    {
        get { lock (_historyLock) return _history.ToList(); }
    }

    public void OnFrame(BusFrame frame)
    {
        lock (_historyLock)
        {
            _history.Add(frame);
            while (_history.Count > _maxHistory)
                _history.RemoveAt(0);
        }
        FrameReceived?.Invoke(this, frame);
        var signals = _dbManager.DecodeFrame(frame);
        if (signals.Count > 0)
            SignalsDecoded?.Invoke(this, signals);
    }

    public void OnCanFrame(CanFrame frame)
    {
        OnFrame(new BusFrame(frame, BusFrame.Bus.CAN1));
    }

    public void ClearHistory()
    {
        lock (_historyLock) _history.Clear();
    }

    public DatabaseManager DatabaseManager => _dbManager;
}
