using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using AkkoTokyoRGB.Controls;
using AkkoTokyoRGB.Hardware;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace AkkoTokyoRGB;

public partial class MainWindow : Window
{
    private const double LedGamma = 1.65;

    private readonly EvisionV2Device _device = new();
    private readonly Dictionary<byte, ToggleButton> _modeButtons = new();
    private readonly List<PreviewKey> _previewKeys = new();
    private readonly Stopwatch _animClock = Stopwatch.StartNew();
    private readonly Random _rng = new();

    private readonly List<(double X, double Y, double StartSec)> _reactivePulses = new();
    private double _nextAutoPulseSec = 0.4;
    private readonly double[] _starSeeds = new double[64];

    private KeyboardModeConfig? _initialConfig;
    private byte _selectedModeCode = 0x0B;
    private bool _suppressUiEvents = true;
    private bool _isUiReady;
    private bool _isApplying;
    private bool _applyPending;
    private bool _isLightTheme;
    private bool _isEnglish;
    private byte _lastConnectedProfile;
    private int _lastMapSize = 85;

    private CancellationTokenSource? _ramPreviewCts;
    private bool _isRamPreviewActive;
    private bool _isWheelRamDragging;
    private long _lastWheelRamMs;

    private WinForms.NotifyIcon? _trayIcon;
    private WinForms.ContextMenuStrip? _trayMenu;
    private WinForms.ToolStripMenuItem? _trayOpenItem;
    private WinForms.ToolStripMenuItem? _trayExitItem;
    private Drawing.Icon? _trayDrawingIcon;
    private bool _allowExit;
    private bool _shownTrayHint;

    private int[] _keyLedMap = CreateDefault61KeyMap();
    private readonly List<ToggleButton> _customSlotButtons = new();
    private byte _activeCustomSlot;
    private bool _isPaintingKey;
    private bool _isCalibratingLed;
    private int _calibSlot;
    private long _lastPerKeyRamMs;
    private static readonly string LedMapFilePath = System.IO.Path.Combine(AppContext.BaseDirectory, "led_map.json");

    private sealed record KeyDef(string Label, double Units, bool IsPink, string Special = "");

    private sealed class PreviewKey
    {
        public required SolidColorBrush HaloBrush { get; init; }
        public required SolidColorBrush TintOverlayBrush { get; init; }
        public required double NormX { get; init; }
        public required double NormY { get; init; }
        public required int Index { get; init; }
        public required string Label { get; init; }
        public required bool IsPinkKeycap { get; init; }
        public byte CustomR { get; set; }
        public byte CustomG { get; set; }
        public byte CustomB { get; set; }
    }

    private static readonly KeyDef[][] TokyoLayout =
    {
        new[]
        {
            new KeyDef("桜", 1, true), new KeyDef("1", 1, false), new KeyDef("2", 1, false), new KeyDef("3", 1, false),
            new KeyDef("4", 1, false), new KeyDef("5", 1, false), new KeyDef("6", 1, false), new KeyDef("7", 1, false),
            new KeyDef("8", 1, false), new KeyDef("9", 1, false), new KeyDef("0", 1, false), new KeyDef("-", 1, false),
            new KeyDef("=", 1, false), new KeyDef("← Backspace", 2, true)
        },
        new[]
        {
            new KeyDef("Tab ⇄", 1.5, true), new KeyDef("Q", 1, false), new KeyDef("W", 1, false), new KeyDef("E", 1, false),
            new KeyDef("R", 1, false), new KeyDef("T", 1, false), new KeyDef("Y", 1, false), new KeyDef("U", 1, false),
            new KeyDef("I", 1, false), new KeyDef("O", 1, false), new KeyDef("P", 1, false), new KeyDef("[", 1, false),
            new KeyDef("]", 1, false), new KeyDef("\\", 1.5, false)
        },
        new[]
        {
            new KeyDef("Caps Lock", 1.75, true), new KeyDef("A", 1, false), new KeyDef("S", 1, false), new KeyDef("D", 1, false),
            new KeyDef("F", 1, false), new KeyDef("G", 1, false), new KeyDef("H", 1, false), new KeyDef("J", 1, false),
            new KeyDef("K", 1, false), new KeyDef("L", 1, false), new KeyDef(";", 1, false), new KeyDef("'", 1, false),
            new KeyDef("Enter", 2.25, false, "koi")
        },
        new[]
        {
            new KeyDef("⇧ Shift", 2.25, true), new KeyDef("Z", 1, false), new KeyDef("X", 1, false), new KeyDef("C", 1, false),
            new KeyDef("V", 1, false), new KeyDef("B", 1, false), new KeyDef("N", 1, false), new KeyDef("M", 1, false),
            new KeyDef(",", 1, false), new KeyDef(".", 1, false), new KeyDef("/", 1, false), new KeyDef("⇧ Shift", 2.75, true)
        },
        new[]
        {
            new KeyDef("Control", 1.25, true), new KeyDef("✿", 1.25, true), new KeyDef("Alt", 1.25, true),
            new KeyDef("", 6.25, false, "fuji"),
            new KeyDef("Alt", 1.25, true), new KeyDef("✿", 1.25, true), new KeyDef("Fn", 1.25, true), new KeyDef("Control", 1.25, true)
        }
    };

    private static readonly (string Name, byte R, byte G, byte B)[] TokyoSwatches =
    {
        ("Сакура",     255,  30,  95),
        ("Красный",    255,   0,   0),
        ("Оранжевый",  255,  85,   0),
        ("Жёлтый",     255, 220,   0),
        ("Зелёный",      0, 255,   0),
        ("Бирюза",       0, 255, 210),
        ("Синий",        0,  90, 255),
        ("Фиолетовый", 170,   0, 255),
        ("Белый",      255, 255, 255),
    };

