// Ported from DesktopSbS: DesktopSbS/View/Theme/ThemeManager.cs
using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace ImmersiveSBS.Theme
{
    /// <summary>
    /// Windows 11 look for the app's own windows (About, Settings, dialogs),
    /// without a third-party UI library: the app ships as a single folder,
    /// so an extra DLL would be missing from installed copies.
    ///
    /// Follows the system light/dark app mode and accent colour, and switches
    /// live when either changes. Windows opt in with
    /// <c>theme:ThemeManager.IsEnabled="True"</c>, which applies the window style,
    /// the Mica backdrop on Windows 11 22H2+ and a matching title bar.
    /// </summary>
    public static class ThemeManager
    {
        private static readonly Uri fluentUri = new Uri("pack://application:,,,/ImmersiveSBS;component/Theme/Fluent.xaml");
        private static readonly Uri lightUri = new Uri("pack://application:,,,/ImmersiveSBS;component/Theme/Light.xaml");
        private static readonly Uri darkUri = new Uri("pack://application:,,,/ImmersiveSBS;component/Theme/Dark.xaml");

        private static ResourceDictionary? paletteDictionary;
        private static ResourceDictionary? accentDictionary;
        private static bool listening;

        /// <summary>True when Windows is set to dark mode for apps.</summary>
        public static bool IsDark { get; private set; }

        /// <summary>
        /// Forces light (false) or dark (true) instead of following Windows; null
        /// follows the system. For previewing both themes; call Refresh() after.
        /// </summary>
        public static bool? ForceDark { get; set; }

        /// <summary>Accent fill for the current mode (the one buttons and checkboxes use).</summary>
        public static Color AccentFill { get; private set; }

        /// <summary>Text colour to draw on <see cref="AccentFill"/>.</summary>
        public static Color TextOnAccent { get; private set; }

        /// <summary>
        /// Loads the theme into <paramref name="application"/>'s resources.
        /// Call once, before any themed window is created.
        /// </summary>
        public static void Apply(Application application)
        {
            ResourceDictionary resources = application.Resources;

            paletteDictionary = new ResourceDictionary();
            accentDictionary = new ResourceDictionary();

            resources.MergedDictionaries.Add(paletteDictionary);
            resources.MergedDictionaries.Add(accentDictionary);
            resources.MergedDictionaries.Add(new ResourceDictionary { Source = fluentUri });

            Refresh();

            if (!listening)
            {
                listening = true;
                SystemEvents.UserPreferenceChanged += (s, e) =>
                {
                    // Dark mode and accent changes arrive as General / Color.
                    if (e.Category == UserPreferenceCategory.General || e.Category == UserPreferenceCategory.Color)
                    {
                        application.Dispatcher.BeginInvoke(new Action(Refresh));
                    }
                };
            }
        }

        /// <summary>Re-reads the system theme and updates every themed window.</summary>
        public static void Refresh()
        {
            if (paletteDictionary == null) return;

            IsDark = ForceDark ?? ReadIsDark();

            paletteDictionary.MergedDictionaries.Clear();
            paletteDictionary.MergedDictionaries.Add(new ResourceDictionary { Source = IsDark ? darkUri : lightUri });

            Color[] palette = ReadAccentPalette();
            // WinUI mapping: light mode fills with Dark1, dark mode with Light2;
            // links use one step further from the background.
            AccentFill = IsDark ? palette[1] : palette[4];
            Color accentHover = IsDark ? palette[2] : palette[5];
            Color accentText = IsDark ? palette[0] : palette[5];
            TextOnAccent = IsDark ? Colors.Black : Colors.White;

            SetBrush(accentDictionary!, "AccentFillBrush", AccentFill);
            SetBrush(accentDictionary!, "AccentFillHoverBrush", accentHover);
            SetBrush(accentDictionary!, "AccentFillPressedBrush", Color.FromArgb(0xCC, AccentFill.R, AccentFill.G, AccentFill.B));
            SetBrush(accentDictionary!, "AccentTextBrush", accentText);
            SetBrush(accentDictionary!, "TextOnAccentBrush", TextOnAccent);
            SetBrush(accentDictionary!, "AccentSubtleBrush", Color.FromArgb(0x33, AccentFill.R, AccentFill.G, AccentFill.B));

            if (Application.Current != null)
            {
                foreach (Window window in Application.Current.Windows.OfType<Window>().Where(GetIsEnabled))
                {
                    ApplyWindowChrome(window);
                }
            }
        }

        private static void SetBrush(ResourceDictionary dictionary, string key, Color color)
        {
            SolidColorBrush brush = new SolidColorBrush(color);
            brush.Freeze();
            dictionary[key] = brush;
        }

        #region Attached property

        public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
            "IsEnabled", typeof(bool), typeof(ThemeManager), new PropertyMetadata(false, OnIsEnabledChanged));

        public static bool GetIsEnabled(DependencyObject element)
        {
            return (bool)element.GetValue(IsEnabledProperty);
        }

        public static void SetIsEnabled(DependencyObject element, bool value)
        {
            element.SetValue(IsEnabledProperty, value);
        }

        private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            Window? window = d as Window;
            if (window == null || !(bool)e.NewValue) return;

            window.SetResourceReference(FrameworkElement.StyleProperty, "FluentWindowStyle");

            if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
            {
                ApplyWindowChrome(window);
            }
            else
            {
                window.SourceInitialized += (s, args) => ApplyWindowChrome(window);
            }
        }

        #endregion

        #region Window chrome

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
        private const int DWMSBT_MAINWINDOW = 2; // Mica

        [StructLayout(LayoutKind.Sequential)]
        private struct MARGINS
        {
            public int Left, Right, Top, Bottom;
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        [DllImport("dwmapi.dll")]
        private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

        /// <summary>
        /// Mica needs Windows 11 22H2 (build 22621): DWMWA_SYSTEMBACKDROP_TYPE does
        /// not exist before it. Older systems get the solid window background.
        /// </summary>
        public static bool SupportsMica
        {
            get
            {
                Version v = Environment.OSVersion.Version;
                return v.Major >= 10 && v.Build >= 22621;
            }
        }

        private static void ApplyWindowChrome(Window window)
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            // Title bar colour follows the app mode (Windows 10 2004+ / 11).
            int dark = IsDark ? 1 : 0;
            if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref dark, sizeof(int));
            }

            bool mica = false;
            if (SupportsMica)
            {
                // Mica shows through wherever WPF draws nothing, so the client area
                // must be glass (frame extended) and WPF must clear to transparent.
                HwndSource? source = HwndSource.FromHwnd(hwnd);
                MARGINS margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
                int backdrop = DWMSBT_MAINWINDOW;

                if (source?.CompositionTarget != null
                    && DwmExtendFrameIntoClientArea(hwnd, ref margins) == 0
                    && DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int)) == 0)
                {
                    source.CompositionTarget.BackgroundColor = Colors.Transparent;
                    mica = true;
                }
            }

            // Without Mica the window paints its own solid Windows 11 background.
            if (mica)
            {
                window.Background = Brushes.Transparent;
            }
            else
            {
                window.SetResourceReference(Window.BackgroundProperty, "WindowBackgroundBrush");
            }
        }

        #endregion

        #region System settings

        private static bool ReadIsDark()
        {
            try
            {
                using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object? value = key?.GetValue("AppsUseLightTheme");
                    return value is int i && i == 0;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// The seven accent shades Windows derives from the user's accent colour:
        /// [0] Light3, [1] Light2, [2] Light1, [3] base, [4] Dark1, [5] Dark2,
        /// [6] Dark3. Read from the registry, or derived from the base colour.
        /// </summary>
        private static Color[] ReadAccentPalette()
        {
            try
            {
                using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent"))
                {
                    if (key?.GetValue("AccentPalette") is byte[] bytes && bytes.Length >= 28)
                    {
                        Color[] result = new Color[7];
                        for (int i = 0; i < 7; ++i)
                        {
                            result[i] = Color.FromRgb(bytes[i * 4], bytes[i * 4 + 1], bytes[i * 4 + 2]);
                        }
                        return result;
                    }
                }
            }
            catch
            {
                // Fall through to a derived palette.
            }

            Color baseColor = SystemParameters.WindowGlassColor;
            if (baseColor.A == 0) baseColor = Color.FromRgb(0x00, 0x78, 0xD4);
            baseColor.A = 0xFF;

            return new[]
            {
                Mix(baseColor, Colors.White, 0.60),
                Mix(baseColor, Colors.White, 0.40),
                Mix(baseColor, Colors.White, 0.20),
                baseColor,
                Mix(baseColor, Colors.Black, 0.20),
                Mix(baseColor, Colors.Black, 0.40),
                Mix(baseColor, Colors.Black, 0.60)
            };
        }

        private static Color Mix(Color a, Color b, double t)
        {
            return Color.FromRgb(
                (byte)(a.R + (b.R - a.R) * t),
                (byte)(a.G + (b.G - a.G) * t),
                (byte)(a.B + (b.B - a.B) * t));
        }

        #endregion
    }
}
