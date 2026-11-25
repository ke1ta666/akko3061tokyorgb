using System;
using System.Collections.Generic;

namespace AkkoTokyoRGB.Hardware;

public sealed record LightingModeInfo(
    byte Code,
    string NameRu,
    string NameEn,
    string Category,
    bool SupportsColor,
    bool SupportsRandomColor,
    bool SupportsSpeed,
    bool SupportsDirection,
    bool IsSurmountHue = false,
    bool IsCustomPerKey = false)
{
    public string CategoryEn => Category switch
    {
        "Статика" => "Static",
        "Базовые" => "Basic",
        "Радуга" => "Rainbow",
        "Волны" => "Waves",
        "От нажатий" => "Reactive",
        "Эффекты" => "Effects",
        "Своё" => "Custom",
        _ => Category
    };
}

public sealed class KeyboardModeConfig
{
    public byte Profile { get; set; }
    public byte Mode { get; set; } = 0x0B;
    public byte Brightness { get; set; } = 3;   // 0..4
    public byte Speed { get; set; } = 1;        // 0 (fastest) .. 5 (slowest)
    public byte Direction { get; set; } = 0;    // 0 or 1
    public bool RandomColors { get; set; }
    public byte Red { get; set; } = 255;
    public byte Green { get; set; } = 133;
    public byte Blue { get; set; } = 162;
    public byte LedMode { get; set; }

    public KeyboardModeConfig Clone() => new()
    {
        Profile = Profile,
        Mode = Mode,
        Brightness = Brightness,
        Speed = Speed,
        Direction = Direction,
        RandomColors = RandomColors,
        Red = Red,
        Green = Green,
        Blue = Blue,
        LedMode = LedMode,
    };
}

public static class EvisionModes
{
    public static readonly IReadOnlyList<LightingModeInfo> All = new List<LightingModeInfo>
    {
        new(0x06, "Статичный цвет",           "Static",                "Статика",    true,  true,  false, false),
        new(0x05, "Дыхание",                  "Breathing",             "Базовые",    true,  true,  true,  false),
        new(0x04, "Спектр (перелив)",         "Spectrum Cycle",        "Радуга",     false, false, true,  false),
        new(0x0B, "Цветение (Blooming)",      "Blooming",              "Радуга",     false, false, true,  false),
        new(0x01, "Волна короткая",           "Color Wave Short",      "Волны",      true,  true,  true,  true),
        new(0x02, "Волна длинная",            "Color Wave Long",       "Волны",      true,  true,  true,  true),
        new(0x03, "Цветовое колесо",          "Color Wheel",           "Волны",      true,  true,  true,  true),
        new(0x0C, "Вертикальная радуга",      "Rainbow Wave Vertical", "Радуга",     false, false, true,  true),
        new(0x12, "Круговая радуга",          "Rainbow Circle",        "Радуга",     false, false, false, true),
        new(0x07, "Реактивный след",          "Reactive",              "От нажатий", true,  true,  true,  false),
        new(0x08, "Рябь от клавиш",           "Reactive Ripple",       "От нажатий", true,  true,  true,  false),
        new(0x09, "Бегущая строка",           "Reactive Line",         "От нажатий", true,  true,  true,  false),
        new(0x0A, "Звёздный дождь (быстрый)", "Starlight Fast",        "Эффекты",    true,  true,  true,  false),
        new(0x0F, "Звёздный дождь (плавный)", "Starlight Slow",        "Эффекты",    true,  true,  true,  false),
        new(0x0D, "Ураган",                   "Hurricane",             "Эффекты",    true,  true,  true,  false),
        new(0x0E, "Накопление",               "Accumulate",            "Эффекты",    true,  true,  true,  false),
        new(0x10, "Визор",                    "Visor",                 "Эффекты",    true,  true,  true,  false),
        new(0x11, "Восхождение (4 цвета)",    "Surmount",              "Эффекты",    true,  false, false, false, IsSurmountHue: true),
        new(0x14, "Поклавишная (свои цвета)", "Custom Per-Key",        "Своё",       true,  false, false, false, IsCustomPerKey: true),
    };

    public static LightingModeInfo? FindByCode(byte code)
    {
        foreach (var m in All)
        {
            if (m.Code == code) return m;
        }
        return null;
    }

    public static byte MapSurmountColor(byte r, byte g, byte b)
    {
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;
        if (delta < 1e-3) return 0x00;

        double hue;
        if (Math.Abs(max - r) < 1e-3)
            hue = 60.0 * (((g - b) / delta) % 6.0);
        else if (Math.Abs(max - g) < 1e-3)
            hue = 60.0 * (((b - r) / delta) + 2.0);
        else
            hue = 60.0 * (((r - g) / delta) + 4.0);

        if (hue < 0) hue += 360.0;

        if (hue <= 30 || hue > 300) return 0x00; // Red
        if (hue <= 90) return 0x01;               // Yellow
        if (hue <= 150) return 0x02;              // Green
        return 0x03;                              // Cyan
    }
}
