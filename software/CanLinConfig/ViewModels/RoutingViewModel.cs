using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CanLinConfig.Models;
using CanLinConfig.Protocol;

namespace CanLinConfig.ViewModels;

public partial class RoutingViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    public ObservableCollection<RoutingRule> Rules { get; } = [];
    [ObservableProperty] private RoutingRule? _selectedRule;

    // The serialized size of one rule on the target — must be verified with sizeof on ARM
    // Default estimate: 4+4+4+4+4+1+1+40+1+3pad = 66 bytes
    // This will be auto-detected from the first bulk read if possible
    private int _ruleSize = 0;

    public RoutingViewModel(MainViewModel main) { _main = main; }

    [RelayCommand]
    private void AddRule()
    {
        if (Rules.Count < ProtocolConstants.MaxRoutingRules)
        {
            var rule = new RoutingRule { Enabled = true, SrcMask = 0x7FF };
            Rules.Add(rule);
            SelectedRule = rule;
        }
    }

    [RelayCommand]
    private void RemoveRule()
    {
        if (SelectedRule != null)
        {
            Rules.Remove(SelectedRule);
            SelectedRule = Rules.Count > 0 ? Rules[^1] : null;
        }
    }

    [RelayCommand]
    private void AddMapping()
    {
        if (SelectedRule != null && SelectedRule.Mappings.Count < ProtocolConstants.MaxByteMappings)
            SelectedRule.Mappings.Add(new ByteMapping { Mask = 0xFF });
    }

    [RelayCommand]
    private void RemoveMapping(ByteMapping? mapping)
    {
        if (SelectedRule != null && mapping != null)
            SelectedRule.Mappings.Remove(mapping);
    }

    public async Task ReadFromDeviceAsync(ConfigProtocol proto)
    {
        var data = await proto.BulkReadAsync(ProtocolConstants.SectionRouting, 0);
        if (data == null || data.Length == 0)
        {
            Rules.Clear();
            return;
        }

        // Auto-detect rule size: serialize one default rule and use its length
        if (_ruleSize == 0)
        {
            var sample = new RoutingRule();
            _ruleSize = sample.Serialize().Length;
        }

        Rules.Clear();
        int count = data.Length / _ruleSize;
        for (int i = 0; i < count; i++)
        {
            Rules.Add(RoutingRule.Deserialize(data, i * _ruleSize, _ruleSize));
        }
    }

    public async Task WriteToDeviceAsync(ConfigProtocol proto)
    {
        if (Rules.Count == 0)
        {
            await proto.BulkWriteAsync(ProtocolConstants.SectionRouting, 0, []);
            return;
        }

        using var ms = new MemoryStream();
        foreach (var rule in Rules)
            ms.Write(rule.Serialize());

        await proto.BulkWriteAsync(ProtocolConstants.SectionRouting, 0, ms.ToArray());
    }
}
