// Ported from DesktopSbS: DesktopSbS/View/Theme/FluentMenuRenderer.cs
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ImmersiveSBS.Theme
{
    /// <summary>
    /// Draws a WinForms context menu (the tray menu) in the Windows 11 style:
    /// theme-coloured background, rounded hover highlight, Fluent checkmarks,
    /// rounded window corners. Colours are read from <see cref="ThemeManager"/>
    /// at paint time, so a light/dark switch applies the next time it opens.
    /// </summary>
    public sealed class FluentMenuRenderer : ToolStripRenderer
    {
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_ROUND = 2;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        private static bool Dark => ThemeManager.IsDark;

        private static Color Background => Dark ? Color.FromArgb(0x2C, 0x2C, 0x2C) : Color.FromArgb(0xF9, 0xF9, 0xF9);
        private static Color Hover => Dark ? Color.FromArgb(0x3A, 0x3A, 0x3A) : Color.FromArgb(0xEA, 0xEA, 0xEA);
        private static Color Text => Dark ? Color.White : Color.FromArgb(0x1B, 0x1B, 0x1B);
        private static Color TextSecondary => Dark ? Color.FromArgb(0xC5, 0xC5, 0xC5) : Color.FromArgb(0x60, 0x60, 0x60);
        private static Color TextDisabled => Dark ? Color.FromArgb(0x78, 0x78, 0x78) : Color.FromArgb(0xA0, 0xA0, 0xA0);
        private static Color Divider => Dark ? Color.FromArgb(0x40, 0x40, 0x40) : Color.FromArgb(0xE0, 0xE0, 0xE0);

        private static Color Accent
        {
            get
            {
                System.Windows.Media.Color c = ThemeManager.AccentFill;
                return c.A == 0 ? Color.FromArgb(0x00, 0x67, 0xC0) : Color.FromArgb(c.R, c.G, c.B);
            }
        }

        /// <summary>
        /// Applies the renderer, font and spacing to a menu and its submenus.
        /// Call after the items are added.
        /// </summary>
        public static void Apply(ContextMenuStrip menu)
        {
            FluentMenuRenderer renderer = new FluentMenuRenderer();
            Font font = new Font("Segoe UI Variable Text", 14.75f);
            if (font.Name != "Segoe UI Variable Text")
            {
                font.Dispose();
                font = new Font("Segoe UI", 14.75f);
            }

            Style(menu, renderer, font);
        }

        private static void Style(ToolStripDropDownMenu menu, ToolStripRenderer renderer, Font font)
        {
            menu.Renderer = renderer;
            menu.Font = font;
            menu.ShowImageMargin = false;
            menu.ShowCheckMargin = true;
            menu.Padding = new Padding(4, 4, 4, 4);
            menu.DropShadowEnabled = true;
            menu.Opened += (s, e) =>
            {
                RoundCorners(menu);
                AlignSubmenus(menu);
            };

            foreach (ToolStripItem item in menu.Items)
            {
                if (item is ToolStripMenuItem menuItem)
                {
                    menuItem.Padding = new Padding(4, 6, 12, 6);
                    if (menuItem.HasDropDownItems && menuItem.DropDown is ToolStripDropDownMenu sub)
                    {
                        Style(sub, renderer, font);
                    }
                }
                else if (item is ToolStripSeparator separator)
                {
                    separator.Margin = new Padding(0, 4, 0, 4);
                }
            }
        }

        /// <summary>
        /// Opens every submenu of a menu in the same direction. WinForms decides
        /// per submenu whether it fits before the screen edge, so with the tray
        /// icon at the right edge a narrow submenu opened to the right and a wider
        /// one to the left. The widest submenu decides for all of them.
        /// </summary>
        private static void AlignSubmenus(ToolStripDropDownMenu menu)
        {
            int widest = 0;
            foreach (ToolStripItem item in menu.Items)
            {
                ToolStripMenuItem? menuItem = item as ToolStripMenuItem;
                if (menuItem != null && menuItem.HasDropDownItems)
                {
                    widest = Math.Max(widest, menuItem.DropDown.GetPreferredSize(Size.Empty).Width);
                }
            }
            if (widest == 0) return;

            Rectangle area = Screen.FromRectangle(menu.Bounds).WorkingArea;
            ToolStripDropDownDirection direction = menu.Bounds.Right + widest <= area.Right
                ? ToolStripDropDownDirection.Right
                : ToolStripDropDownDirection.Left;

            foreach (ToolStripItem item in menu.Items)
            {
                ToolStripMenuItem? menuItem = item as ToolStripMenuItem;
                if (menuItem != null && menuItem.HasDropDownItems) menuItem.DropDownDirection = direction;
            }
        }

        private static void RoundCorners(Control control)
        {
            if (!control.IsHandleCreated) return;
            int preference = DWMWCP_ROUND;
            // Windows 11 only; the call simply fails on Windows 10.
            DwmSetWindowAttribute(control.Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using (SolidBrush brush = new SolidBrush(Background))
            {
                e.Graphics.FillRectangle(brush, e.AffectedBounds);
            }
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            // Windows 11 draws the border of a rounded window itself.
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
        {
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;

            Rectangle r = new Rectangle(2, 1, e.Item.Width - 8, e.Item.Height - 2);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = RoundedRect(r, 4))
            using (SolidBrush brush = new SolidBrush(Hover))
            {
                e.Graphics.FillPath(brush, path);
            }
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            bool isStatus = !e.Item.Enabled && e.Item.Font.Bold;
            e.TextColor = isStatus ? TextSecondary : (e.Item.Enabled ? Text : TextDisabled);
            e.TextFormat |= TextFormatFlags.VerticalCenter;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            // Checkmark glyph in the accent colour, like WinUI's RadioMenuFlyoutItem.
            Rectangle r = e.ImageRectangle;
            using (Font glyph = new Font("Segoe Fluent Icons", 12f))
            {
                Font? use = glyph.Name == "Segoe Fluent Icons" ? glyph : null;
                using (Font? fallback = use == null ? new Font("Segoe MDL2 Assets", 12f) : null)
                {
                    TextRenderer.DrawText(e.Graphics, "", use ?? fallback!,
                        new Rectangle(r.X - 2, 0, r.Width + 4, e.Item.Height), Accent,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                }
            }
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using (Pen pen = new Pen(Divider))
            {
                e.Graphics.DrawLine(pen, 4, y, e.Item.Width - 4, y);
            }
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = e.Item is { Enabled: true } ? TextSecondary : TextDisabled;
            base.OnRenderArrow(e);
        }

        private static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            int d = radius * 2;
            GraphicsPath path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
