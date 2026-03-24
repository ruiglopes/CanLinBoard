using System;
using System.Collections.Generic;
using CanBus;

namespace CanBus.Adapters;

public static class CanAdapterRegistry
{
    public static ICanAdapter[] DetectAvailableAdapters()
    {
        var list = new List<ICanAdapter>();
        TryAdd<PcanService>(list);
        TryAdd<VectorService>(list);
        TryAdd<KvaserService>(list);
        TryAdd<SlcanService>(list);
        return list.ToArray();
    }

    private static void TryAdd<T>(List<ICanAdapter> list) where T : ICanAdapter, new()
    {
        try { list.Add(new T()); }
        catch (Exception) { /* DLL missing, init failed, wrong arch, etc. */ }
    }
}
