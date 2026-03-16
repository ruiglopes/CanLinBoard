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
    [ObservableProperty] private ByteMapping? _selectedMapping;

    // Set by MainViewModel after querying firmware, falls back to expected constant
    public int FirmwareRuleSize { get; set; } = ProtocolConstants.ExpectedRoutingRuleSize;

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
    private void RemoveMapping()
    {
        if (SelectedRule != null && SelectedMapping != null)
            SelectedRule.Mappings.Remove(SelectedMapping);
    }

    [ObservableProperty] private BitMapping? _selectedBitMapping;

    [RelayCommand]
    private void AddBitMapping()
    {
        if (SelectedRule != null)
            SelectedRule.BitMappings.Add(new BitMapping());
    }

    [RelayCommand]
    private void RemoveBitMapping()
    {
        if (SelectedRule != null && SelectedBitMapping != null)
            SelectedRule.BitMappings.Remove(SelectedBitMapping);
    }

    public async Task ReadFromDeviceAsync(ConfigProtocol proto)
    {
        var data = await proto.BulkReadAsync(ProtocolConstants.SectionRouting, 0);
        if (data == null || data.Length == 0)
        {
            Rules.Clear();
            return;
        }

        int ruleSize = FirmwareRuleSize;
        Rules.Clear();
        int count = data.Length / ruleSize;
        for (int i = 0; i < count; i++)
        {
            Rules.Add(RoutingRule.Deserialize(data, i * ruleSize, ruleSize));
        }
    }

    public async Task WriteToDeviceAsync(ConfigProtocol proto)
    {
        if (Rules.Count == 0)
        {
            await proto.BulkWriteAsync(ProtocolConstants.SectionRouting, 0, []);
            return;
        }

        // Convert bit mappings to byte mappings for rules in bit mode
        foreach (var rule in Rules)
        {
            if (rule.BitMode && rule.BitMappings.Count > 0)
            {
                rule.Mappings.Clear();
                foreach (var bm in rule.BitMappings)
                    foreach (var byteMap in bm.ToByteMapppings())
                        if (rule.Mappings.Count < ProtocolConstants.MaxByteMappings)
                            rule.Mappings.Add(byteMap);
            }
        }

        using var ms = new MemoryStream();
        foreach (var rule in Rules)
            ms.Write(rule.Serialize());

        await proto.BulkWriteAsync(ProtocolConstants.SectionRouting, 0, ms.ToArray());
    }
}