    public MainWindow()
    {
        _suppressUiEvents = true;
        InitializeComponent();
        for (int i = 0; i < _starSeeds.Length; i++)
        {
            _starSeeds[i] = _rng.NextDouble() * 100.0;
        }

        LoadLedMap();
        BuildKeyboardPreview();
        BuildCustomSlotButtons();
        ApplyTokyoPerKeyPreset(sendRam: false);
        BuildModeButtons();
        BuildSwatches();
        WheelPicker.SetRgb(255, 25, 80);
        InitializeTrayIcon();

        PreviewMouseLeftButtonUp += (_, _) => _isPaintingKey = false;

        _isUiReady = true;
        _suppressUiEvents = false;
        UpdateModeCapabilitiesUi();
        UpdateColorPreview();

        CompositionTarget.Rendering += CompositionTarget_Rendering;

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        StateChanged += MainWindow_StateChanged;
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            RootWindowFrame.Margin = new Thickness(7);
            MaxBtn.Content = "❐";
        }
        else
        {
            RootWindowFrame.Margin = new Thickness(0);
            MaxBtn.Content = "□";
        }
    }

    private void MinBtn_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaxBtn_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await ConnectAndReadInitialAsync();
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowExit)
        {
            e.Cancel = true;
            MinimizeToTray();
            return;
        }

        CompositionTarget.Rendering -= CompositionTarget_Rendering;
        StopRamPreview();
        _device.Dispose();

        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }
        _trayMenu?.Dispose();
        _trayMenu = null;
        _trayDrawingIcon?.Dispose();
        _trayDrawingIcon = null;
    }

    private void InitializeTrayIcon()
    {
        try
        {
            var iconRes = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/tokyo_icon.ico"));
            if (iconRes != null)
            {
                using var stream = iconRes.Stream;
                _trayDrawingIcon = new Drawing.Icon(stream);
            }
        }
        catch
        {
            _trayDrawingIcon = Drawing.SystemIcons.Application;
        }

        _trayMenu = new WinForms.ContextMenuStrip();
        _trayOpenItem = new WinForms.ToolStripMenuItem(Tr("Открыть AKKO 3061", "Open AKKO 3061"))
        {
            Font = new Drawing.Font("Segoe UI", 9.5f, Drawing.FontStyle.Bold)
        };
        _trayOpenItem.Click += (_, _) => Dispatcher.Invoke(RestoreFromTray);

        _trayExitItem = new WinForms.ToolStripMenuItem(Tr("Выход", "Exit"));
        _trayExitItem.Click += (_, _) => Dispatcher.Invoke(ExitFromTray);

        _trayMenu.Items.Add(_trayOpenItem);
        _trayMenu.Items.Add(new WinForms.ToolStripSeparator());
        _trayMenu.Items.Add(_trayExitItem);

        _trayIcon = new WinForms.NotifyIcon
        {
            Icon = _trayDrawingIcon ?? Drawing.SystemIcons.Application,
            Text = "AKKO 3061 • World Tour Tokyo RGB",
            ContextMenuStrip = _trayMenu,
            Visible = true
        };

        _trayIcon.MouseClick += (_, args) =>
        {
            if (args.Button == WinForms.MouseButtons.Left)
            {
                Dispatcher.Invoke(() =>
                {
                    if (IsVisible && WindowState != WindowState.Minimized)
                        MinimizeToTray();
                    else
                        RestoreFromTray();
                });
            }
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(RestoreFromTray);
    }

    private void MinimizeToTray()
    {
        Hide();
        ShowInTaskbar = false;
        if (!_shownTrayHint && _trayIcon != null)
        {
            _shownTrayHint = true;
            _trayIcon.ShowBalloonTip(
                2000,
                "AKKO 3061 • World Tour Tokyo",
                "Приложение свёрнуто в трей. Кликните по иконке, чтобы открыть, или выберите «Выход».",
                WinForms.ToolTipIcon.Info);
        }
    }

    private void RestoreFromTray()
    {
        Show();
        ShowInTaskbar = true;
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        Focus();
    }

    private void ExitFromTray()
    {
        _allowExit = true;
        Close();
    }

    private async Task ConnectAndReadInitialAsync()
    {
        SetStatus(false, Tr("Поиск клавиатуры...", "Searching keyboard..."));
        try
        {
            var (cfg, mapSize) = await Task.Run(() =>
            {
                _device.Connect();
                var current = _device.ReadCurrentConfig();
                return (current, _device.MapSize);
            });

            _lastConnectedProfile = cfg.Profile;
            _lastMapSize = mapSize;
            _initialConfig ??= cfg.Clone();
            ApplyConfigToUi(cfg);
            SetStatus(true, Tr($"Подключено • Профиль {cfg.Profile + 1} • LED: {mapSize}", $"Connected • Profile {cfg.Profile + 1} • LED: {mapSize}"));
            FooterLogText.Text = Tr(
                "Клавиатура подключена. Текущие настройки загружены.",
                "Keyboard connected. Current settings loaded.");
        }
        catch (Exception ex)
        {
            SetStatus(false, Tr("Не найдена", "Not found"));
            FooterLogText.Text = ex.Message;
        }
    }

    private void SetStatus(bool ok, string text)
    {
        StatusDot.Fill = new SolidColorBrush(ok ? Color.FromRgb(54, 199, 117) : Color.FromRgb(238, 62, 110));
        StatusText.Text = text;
        ApplyBtn.IsEnabled = ok;
        RamPreviewBtn.IsEnabled = ok;
        OffBtn.IsEnabled = ok;
        RestoreBtn.IsEnabled = ok && _initialConfig != null;
    }

    private void ThemeToggleBtn_Click(object sender, RoutedEventArgs e)
    {
        _isLightTheme = !_isLightTheme;
        var res = Application.Current.Resources;
        if (_isLightTheme)
        {
            res["SakuraBgBrush"] = new SolidColorBrush(Color.FromRgb(253, 242, 245));
            res["SakuraCardBrush"] = new SolidColorBrush(Color.FromRgb(255, 255, 255));
            res["SakuraPanelBrush"] = new SolidColorBrush(Color.FromRgb(255, 247, 249));
            res["SakuraBorderBrush"] = new SolidColorBrush(Color.FromRgb(244, 194, 207));
            res["SakuraPinkAccentBrush"] = new SolidColorBrush(Color.FromRgb(217, 56, 72));
            res["TokyoTextBrush"] = new SolidColorBrush(Color.FromRgb(74, 37, 49));
            res["TokyoMutedBrush"] = new SolidColorBrush(Color.FromRgb(140, 91, 106));
            res["ModeBtnBgBrush"] = new SolidColorBrush(Color.FromRgb(255, 248, 250));
            res["ModeBtnBorderBrush"] = new SolidColorBrush(Color.FromRgb(245, 206, 216));
            res["SoftBtnBgBrush"] = new SolidColorBrush(Color.FromRgb(252, 224, 232));
            res["SoftBtnBorderBrush"] = new SolidColorBrush(Color.FromRgb(243, 166, 185));
            res["BadgeBgBrush"] = new SolidColorBrush(Color.FromRgb(252, 224, 232));
            res["BadgeFgBrush"] = new SolidColorBrush(Color.FromRgb(156, 60, 82));
            res["InputBgBrush"] = new SolidColorBrush(Color.FromRgb(255, 255, 255));
            res["TitleBarBgBrush"] = new SolidColorBrush(Color.FromRgb(249, 222, 231));
            ThemeToggleBtn.Content = Tr("Чёрная тема", "Dark Theme");
        }
        else
        {
            res["SakuraBgBrush"] = new SolidColorBrush(Color.FromRgb(10, 9, 14));
            res["SakuraCardBrush"] = new SolidColorBrush(Color.FromRgb(19, 16, 25));
            res["SakuraPanelBrush"] = new SolidColorBrush(Color.FromRgb(27, 22, 35));
            res["SakuraBorderBrush"] = new SolidColorBrush(Color.FromRgb(110, 53, 76));
            res["SakuraPinkAccentBrush"] = new SolidColorBrush(Color.FromRgb(255, 105, 150));
            res["TokyoTextBrush"] = new SolidColorBrush(Color.FromRgb(252, 239, 244));
            res["TokyoMutedBrush"] = new SolidColorBrush(Color.FromRgb(194, 149, 166));
            res["ModeBtnBgBrush"] = new SolidColorBrush(Color.FromRgb(25, 20, 33));
            res["ModeBtnBorderBrush"] = new SolidColorBrush(Color.FromRgb(68, 39, 56));
            res["SoftBtnBgBrush"] = new SolidColorBrush(Color.FromRgb(38, 27, 46));
            res["SoftBtnBorderBrush"] = new SolidColorBrush(Color.FromRgb(128, 64, 91));
            res["BadgeBgBrush"] = new SolidColorBrush(Color.FromRgb(59, 31, 48));
            res["BadgeFgBrush"] = new SolidColorBrush(Color.FromRgb(255, 140, 176));
            res["InputBgBrush"] = new SolidColorBrush(Color.FromRgb(14, 11, 19));
            res["TitleBarBgBrush"] = new SolidColorBrush(Color.FromRgb(14, 11, 20));
            ThemeToggleBtn.Content = Tr("Светлая тема", "Light Theme");
        }
    }

    private string Tr(string ru, string en) => _isEnglish ? en : ru;

    private void LangToggleBtn_Click(object sender, RoutedEventArgs e)
    {
        _isEnglish = !_isEnglish;
        ApplyLanguage();
    }

    private void ApplyLanguage()
    {
        LangToggleBtn.Content = _isEnglish ? "RU" : "EN";
        MinBtn.ToolTip = Tr("Свернуть", "Minimize");
        MaxBtn.ToolTip = Tr("Развернуть", "Maximize");
        CloseBtn.ToolTip = Tr("Свернуть в трей", "Minimize to tray");

        HeaderSubtitleText.Text = Tr(
            "Подсветка и эффекты для клавиатуры",
            "RGB lighting & effects controller");
        ThemeToggleBtn.Content = _isLightTheme
            ? Tr("Чёрная тема", "Dark Theme")
            : Tr("Светлая тема", "Light Theme");
        ReconnectBtn.Content = Tr("Обновить", "Refresh");
        RestoreBtn.Content = Tr("Вернуть исходный", "Restore Initial");
        RestoreBtn.ToolTip = Tr(
            "Вернуть настройки, которые были на клавиатуре при запуске",
            "Restore the lighting configuration active on startup");

        PreviewTitleText.Text = Tr("Предпросмотр клавиатуры", "Keyboard Preview");
        SlotLabelText.Text = Tr("Слот:", "Slot:");
        ToolLabelText.Text = Tr("Инструмент:", "Tool:");
        BrushToolBtn.Content = Tr("Кисть", "Brush");
        EraserToolBtn.Content = Tr("Ластик", "Eraser");
        FillAllBtn.Content = Tr("Залить всё", "Fill All");
        PresetLabelText.Text = Tr("Пресет:", "Preset:");
        RainbowPresetBtn.Content = Tr("Радуга", "Rainbow");
        ClearPerKeyBtn.Content = Tr("Очистить", "Clear");
        CalibrateToggleBtn.Content = Tr("Калибровка", "Calibrate");
        CalibrateToggleBtn.ToolTip = Tr(
            "Если какая-то клавиша на клавиатуре не совпадает со схемой, нажмите для быстрой привязки",
            "Click to remap any key if your 85-LED matrix differs from the default layout");

        CalibTitleText.Text = Tr("Калибровка LED:", "LED Calibration:");
        CalibHintText.Text = Tr(
            "Кликните по загоревшейся клавише на схеме выше (привяжется к слоту)",
            "Click the lit key on the preview above to bind it to this LED slot");
        CalibResetBtn.Content = Tr("Сброс карты", "Reset Map");
        CalibSlotText.Text = Tr($"Слот {_calibSlot} / {Math.Max(1, _device.MapSize) - 1}", $"Slot {_calibSlot} / {Math.Max(1, _device.MapSize) - 1}");

        ModesHeaderText.Text = Tr("Режимы подсветки (19 эффектов)", "Lighting Modes (19 effects)");
        ModesSubHeaderText.Text = Tr("Клик сразу включает эффект", "Click applies immediately");
        ColorParamsHeaderText.Text = Tr("Цвет и параметры эффекта", "Color & Effect Parameters");
        PaletteTitleText.Text = Tr("Палитра (16.7 млн цветов)", "Color Wheel (16.7M colors)");
        RandomColorCheck.Content = Tr("Случайный (радуга)", "Random (Rainbow)");
        ToneLabelText.Text = Tr("Тон", "Value");
        LedGammaCheck.Content = Tr(
            "Калибровка гаммы 1.65 (приглушить пересвет на белых кейкапах)",
            "Gamma 1.65 calibration (richer colors on white keycaps)");
        BrightnessLabelText.Text = Tr("Яркость", "Brightness");
        SpeedLabelText.Text = Tr("Скорость", "Speed");
        DirectionLabelText.Text = Tr("Направление", "Direction");
        DirLeftRadio.Content = Tr("Влево / Вверх", "Left / Up");
        DirRightRadio.Content = Tr("Вправо / Вниз", "Right / Down");

        ApplyBtn.Content = Tr("Применить и сохранить", "Apply & Save");
        RamPreviewBtn.Content = _isRamPreviewActive
            ? Tr("Остановить предпросмотр", "Stop Preview")
            : Tr("Быстрый предпросмотр цвета", "Quick Color Preview");
        OffBtn.Content = Tr("Выкл свет", "Lights Off");
        FooterHintText.Text = Tr(
            "Клик по клавише на схеме запускает световую волну",
            "Click any key on the preview to trigger a light pulse");

        if (_device.IsConnected)
            StatusText.Text = Tr($"Подключено • Профиль {_lastConnectedProfile + 1} • LED: {_lastMapSize}", $"Connected • Profile {_lastConnectedProfile + 1} • LED: {_lastMapSize}");
        else
            StatusText.Text = Tr("Не найдена", "Not found");

        if (_trayOpenItem != null) _trayOpenItem.Text = Tr("Открыть AKKO 3061", "Open AKKO 3061");
        if (_trayExitItem != null) _trayExitItem.Text = Tr("Выход", "Exit");

        BuildModeButtons();
        foreach (var kv in _modeButtons)
        {
            kv.Value.IsChecked = kv.Key == _selectedModeCode;
        }
        BuildSwatches();
        UpdateModeCapabilitiesUi();
    }

    private void BuildKeyboardPreview()
    {
        KeyboardPreviewHost.Children.Clear();
        _previewKeys.Clear();

        // Exact integer pixel math: 15 * 44 + 14 * 4 = 716px, zero fractional pixels
        const double unitW = 44.0;
        const double keyH = 42.0;
        const double gap = 4.0;
        const double totalUnits = 15.0;
        double totalW = totalUnits * unitW + (totalUnits - 1) * gap;

        int idx = 0;
        for (int rowIdx = 0; rowIdx < TokyoLayout.Length; rowIdx++)
        {
            var row = TokyoLayout[rowIdx];
            var rowPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                SnapsToDevicePixels = false,
                UseLayoutRounding = false,
                Margin = new Thickness(0, 0, 0, rowIdx == TokyoLayout.Length - 1 ? 0 : gap)
            };
            double xCursor = 0;

            foreach (var k in row)
            {
                double w = Math.Round(k.Units * unitW + (k.Units - 1) * gap);
                double centerX = (xCursor + w / 2.0) / totalW;
                double centerY = rowIdx / 4.0;
                xCursor += w + gap;

                var haloBrush = new SolidColorBrush(Color.FromRgb(0, 255, 255));
                var haloWrap = new Border
                {
                    Width = w,
                    Height = keyH,
                    Margin = new Thickness(0, 0, rowPanel.Children.Count == row.Length - 1 ? 0 : gap, 0),
                    CornerRadius = new CornerRadius(7),
                    Padding = new Thickness(3),
                    Background = haloBrush,
                    SnapsToDevicePixels = false,
                    UseLayoutRounding = false,
                    Cursor = Cursors.Hand,
                };

                var tintOverlay = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));

                // Outer keycap skirt (creates clean dimensional keycap bevel)
                var keySkirt = new Border
                {
                    CornerRadius = new CornerRadius(5.5),
                    Padding = new Thickness(2, 1.5, 2, 3),
                    Background = new SolidColorBrush(k.IsPink ? Color.FromRgb(214, 118, 144) : Color.FromRgb(220, 200, 208)),
                    SnapsToDevicePixels = false,
                    UseLayoutRounding = false,
                };

                // Inner keycap top dish with smooth gradient and crisp corner radius
                var keyTopDish = new Border
                {
                    CornerRadius = new CornerRadius(4),
                    Background = k.IsPink
                        ? new LinearGradientBrush(Color.FromRgb(249, 174, 195), Color.FromRgb(239, 144, 170), 90)
                        : new LinearGradientBrush(Color.FromRgb(255, 253, 254), Color.FromRgb(246, 236, 240), 90),
                    SnapsToDevicePixels = false,
                    UseLayoutRounding = false,
                    ClipToBounds = true
                };

                var keyContentGrid = new Grid();
                if (k.Special == "fuji")
                {
                    double cw = w - 10;
                    double ch = keyH - 10.5;
                    var canvas = new Canvas { Width = cw, Height = ch };
                    var sun = new Ellipse { Width = 10, Height = 10, Fill = new SolidColorBrush(Color.FromRgb(217, 56, 72)) };
                    Canvas.SetLeft(sun, cw * 0.42);
                    Canvas.SetTop(sun, 3);
                    canvas.Children.Add(sun);

                    var fuji = new Polygon
                    {
                        Fill = new SolidColorBrush(Color.FromRgb(44, 67, 110)),
                        Points = new PointCollection
                        {
                            new Point(cw * 0.20, ch),
                            new Point(cw * 0.36, 10),
                            new Point(cw * 0.42, 10),
                            new Point(cw * 0.58, ch)
                        }
                    };
                    canvas.Children.Add(fuji);

                    var snow = new Polygon
                    {
                        Fill = Brushes.White,
                        Points = new PointCollection
                        {
                            new Point(cw * 0.32, 17),
                            new Point(cw * 0.36, 10),
                            new Point(cw * 0.42, 10),
                            new Point(cw * 0.46, 17),
                            new Point(cw * 0.41, 15),
                            new Point(cw * 0.37, 18)
                        }
                    };
                    canvas.Children.Add(snow);

                    var toriiTop = new Rectangle { Width = 17, Height = 2.5, Fill = new SolidColorBrush(Color.FromRgb(217, 56, 72)) };
                    Canvas.SetLeft(toriiTop, cw * 0.66);
                    Canvas.SetTop(toriiTop, 17);
                    var toriiL = new Rectangle { Width = 2, Height = 14, Fill = new SolidColorBrush(Color.FromRgb(217, 56, 72)) };
                    Canvas.SetLeft(toriiL, cw * 0.66 + 3);
                    Canvas.SetTop(toriiL, 18);
                    var toriiR = new Rectangle { Width = 2, Height = 14, Fill = new SolidColorBrush(Color.FromRgb(217, 56, 72)) };
                    Canvas.SetLeft(toriiR, cw * 0.66 + 12);
                    Canvas.SetTop(toriiR, 18);
                    canvas.Children.Add(toriiTop);
                    canvas.Children.Add(toriiL);
                    canvas.Children.Add(toriiR);

                    foreach (var pt in new[] { (12.0, 6.0), (24.0, 12.0), (40.0, 5.0), (cw - 30, 7.0), (cw - 18, 13.0) })
                    {
                        var petal = new Ellipse { Width = 4.5, Height = 3.5, Fill = new SolidColorBrush(Color.FromRgb(244, 143, 177)) };
                        Canvas.SetLeft(petal, pt.Item1);
                        Canvas.SetTop(petal, pt.Item2);
                        canvas.Children.Add(petal);
                    }

                    keyContentGrid.Children.Add(canvas);
                }
                else if (k.Special == "koi")
                {
                    var koi = new Polygon
                    {
                        Fill = new SolidColorBrush(Color.FromRgb(230, 82, 102)),
                        Opacity = 0.28,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        Points = new PointCollection { new Point(0, 7), new Point(20, 0), new Point(34, 4), new Point(44, 0), new Point(40, 7), new Point(44, 14), new Point(34, 11), new Point(20, 14) }
                    };
                    keyContentGrid.Children.Add(koi);
                    keyContentGrid.Children.Add(new TextBlock
                    {
                        Text = k.Label,
                        FontSize = 10,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = new SolidColorBrush(Color.FromRgb(186, 52, 74)),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    });
                }
                else
                {
                    keyContentGrid.Children.Add(new TextBlock
                    {
                        Text = k.Label,
                        FontSize = k.Label.Length > 2 ? 8.5 : 10.5,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = new SolidColorBrush(k.IsPink ? Color.FromRgb(166, 36, 58) : Color.FromRgb(192, 68, 94)),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    });
                }

                keyContentGrid.Children.Add(new Border
                {
                    Background = tintOverlay,
                    IsHitTestVisible = false
                });

                keyTopDish.Child = keyContentGrid;
                keySkirt.Child = keyTopDish;
                haloWrap.Child = keySkirt;

                var previewKey = new PreviewKey
                {
                    HaloBrush = haloBrush,
                    TintOverlayBrush = tintOverlay,
                    NormX = centerX,
                    NormY = centerY,
                    Index = idx++,
                    Label = k.Label,
                    IsPinkKeycap = k.IsPink
                };

                double capturedX = centerX;
                double capturedY = centerY;
                haloWrap.MouseLeftButtonDown += (_, args) =>
                {
                    if (_selectedModeCode == 0x14 && !_isRamPreviewActive)
                    {
                        if (_isCalibratingLed)
                        {
                            BindCalibSlotToKey(previewKey.Index);
                        }
                        else
                        {
                            _isPaintingKey = true;
                            PaintPreviewKey(previewKey, erase: false);
                        }
                        args.Handled = true;
                        return;
                    }

                    double now = _animClock.Elapsed.TotalSeconds;
                    _reactivePulses.Add((capturedX, capturedY, now));
                    if (_reactivePulses.Count > 14) _reactivePulses.RemoveAt(0);
                };

                haloWrap.MouseEnter += (_, _) =>
                {
                    if (_selectedModeCode == 0x14 && !_isRamPreviewActive && !_isCalibratingLed
                        && _isPaintingKey && Mouse.LeftButton == MouseButtonState.Pressed)
                    {
                        PaintPreviewKey(previewKey, erase: false);
                    }
                };

                haloWrap.MouseRightButtonDown += (_, args) =>
                {
                    if (_selectedModeCode == 0x14 && !_isRamPreviewActive && !_isCalibratingLed)
                    {
                        PaintPreviewKey(previewKey, erase: true);
                        args.Handled = true;
                    }
                };

                _previewKeys.Add(previewKey);

                rowPanel.Children.Add(haloWrap);
            }
            KeyboardPreviewHost.Children.Add(rowPanel);
        }
    }

    private void CompositionTarget_Rendering(object? sender, EventArgs e)
    {
        if (!_isUiReady || _previewKeys.Count == 0) return;

        double t = _animClock.Elapsed.TotalSeconds;
        double bright = BrightnessSlider.Value / 4.0;
        // Calibrated to match EVision V2 hardware firmware speeds (1..6)
        double speedFactor = 0.28 + (SpeedSlider.Value - 1) * 0.20;
        double dir = DirLeftRadio.IsChecked == true ? 1.0 : -1.0;

        byte baseR = (byte)RedSlider.Value;
        byte baseG = (byte)GreenSlider.Value;
        byte baseB = (byte)BlueSlider.Value;
        bool isRandom = RandomColorCheck.IsChecked == true;
        byte mode = (_isRamPreviewActive || _isWheelRamDragging) ? (byte)0x06 : _selectedModeCode;
        if (_isRamPreviewActive || _isWheelRamDragging) isRandom = false;

        if (mode is 0x07 or 0x08 or 0x09 && t >= _nextAutoPulseSec)
        {
            var pk = _previewKeys[_rng.Next(_previewKeys.Count)];
            _reactivePulses.Add((pk.NormX, pk.NormY, t));
            if (_reactivePulses.Count > 14) _reactivePulses.RemoveAt(0);
            _nextAutoPulseSec = t + Math.Max(0.28, 1.1 / speedFactor);
        }
        _reactivePulses.RemoveAll(p => t - p.StartSec > 2.2);

        foreach (var k in _previewKeys)
        {
            var (r, g, b) = EvaluateModePixel(mode, k, t, speedFactor, dir, isRandom, baseR, baseG, baseB);
            byte lr = (byte)(r * bright);
            byte lg = (byte)(g * bright);
            byte lb = (byte)(b * bright);

            k.HaloBrush.Color = Color.FromRgb(lr, lg, lb);

            byte alpha = (byte)(bright > 0.01 ? 24 + 42 * bright : 0);
            k.TintOverlayBrush.Color = Color.FromArgb(alpha, lr, lg, lb);
        }
    }

    private (byte R, byte G, byte B) EvaluateModePixel(
        byte mode, PreviewKey k, double t, double speed, double dir, bool isRandom,
        byte baseR, byte baseG, byte baseB)
    {
        Color PickBase(double phase) => isRandom ? HsvToRgb((phase % 1.0 + 1.0) % 1.0, 1.0, 1.0) : Color.FromRgb(baseR, baseG, baseB);

        switch (mode)
        {
            case 0x14:
            {
                if (_isCalibratingLed)
                {
                    bool isMatch = k.Index < _keyLedMap.Length && _keyLedMap[k.Index] == _calibSlot;
                    return isMatch ? ((byte)255, (byte)80, (byte)150) : ((byte)18, (byte)12, (byte)24);
                }
                return (k.CustomR, k.CustomG, k.CustomB);
            }
            case 0x06:
            {
                var c = PickBase(t * 0.15);
                return (c.R, c.G, c.B);
            }
            case 0x05:
            {
                double wave = 0.5 - 0.5 * Math.Cos(t * speed * 1.65);
                double hueCycle = Math.Floor((t * speed * 1.65) / (2 * Math.PI)) * 0.18;
                var c = PickBase(hueCycle);
                return ((byte)(c.R * wave), (byte)(c.G * wave), (byte)(c.B * wave));
            }
            case 0x04:
            {
                var c = HsvToRgb(((t * speed * 0.11) % 1.0 + 1.0) % 1.0, 1.0, 1.0);
                return (c.R, c.G, c.B);
            }
            case 0x0B:
            {
                double dx = k.NormX - 0.5;
                double dy = (k.NormY - 0.5) * 0.45;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                double hue = dist * 1.5 - t * speed * 0.24;
                var c = HsvToRgb(((hue % 1.0) + 1.0) % 1.0, 1.0, 1.0);
                return (c.R, c.G, c.B);
            }
            case 0x01:
            case 0x02:
            {
                double scale = mode == 0x01 ? 1.8 : 0.9;
                double phase = k.NormX * scale + dir * t * speed * 0.30;
                if (isRandom)
                {
                    var c = HsvToRgb(((phase % 1.0) + 1.0) % 1.0, 1.0, 1.0);
                    return (c.R, c.G, c.B);
                }
                double amp = 0.12 + 0.88 * (0.5 + 0.5 * Math.Sin(phase * 2.0 * Math.PI));
                return ((byte)(baseR * amp), (byte)(baseG * amp), (byte)(baseB * amp));
            }
            case 0x03:
            {
                double angle = Math.Atan2((k.NormY - 0.5) * 0.45, k.NormX - 0.5) / (2.0 * Math.PI);
                double phase = angle + dir * t * speed * 0.25;
                if (isRandom)
                {
                    var c = HsvToRgb(((phase % 1.0) + 1.0) % 1.0, 1.0, 1.0);
                    return (c.R, c.G, c.B);
                }
                double amp = 0.12 + 0.88 * (0.5 + 0.5 * Math.Sin(phase * 2.0 * Math.PI));
                return ((byte)(baseR * amp), (byte)(baseG * amp), (byte)(baseB * amp));
            }
            case 0x0C:
            {
                double phase = k.NormY * 1.1 + dir * t * speed * 0.30;
                var c = HsvToRgb(((phase % 1.0) + 1.0) % 1.0, 1.0, 1.0);
                return (c.R, c.G, c.B);
            }
            case 0x12:
            {
                double dx = k.NormX - 0.5;
                double dy = (k.NormY - 0.5) * 0.45;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                double phase = dist * 2.2 - dir * t * speed * 0.30;
                var c = HsvToRgb(((phase % 1.0) + 1.0) % 1.0, 1.0, 1.0);
                return (c.R, c.G, c.B);
            }
            case 0x07:
            {
                double intensity = 0.08;
                double hue = t * 0.2;
                foreach (var p in _reactivePulses)
                {
                    double age = t - p.StartSec;
                    double dx = k.NormX - p.X;
                    double dy = (k.NormY - p.Y) * 0.45;
                    if (Math.Sqrt(dx * dx + dy * dy) < 0.08)
                    {
                        double val = Math.Max(0, 1.0 - age * speed * 0.9);
                        if (val > intensity) { intensity = val; hue = p.StartSec * 0.37; }
                    }
                }
                var c = PickBase(hue);
                return ((byte)(c.R * intensity), (byte)(c.G * intensity), (byte)(c.B * intensity));
            }
            case 0x08:
            {
                double intensity = 0.06;
                double hue = t * 0.2;
                foreach (var p in _reactivePulses)
                {
                    double age = t - p.StartSec;
                    double dx = k.NormX - p.X;
                    double dy = (k.NormY - p.Y) * 0.45;
                    double d = Math.Sqrt(dx * dx + dy * dy);
                    double radius = age * speed * 0.55;
                    double ring = Math.Exp(-Math.Pow((d - radius) / 0.09, 2.0)) * Math.Max(0, 1.0 - age * 0.7);
                    if (ring > intensity) { intensity = ring; hue = p.StartSec * 0.41 + d; }
                }
                var c = PickBase(hue);
                return ((byte)(c.R * intensity), (byte)(c.G * intensity), (byte)(c.B * intensity));
            }
            case 0x09:
            {
                double intensity = 0.06;
                double hue = t * 0.2;
                foreach (var p in _reactivePulses)
                {
                    double age = t - p.StartSec;
                    if (Math.Abs(k.NormY - p.Y) < 0.14)
                    {
                        double d = Math.Abs(k.NormX - p.X);
                        double pos = age * speed * 0.85;
                        double band = Math.Exp(-Math.Pow((d - pos) / 0.10, 2.0)) * Math.Max(0, 1.0 - age * 0.8);
                        if (band > intensity) { intensity = band; hue = p.StartSec * 0.31; }
                    }
                }
                var c = PickBase(hue);
                return ((byte)(c.R * intensity), (byte)(c.G * intensity), (byte)(c.B * intensity));
            }
            case 0x0A:
            case 0x0F:
            {
                double rate = mode == 0x0A ? 2.4 : 0.9;
                double seed = _starSeeds[k.Index % _starSeeds.Length];
                double v = Math.Pow(Math.Max(0, Math.Sin(t * speed * rate + seed)), 6.0);
                var c = PickBase(seed * 0.13 + Math.Floor((t * speed * rate + seed) / Math.PI) * 0.25);
                return ((byte)(c.R * (0.08 + 0.92 * v)), (byte)(c.G * (0.08 + 0.92 * v)), (byte)(c.B * (0.08 + 0.92 * v)));
            }
            case 0x0D:
            {
                double dx = k.NormX - 0.5;
                double dy = (k.NormY - 0.5) * 0.45;
                double angle = Math.Atan2(dy, dx);
                double dist = Math.Sqrt(dx * dx + dy * dy);
                double wave = 0.5 + 0.5 * Math.Sin(angle * 2.0 + dist * 10.0 - t * speed * 2.4);
                var c = PickBase(angle / (2 * Math.PI) + t * 0.14);
                return ((byte)(c.R * wave), (byte)(c.G * wave), (byte)(c.B * wave));
            }
            case 0x0E:
            {
                double cycle = (t * speed * 0.24) % 1.0;
                double threshold = k.NormY * 0.8 + k.NormX * 0.2;
                double amp = cycle > threshold ? 1.0 : 0.10;
                var c = PickBase(Math.Floor(t * speed * 0.24) * 0.21 + k.NormX * 0.2);
                return ((byte)(c.R * amp), (byte)(c.G * amp), (byte)(c.B * amp));
            }
            case 0x10:
            {
                double beamX = 0.5 + 0.48 * Math.Sin(t * speed * 1.6);
                double amp = Math.Exp(-Math.Pow((k.NormX - beamX) / 0.14, 2.0));
                var c = PickBase(t * 0.25);
                return ((byte)(c.R * (0.08 + 0.92 * amp)), (byte)(c.G * (0.08 + 0.92 * amp)), (byte)(c.B * (0.08 + 0.92 * amp)));
            }
            case 0x11:
            {
                byte hueCode = EvisionModes.MapSurmountColor(baseR, baseG, baseB);
                Color c = hueCode switch
                {
                    0 => Color.FromRgb(255, 30, 30),
                    1 => Color.FromRgb(255, 210, 0),
                    2 => Color.FromRgb(30, 255, 60),
                    _ => Color.FromRgb(0, 240, 255),
                };
                double wave = 0.5 + 0.5 * Math.Sin(Math.Abs(k.NormX - 0.5) * 10.0 - t * speed * 3.0);
                return ((byte)(c.R * wave), (byte)(c.G * wave), (byte)(c.B * wave));
            }
            default:
                return (baseR, baseG, baseB);
        }
    }

    private static Color HsvToRgb(double h, double s, double v)
    {
        var (r, g, b) = ColorWheel.HsvToRgb(h, s, v);
        return Color.FromRgb(r, g, b);
    }

    private void BuildModeButtons()
    {
        ModesGrid.Children.Clear();
        _modeButtons.Clear();

        foreach (var mode in EvisionModes.All)
        {
            var stack = new StackPanel();
            var top = new DockPanel();

            var title = new TextBlock
            {
                Text = _isEnglish ? mode.NameEn : mode.NameRu,
                FontWeight = FontWeights.SemiBold,
                FontSize = 12.5,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            title.SetResourceReference(TextBlock.ForegroundProperty, "TokyoTextBrush");
            top.Children.Add(title);

            var badgeText = new TextBlock { Text = _isEnglish ? mode.CategoryEn : mode.Category, FontSize = 10 };
            badgeText.SetResourceReference(TextBlock.ForegroundProperty, "BadgeFgBrush");

            var badge = new Border
            {
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(6, 1, 6, 1),
                HorizontalAlignment = HorizontalAlignment.Right,
                Child = badgeText
            };
            badge.SetResourceReference(Border.BackgroundProperty, "BadgeBgBrush");
            top.Children.Add(badge);

            var sub = new TextBlock
            {
                Text = _isEnglish ? mode.CategoryEn : mode.NameEn,
                FontSize = 10.5,
                Margin = new Thickness(0, 2, 0, 0)
            };
            sub.SetResourceReference(TextBlock.ForegroundProperty, "TokyoMutedBrush");

            stack.Children.Add(top);
            stack.Children.Add(sub);

            var btn = new ToggleButton
            {
                Style = (Style)FindResource("ModeCardBtn"),
                Content = stack,
                Tag = mode.Code
            };
            btn.Click += ModeButton_Click;
            _modeButtons[mode.Code] = btn;
            ModesGrid.Children.Add(btn);
        }
    }

    private void BuildSwatches()
    {
        SwatchesPanel.Children.Clear();
        foreach (var (name, r, g, b) in TokyoSwatches)
        {
            var btn = new Button
            {
                Width = 22,
                Height = 22,
                Margin = new Thickness(0, 0, 5, 4),
                ToolTip = name,
                Cursor = Cursors.Hand,
                Background = new SolidColorBrush(Color.FromRgb(r, g, b)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(140, 82, 106)),
                BorderThickness = new Thickness(1),
                Template = CreateSwatchTemplate()
            };
            btn.Click += async (_, _) =>
            {
                SetRgbFromUser(r, g, b, syncWheel: true);
                if (!_isRamPreviewActive && _device.IsConnected)
                {
                    await ApplyCurrentModeToKeyboardAsync();
                }
            };
            SwatchesPanel.Children.Add(btn);
        }
    }

    private static ControlTemplate CreateSwatchTemplate()
    {
        var borderFactory = new FrameworkElementFactory(typeof(Border));
        borderFactory.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        borderFactory.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        borderFactory.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(11));
        return new ControlTemplate(typeof(Button)) { VisualTree = borderFactory };
    }

    private static byte InvertGamma(byte hardwareVal)
    {
        if (hardwareVal == 0) return 0;
        double norm = hardwareVal / 255.0;
        return (byte)Math.Clamp((int)Math.Round(Math.Pow(norm, 1.0 / LedGamma) * 255.0), 1, 255);
    }

    private void ApplyConfigToUi(KeyboardModeConfig cfg)
    {
        _suppressUiEvents = true;
        _selectedModeCode = cfg.Mode;
        if (!_modeButtons.ContainsKey(_selectedModeCode))
        {
            _selectedModeCode = 0x06;
        }
        foreach (var kv in _modeButtons)
        {
            kv.Value.IsChecked = kv.Key == _selectedModeCode;
        }

        BrightnessSlider.Value = Math.Clamp(cfg.Brightness, (byte)0, (byte)4);
        SpeedSlider.Value = Math.Clamp(6 - cfg.Speed, 1, 6);
        DirLeftRadio.IsChecked = cfg.Direction == 0;
        DirRightRadio.IsChecked = cfg.Direction != 0;
        RandomColorCheck.IsChecked = cfg.RandomColors;

        byte uiR = LedGammaCheck.IsChecked == true ? InvertGamma(cfg.Red) : cfg.Red;
        byte uiG = LedGammaCheck.IsChecked == true ? InvertGamma(cfg.Green) : cfg.Green;
        byte uiB = LedGammaCheck.IsChecked == true ? InvertGamma(cfg.Blue) : cfg.Blue;

        RedSlider.Value = uiR;
        GreenSlider.Value = uiG;
        BlueSlider.Value = uiB;
        WheelPicker.SetRgb(uiR, uiG, uiB);
        WheelValueSlider.Value = Math.Round(Math.Max(uiR, Math.Max(uiG, uiB)) / 255.0 * 100.0);
        if (cfg.Mode == 0x14)
        {
            _activeCustomSlot = Math.Min(cfg.LedMode, (byte)4);
            UpdateCustomSlotButtonsUi();
        }
        _suppressUiEvents = false;

        UpdateModeCapabilitiesUi();
        UpdateColorPreview();

        if (cfg.Mode == 0x14 && _device.IsConnected)
        {
            _ = LoadCustomSlotFromDeviceAsync(_activeCustomSlot, applyPresetIfEmpty: false);
        }
    }

    private void SetRgbFromUser(byte r, byte g, byte b, bool syncWheel)
    {
        _suppressUiEvents = true;
        RandomColorCheck.IsChecked = false;
        RedSlider.Value = r;
        GreenSlider.Value = g;
        BlueSlider.Value = b;
        if (syncWheel)
        {
            WheelPicker.SetRgb(r, g, b);
            WheelValueSlider.Value = Math.Round(Math.Max(r, Math.Max(g, b)) / 255.0 * 100.0);
        }
        _suppressUiEvents = false;
        UpdateColorPreview();
    }

    private void WheelPicker_ColorChanged(object? sender, EventArgs e)
    {
        if (!_isUiReady || _suppressUiEvents) return;
        var (r, g, b) = WheelPicker.SelectedRgb;
        _suppressUiEvents = true;
        WheelValueSlider.Value = Math.Round(WheelPicker.ValueFactor * 100.0);
        _suppressUiEvents = false;
        SetRgbFromUser(r, g, b, syncWheel: false);

        // Live RAM feedback while dragging on the wheel in Static mode (zero flash wear!)
        SendLiveWheelRamIfNeeded();
    }

    private void SendLiveWheelRamIfNeeded()
    {
        if (_device.IsConnected && (_selectedModeCode == 0x06 || _isRamPreviewActive))
        {
            long now = _animClock.ElapsedMilliseconds;
            if (now - _lastWheelRamMs >= 35)
            {
                _lastWheelRamMs = now;
                _isWheelRamDragging = true;
                var eff = GetEffectiveLedRgb(applyBrightness: true);
                try { _device.SendSolidDirectRam(eff.R, eff.G, eff.B); } catch { }
            }
        }
    }

    private async void WheelPicker_DragCompleted(object? sender, EventArgs e)
    {
        if (!_isUiReady) return;
        bool wasRamDragging = _isWheelRamDragging;
        _isWheelRamDragging = false;
        if (!_isRamPreviewActive && _device.IsConnected)
        {
            if (wasRamDragging)
            {
                await Task.Run(() => _device.EndDirectRam());
            }
            await ApplyCurrentModeToKeyboardAsync();
        }
    }

    private void WheelValueSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isUiReady || _suppressUiEvents) return;
        WheelPicker.ValueFactor = WheelValueSlider.Value / 100.0;
        var (r, g, b) = WheelPicker.SelectedRgb;
        SetRgbFromUser(r, g, b, syncWheel: false);
        SendLiveWheelRamIfNeeded();
    }

    private async void Slider_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (!_isUiReady || _isRamPreviewActive || !_device.IsConnected) return;
        bool wasRamDragging = _isWheelRamDragging;
        _isWheelRamDragging = false;
        if (wasRamDragging)
        {
            await Task.Run(() => _device.EndDirectRam());
        }
        await ApplyCurrentModeToKeyboardAsync();
    }

    private async void ModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton clicked && clicked.Tag is byte code)
        {
            StopRamPreview();
            _isWheelRamDragging = false;
            _selectedModeCode = code;
            foreach (var kv in _modeButtons)
            {
                kv.Value.IsChecked = kv.Key == code;
            }
            UpdateModeCapabilitiesUi();
            UpdateColorPreview();

            if (_device.IsConnected)
            {
                if (code == 0x14)
                {
                    await LoadCustomSlotFromDeviceAsync(_activeCustomSlot, applyPresetIfEmpty: true);
                }
                await ApplyCurrentModeToKeyboardAsync();
            }
        }
    }

    private void UpdateModeCapabilitiesUi()
    {
        if (!_isUiReady) return;
        var info = EvisionModes.FindByCode(_selectedModeCode);
        if (info == null) return;

        ColorPanel.IsEnabled = info.SupportsColor || _isRamPreviewActive;
        ColorPanel.Opacity = ColorPanel.IsEnabled ? 1.0 : 0.45;
        RandomColorCheck.IsEnabled = info.SupportsRandomColor && !_isRamPreviewActive;
        if (!info.SupportsRandomColor && info.SupportsColor)
        {
            RandomColorCheck.IsChecked = false;
        }
        ColorHintText.Text = _isRamPreviewActive
            ? Tr("RAM-статика", "RAM Static")
            : !info.SupportsColor
                ? Tr("Многоцветный режим", "Multi-color mode")
                : info.IsSurmountHue
                    ? Tr("4 оттенка (красн/жёлт/зел/бирюз)", "4 hues (red/yellow/green/cyan)")
                    : "";

        SpeedRow.IsEnabled = info.SupportsSpeed && !_isRamPreviewActive;
        SpeedRow.Opacity = SpeedRow.IsEnabled ? 1.0 : 0.45;
        DirectionRow.IsEnabled = info.SupportsDirection && !_isRamPreviewActive;
        DirectionRow.Opacity = DirectionRow.IsEnabled ? 1.0 : 0.45;

        bool showPerKey = _selectedModeCode == 0x14 && !_isRamPreviewActive;
        PerKeyToolbar.Visibility = showPerKey ? Visibility.Visible : Visibility.Collapsed;
        if (!showPerKey && _isCalibratingLed)
        {
            _isCalibratingLed = false;
            CalibrateToggleBtn.IsChecked = false;
            CalibrationBar.Visibility = Visibility.Collapsed;
        }

        BrightnessValText.Text = $"{(int)BrightnessSlider.Value} / 4";
        SpeedValText.Text = $"{(int)SpeedSlider.Value} / 6";
        string modeTitle = _isEnglish ? info.NameEn : info.NameRu;
        ActiveStateSummary.Text = _isRamPreviewActive
            ? Tr($"Быстрый предпросмотр • Яркость {(int)BrightnessSlider.Value}/4", $"Quick Preview • Brightness {(int)BrightnessSlider.Value}/4")
            : Tr($"Режим: {modeTitle} • Яркость {(int)BrightnessSlider.Value}/4", $"Mode: {modeTitle} • Brightness {(int)BrightnessSlider.Value}/4");
    }

    private (byte R, byte G, byte B) GetEffectiveLedRgb(bool applyBrightness)
    {
        double r = RedSlider.Value / 255.0;
        double g = GreenSlider.Value / 255.0;
        double b = BlueSlider.Value / 255.0;

        if (LedGammaCheck.IsChecked == true)
        {
            r = Math.Pow(r, LedGamma);
            g = Math.Pow(g, LedGamma);
            b = Math.Pow(b, LedGamma);
        }

        if (applyBrightness)
        {
            double br = BrightnessSlider.Value / 4.0;
            r *= br;
            g *= br;
            b *= br;
        }

        return (
            (byte)Math.Clamp((int)Math.Round(r * 255.0), 0, 255),
            (byte)Math.Clamp((int)Math.Round(g * 255.0), 0, 255),
            (byte)Math.Clamp((int)Math.Round(b * 255.0), 0, 255)
        );
    }

    private void UpdateColorPreview()
    {
        if (!_isUiReady) return;
        byte r = (byte)RedSlider.Value;
        byte g = (byte)GreenSlider.Value;
        byte b = (byte)BlueSlider.Value;

        RedValText.Text = r.ToString();
        GreenValText.Text = g.ToString();
        BlueValText.Text = b.ToString();

        var color = Color.FromRgb(r, g, b);
        ColorPreviewBox.Background = new SolidColorBrush(color);
        HexTextBox.Text = $"#{r:X2}{g:X2}{b:X2}";

        if (_isRamPreviewActive)
        {
            var eff = GetEffectiveLedRgb(applyBrightness: true);
            try { _device.SendSolidDirectRam(eff.R, eff.G, eff.B); } catch { }
        }
    }

    private KeyboardModeConfig BuildConfigFromUi()
    {
        var eff = GetEffectiveLedRgb(applyBrightness: false);
        return new KeyboardModeConfig
        {
            Mode = _selectedModeCode,
            Brightness = (byte)BrightnessSlider.Value,
            Speed = (byte)Math.Clamp(6 - (int)SpeedSlider.Value, 0, 5),
            Direction = (byte)(DirLeftRadio.IsChecked == true ? 0 : 1),
            RandomColors = RandomColorCheck.IsChecked == true,
            Red = eff.R,
            Green = eff.G,
            Blue = eff.B,
            LedMode = _selectedModeCode == 0x14 ? _activeCustomSlot : (byte)0,
        };
    }

    private void RgbSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isUiReady || _suppressUiEvents) return;
        _suppressUiEvents = true;
        RandomColorCheck.IsChecked = false;
        byte r = (byte)RedSlider.Value;
        byte g = (byte)GreenSlider.Value;
        byte b = (byte)BlueSlider.Value;
        WheelPicker.SetRgb(r, g, b);
        WheelValueSlider.Value = Math.Round(Math.Max(r, Math.Max(g, b)) / 255.0 * 100.0);
        _suppressUiEvents = false;
        UpdateColorPreview();
        SendLiveWheelRamIfNeeded();
    }

    private void ParamSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isUiReady || _suppressUiEvents) return;
        UpdateModeCapabilitiesUi();
        UpdateColorPreview();
    }

    private async void ColorOptionChanged(object sender, RoutedEventArgs e)
    {
        if (!_isUiReady || _suppressUiEvents) return;
        UpdateColorPreview();
        if (!_isRamPreviewActive && _device.IsConnected)
        {
            await ApplyCurrentModeToKeyboardAsync();
        }
    }

    private async void DirectionChanged(object sender, RoutedEventArgs e)
    {
        if (!_isUiReady || _suppressUiEvents) return;
        UpdateModeCapabilitiesUi();
        if (!_isRamPreviewActive && _device.IsConnected)
        {
            await ApplyCurrentModeToKeyboardAsync();
        }
    }

    private void HexTextBox_LostFocus(object sender, RoutedEventArgs e) => TryParseHexInput();

    private async void HexTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            TryParseHexInput();
            if (!_isRamPreviewActive && _device.IsConnected)
            {
                await ApplyCurrentModeToKeyboardAsync();
            }
        }
    }

    private void TryParseHexInput()
    {
        string raw = HexTextBox.Text.Trim().TrimStart('#');
        if (raw.Length == 6 &&
            byte.TryParse(raw.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r) &&
            byte.TryParse(raw.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g) &&
            byte.TryParse(raw.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
        {
            SetRgbFromUser(r, g, b, syncWheel: true);
        }
    }

    private async void ReconnectBtn_Click(object sender, RoutedEventArgs e)
    {
        StopRamPreview();
        await ConnectAndReadInitialAsync();
    }

    private async void ApplyBtn_Click(object sender, RoutedEventArgs e)
    {
        StopRamPreview();
        await ApplyCurrentModeToKeyboardAsync();
    }

    private async Task ApplyCurrentModeToKeyboardAsync()
    {
        if (_isApplying)
        {
            _applyPending = true;
            return;
        }
        _isApplying = true;
        try
        {
            do
            {
                _applyPending = false;
                var cfg = BuildConfigFromUi();
                byte[]? customBuf = cfg.Mode == 0x14 ? BuildPerKeyHardwareBuffer(applyBrightness: false) : null;
                byte slot = _activeCustomSlot;
                var actual = await Task.Run(() =>
                {
                    if (customBuf != null)
                    {
                        _device.WriteCustomColorsRaw(slot, customBuf);
                    }
                    return _device.ApplyHardwareMode(cfg);
                });
                _animClock.Restart();
                _nextAutoPulseSec = 0.4;
                var info = EvisionModes.FindByCode(actual.Mode);
                FooterLogText.Text = actual.Mode == 0x14
                    ? $"Активна поклавишная раскладка (Слот {slot + 1}) • Яркость {actual.Brightness}/4."
                    : $"Активен на клавиатуре: {info?.NameRu ?? $"0x{actual.Mode:X2}"} • Яркость {actual.Brightness}/4 • Скорость {6 - actual.Speed}/6 • RGB({actual.Red},{actual.Green},{actual.Blue}).";
            }
            while (_applyPending && !_isRamPreviewActive);
        }
        catch (Exception ex)
        {
            FooterLogText.Text = $"Ошибка применения: {ex.Message}";
        }
        finally
        {
            _isApplying = false;
        }
    }

    private async void RestoreBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_initialConfig == null) return;
        StopRamPreview();
        try
        {
            var cfg = _initialConfig.Clone();
            await Task.Run(() => _device.ApplyHardwareMode(cfg));
            ApplyConfigToUi(cfg);
            FooterLogText.Text = "Восстановлен исходный режим, который был при запуске приложения.";
        }
        catch (Exception ex)
        {
            FooterLogText.Text = $"Ошибка восстановления: {ex.Message}";
        }
    }

    private async void OffBtn_Click(object sender, RoutedEventArgs e)
    {
        StopRamPreview();
        _suppressUiEvents = true;
        BrightnessSlider.Value = 0;
        _suppressUiEvents = false;
        UpdateModeCapabilitiesUi();
        UpdateColorPreview();
        await ApplyCurrentModeToKeyboardAsync();
    }

    private void RamPreviewBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isRamPreviewActive)
        {
            StopRamPreview();
            UpdateModeCapabilitiesUi();
            FooterLogText.Text = Tr(
                "Предпросмотр остановлен, возвращён выбранный режим.",
                "Preview stopped, restored selected mode.");
            return;
        }

        try
        {
            var eff = GetEffectiveLedRgb(applyBrightness: true);
            _device.SendSolidDirectRam(eff.R, eff.G, eff.B);

            _isRamPreviewActive = true;
            RamPreviewBtn.Content = Tr("Остановить предпросмотр", "Stop Preview");
            UpdateModeCapabilitiesUi();
            FooterLogText.Text = Tr(
                "Быстрый предпросмотр цвета активен.",
                "Quick color preview is active.");

            _ramPreviewCts = new CancellationTokenSource();
            var token = _ramPreviewCts.Token;
            Task.Run(async () =>
            {
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        await Task.Delay(250, token).ConfigureAwait(false);
                        if (token.IsCancellationRequested) break;
                        await Dispatcher.InvokeAsync(() =>
                        {
                            if (!_isRamPreviewActive || token.IsCancellationRequested) return;
                            try
                            {
                                var live = GetEffectiveLedRgb(applyBrightness: true);
                                _device.SendSolidDirectRam(live.R, live.G, live.B);
                            }
                            catch
                            {
                                StopRamPreview();
                            }
                        });
                    }
                }
                catch (OperationCanceledException)
                {
                }
            }, token);
        }
        catch (Exception ex)
        {
            StopRamPreview();
            FooterLogText.Text = Tr($"Ошибка предпросмотра: {ex.Message}", $"Preview error: {ex.Message}");
        }
    }

    private void StopRamPreview()
    {
        if (!_isRamPreviewActive && _ramPreviewCts == null) return;
        _isRamPreviewActive = false;
        _ramPreviewCts?.Cancel();
        _ramPreviewCts?.Dispose();
        _ramPreviewCts = null;
        RamPreviewBtn.Content = Tr("Быстрый предпросмотр цвета", "Quick Color Preview");
        _device.EndDirectRam();
    }

    #region Per-Key Custom Coloring & Calibration

    private static int[] CreateDefault61KeyMap()
    {
        // Standard 5x17 EVision V2 60% matrix layout (85 slots total)
        int[] map = new int[61];
        int i = 0;
        for (int c = 0; c < 14; c++) map[i++] = 0 + c;       // Row 0: 14 keys
        for (int c = 0; c < 14; c++) map[i++] = 17 + c;      // Row 1: 14 keys
        for (int c = 0; c < 13; c++) map[i++] = 34 + c;      // Row 2: 13 keys
        for (int c = 0; c < 12; c++) map[i++] = 51 + c;      // Row 3: 12 keys
        int[] row4 = { 68, 69, 70, 73, 76, 77, 78, 79 };     // Row 4: 8 keys
        foreach (int s in row4) map[i++] = s;
        return map;
    }

    private void LoadLedMap()
    {
        try
        {
            if (File.Exists(LedMapFilePath))
            {
                var loaded = JsonSerializer.Deserialize<int[]>(File.ReadAllText(LedMapFilePath));
                if (loaded != null && loaded.Length == 61)
                {
                    _keyLedMap = loaded;
                }
            }
        }
        catch { }
    }

    private void SaveLedMap()
    {
        try
        {
            File.WriteAllText(LedMapFilePath, JsonSerializer.Serialize(_keyLedMap));
        }
        catch { }
    }

    private void BuildCustomSlotButtons()
    {
        CustomSlotButtonsPanel.Children.Clear();
        _customSlotButtons.Clear();
        for (byte s = 0; s < 5; s++)
        {
            byte captured = s;
            var btn = new ToggleButton
            {
                Content = (s + 1).ToString(),
                Width = 26,
                Height = 24,
                Margin = new Thickness(0, 0, 4, 0),
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                IsChecked = s == _activeCustomSlot,
                Cursor = Cursors.Hand,
                Tag = captured
            };
            btn.Style = BrushToolBtn.Style;
            btn.Click += async (_, _) =>
            {
                _activeCustomSlot = captured;
                UpdateCustomSlotButtonsUi();
                if (_device.IsConnected)
                {
                    await LoadCustomSlotFromDeviceAsync(_activeCustomSlot, applyPresetIfEmpty: false);
                    await ApplyCurrentModeToKeyboardAsync();
                }
            };
            _customSlotButtons.Add(btn);
            CustomSlotButtonsPanel.Children.Add(btn);
        }
    }

    private void UpdateCustomSlotButtonsUi()
    {
        for (int i = 0; i < _customSlotButtons.Count; i++)
        {
            _customSlotButtons[i].IsChecked = i == _activeCustomSlot;
        }
    }

    private void PerKeyToolBtn_Click(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, BrushToolBtn))
        {
            BrushToolBtn.IsChecked = true;
            EraserToolBtn.IsChecked = false;
        }
        else
        {
            BrushToolBtn.IsChecked = false;
            EraserToolBtn.IsChecked = true;
        }
    }

    private void PaintPreviewKey(PreviewKey key, bool erase)
    {
        bool useEraser = erase || EraserToolBtn.IsChecked == true;
        if (useEraser)
        {
            key.CustomR = 0;
            key.CustomG = 0;
            key.CustomB = 0;
        }
        else
        {
            var (r, g, b) = GetEffectiveLedRgb(applyBrightness: false);
            key.CustomR = r;
            key.CustomG = g;
            key.CustomB = b;
        }

        SendPerKeyLiveRamThrottled();
    }

    private void SendPerKeyLiveRamThrottled()
    {
        if (!_device.IsConnected || _selectedModeCode != 0x14) return;
        long now = _animClock.ElapsedMilliseconds;
        if (now - _lastPerKeyRamMs < 35) return;
        _lastPerKeyRamMs = now;
        try
        {
            _device.SendPerKeyDirectRam(BuildPerKeyHardwareBuffer(applyBrightness: true));
        }
        catch { }
    }

    private byte[] BuildPerKeyHardwareBuffer(bool applyBrightness)
    {
        int mapSize = Math.Max(85, _device.MapSize);
        byte[] buf = new byte[mapSize * 3];
        double br = applyBrightness ? BrightnessSlider.Value / 4.0 : 1.0;

        for (int i = 0; i < _previewKeys.Count && i < _keyLedMap.Length; i++)
        {
            int slot = _keyLedMap[i];
            if (slot < 0 || slot >= mapSize) continue;
            var pk = _previewKeys[i];
            buf[slot * 3 + 0] = (byte)Math.Clamp((int)Math.Round(pk.CustomR * br), 0, 255);
            buf[slot * 3 + 1] = (byte)Math.Clamp((int)Math.Round(pk.CustomG * br), 0, 255);
            buf[slot * 3 + 2] = (byte)Math.Clamp((int)Math.Round(pk.CustomB * br), 0, 255);
        }
        return buf;
    }

    private async Task LoadCustomSlotFromDeviceAsync(byte slot, bool applyPresetIfEmpty)
    {
        try
        {
            byte[] raw = await Task.Run(() => _device.ReadCustomColorsRaw(slot));
            bool hasAnyColor = false;
            for (int i = 0; i < raw.Length; i++)
            {
                if (raw[i] > 0) { hasAnyColor = true; break; }
            }

            if (!hasAnyColor && applyPresetIfEmpty)
            {
                ApplyTokyoPerKeyPreset(sendRam: true);
                return;
            }

            for (int i = 0; i < _previewKeys.Count && i < _keyLedMap.Length; i++)
            {
                int ledIdx = _keyLedMap[i];
                if (ledIdx >= 0 && ledIdx * 3 + 2 < raw.Length)
                {
                    _previewKeys[i].CustomR = raw[ledIdx * 3 + 0];
                    _previewKeys[i].CustomG = raw[ledIdx * 3 + 1];
                    _previewKeys[i].CustomB = raw[ledIdx * 3 + 2];
                }
            }
            FooterLogText.Text = $"Загружен слот Custom {slot + 1} с клавиатуры.";
        }
        catch (Exception ex)
        {
            FooterLogText.Text = $"Не удалось считать слот {slot + 1}: {ex.Message}";
        }
    }

    private void PerKeyFillAllBtn_Click(object sender, RoutedEventArgs e)
    {
        var (r, g, b) = GetEffectiveLedRgb(applyBrightness: false);
        foreach (var k in _previewKeys)
        {
            k.CustomR = r;
            k.CustomG = g;
            k.CustomB = b;
        }
        PushPerKeyRamNow();
    }

    private void PerKeyClearBtn_Click(object sender, RoutedEventArgs e)
    {
        foreach (var k in _previewKeys)
        {
            k.CustomR = 0;
            k.CustomG = 0;
            k.CustomB = 0;
        }
        PushPerKeyRamNow();
    }

    private void PerKeyPresetTokyo_Click(object sender, RoutedEventArgs e) => ApplyTokyoPerKeyPreset(sendRam: true);

    private void ApplyTokyoPerKeyPreset(bool sendRam)
    {
        foreach (var k in _previewKeys)
        {
            if (k.Label == "桜" || k.Label == "Enter" || string.IsNullOrEmpty(k.Label))
            {
                // Sakura & Mt Fuji / Koi highlights
                k.CustomR = 255; k.CustomG = 45; k.CustomB = 115;
            }
            else if (k.IsPinkKeycap)
            {
                // Pink modifiers
                k.CustomR = 255; k.CustomG = 95; k.CustomB = 155;
            }
            else
            {
                // Soft warm ice-white alpha keys
                k.CustomR = 255; k.CustomG = 215; k.CustomB = 235;
            }
        }
        if (sendRam) PushPerKeyRamNow();
    }

    private void PerKeyPresetWasd_Click(object sender, RoutedEventArgs e)
    {
        foreach (var k in _previewKeys)
        {
            if (k.Label is "W" or "A" or "S" or "D")
            {
                k.CustomR = 255; k.CustomG = 40; k.CustomB = 110;
            }
            else if (k.Label is "1" or "2" or "3" or "4" or "5")
            {
                k.CustomR = 0; k.CustomG = 225; k.CustomB = 200;
            }
            else if (k.Label is "⇧ Shift" or "Control" or "")
            {
                k.CustomR = 175; k.CustomG = 50; k.CustomB = 255;
            }
            else
            {
                k.CustomR = 24; k.CustomG = 14; k.CustomB = 34;
            }
        }
        PushPerKeyRamNow();
    }

    private void PerKeyPresetRainbow_Click(object sender, RoutedEventArgs e)
    {
        foreach (var k in _previewKeys)
        {
            var c = HsvToRgb((k.NormX * 0.85 + k.NormY * 0.15) % 1.0, 1.0, 1.0);
            k.CustomR = c.R;
            k.CustomG = c.G;
            k.CustomB = c.B;
        }
        PushPerKeyRamNow();
    }

    private void PushPerKeyRamNow()
    {
        if (!_device.IsConnected || _selectedModeCode != 0x14) return;
        try
        {
            _device.SendPerKeyDirectRam(BuildPerKeyHardwareBuffer(applyBrightness: true));
        }
        catch { }
    }

    private async void CalibrateToggleBtn_Click(object sender, RoutedEventArgs e)
    {
        _isCalibratingLed = CalibrateToggleBtn.IsChecked == true;
        CalibrationBar.Visibility = _isCalibratingLed ? Visibility.Visible : Visibility.Collapsed;
        if (_isCalibratingLed)
        {
            _calibSlot = 0;
            UpdateCalibrationRam();
        }
        else if (_device.IsConnected)
        {
            await Task.Run(() => _device.EndDirectRam());
            PushPerKeyRamNow();
        }
    }

    private void CalibPrevSlot_Click(object sender, RoutedEventArgs e)
    {
        int max = Math.Max(1, _device.MapSize);
        _calibSlot = (_calibSlot - 1 + max) % max;
        UpdateCalibrationRam();
    }

    private void CalibNextSlot_Click(object sender, RoutedEventArgs e)
    {
        int max = Math.Max(1, _device.MapSize);
        _calibSlot = (_calibSlot + 1) % max;
        UpdateCalibrationRam();
    }

    private void CalibResetMap_Click(object sender, RoutedEventArgs e)
    {
        _keyLedMap = CreateDefault61KeyMap();
        SaveLedMap();
        UpdateCalibrationRam();
        FooterLogText.Text = "Карта LED сброшена к стандартной 60% матрице.";
    }

    private void BindCalibSlotToKey(int keyIndex)
    {
        if (keyIndex < 0 || keyIndex >= _keyLedMap.Length) return;
        _keyLedMap[keyIndex] = _calibSlot;
        SaveLedMap();
        FooterLogText.Text = $"Клавиша «{_previewKeys[keyIndex].Label}» привязана к LED-слоту #{_calibSlot}.";
        int max = Math.Max(1, _device.MapSize);
        _calibSlot = (_calibSlot + 1) % max;
        UpdateCalibrationRam();
    }

    private void UpdateCalibrationRam()
    {
        int max = Math.Max(1, _device.MapSize);
        CalibSlotText.Text = $"Слот {_calibSlot} / {max - 1}";
        if (!_device.IsConnected) return;
        byte[] buf = new byte[max * 3];
        if (_calibSlot >= 0 && _calibSlot < max)
        {
            buf[_calibSlot * 3 + 0] = 255;
            buf[_calibSlot * 3 + 1] = 80;
            buf[_calibSlot * 3 + 2] = 160;
        }
        try { _device.SendPerKeyDirectRam(buf); } catch { }
    }

    #endregion
}
