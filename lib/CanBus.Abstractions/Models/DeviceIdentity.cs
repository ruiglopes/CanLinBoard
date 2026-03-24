using System;
using System.Collections.Generic;

namespace CanBus;

public class DeviceIdentity
{
    public byte[] UniqueId { get; set; } = new byte[8];
    public byte[] FlashJedecCs0 { get; set; } = new byte[3];
    public byte[] FlashJedecCs1 { get; set; } = new byte[3];

    public string UniqueIdHex => BitConverter.ToString(UniqueId).Replace("-", "");

    public string FlashCs0Summary
    {
        get
        {
            if (FlashJedecCs0[0] == 0) return "Not detected";
            string mfr = LookupManufacturer(FlashJedecCs0[0]);
            int sizeMb = FlashJedecCs0[2] >= 0x14 ? (1 << (FlashJedecCs0[2] - 17)) : 0;
            string sizeStr = sizeMb > 0 ? $" {sizeMb}MB" : "";
            return $"{mfr} (0x{FlashJedecCs0[0]:X2}{FlashJedecCs0[1]:X2}{FlashJedecCs0[2]:X2}){sizeStr}";
        }
    }

    public string FlashCs1Summary
    {
        get
        {
            if (FlashJedecCs1[0] == 0) return "Not detected";
            string mfr = LookupManufacturer(FlashJedecCs1[0]);
            int sizeMb = FlashJedecCs1[2] >= 0x14 ? (1 << (FlashJedecCs1[2] - 17)) : 0;
            string sizeStr = sizeMb > 0 ? $" {sizeMb}MB" : "";
            return $"{mfr} (0x{FlashJedecCs1[0]:X2}{FlashJedecCs1[1]:X2}{FlashJedecCs1[2]:X2}){sizeStr}";
        }
    }

    private static readonly Dictionary<byte, string> Manufacturers = new()
    {
        { 0x01, "Spansion/Infineon" },
        { 0x0B, "XTX" },
        { 0x1F, "Adesto/Renesas" },
        { 0x20, "Micron/ST" },
        { 0x25, "XMC" },
        { 0x34, "Cypress/Infineon" },
        { 0x37, "AMIC" },
        { 0x46, "Dialog/Renesas" },
        { 0x5E, "Zbit" },
        { 0x68, "Boya" },
        { 0x85, "Puya" },
        { 0x8C, "ESMT" },
        { 0x9D, "ISSI" },
        { 0xA1, "Fudan" },
        { 0xBA, "Zetta" },
        { 0xBF, "Microchip" },
        { 0xC2, "Macronix" },
        { 0xC8, "GigaDevice" },
        { 0xEF, "Winbond" },
    };

    private static string LookupManufacturer(byte id)
    {
        return Manufacturers.TryGetValue(id, out var name) ? name : $"Unknown(0x{id:X2})";
    }
}
