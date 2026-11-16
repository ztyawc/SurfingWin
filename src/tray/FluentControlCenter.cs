using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace SurfingWin.Tray
{
    // The existing WinForms message loop owns this modeless WPF window. No browser,
    // extra process, downloaded UI assets, or third-party controls are needed.
    internal sealed class FluentControlCenter : Window, IDisposable
    {
        private readonly Dictionary<string, Button> buttons = new Dictionary<string, Button>();
        private readonly TextBlock heading, version, badgeText, tunText, detail, feedback;
        private readonly Border surface, badge, exitOptions;
        private readonly System.Windows.Shapes.Ellipse tunDot;
        private HwndSource source;
        private bool disposing, positioning, dark, highContrast;
        private bool? appearanceOverride;
        private DateTime focusLostAt = DateTime.MinValue;
        private Drawing.Point anchor;
        internal event Action<string> ActionRequested;
        internal event Action<string> ModeRequested;
        internal bool DarkAppearance { get { return dark; } }
        internal bool JustLostFocus { get { return (DateTime.UtcNow - focusLostAt).TotalMilliseconds < 200; } }
        internal string Heading { get { return heading.Text; } }
        internal string TunStatus { get { return tunText.Text; } }
        internal string Detail { get { return detail.Text; } }
        internal bool AnimationEnabled { get { return SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast; } }
        internal Button Item(string id) { return buttons[id]; }

        internal FluentControlCenter()
        {
            Title = "SurfingWin 控制中心";
            Width = 336; SizeToContent = SizeToContent.Height;
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true; Background = Brushes.Transparent;
            ShowInTaskbar = false; Topmost = true;
            WindowStartupLocation = WindowStartupLocation.Manual;
            FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"); FontSize = 13;
            UseLayoutRounding = true; SnapsToDevicePixels = true;
            Resources = (ResourceDictionary)XamlReader.Parse(Styles);
            ApplyTheme(null);
            SetResourceReference(ForegroundProperty, "Text");
            AutomationProperties.SetName(this, "SurfingWin 快捷控制中心");

            var content = new StackPanel { Margin = new Thickness(14) };
            var header = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var brand = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var logoGlyph = new FluentGlyph("wave") { Width = 20, Height = 20 };
            logoGlyph.SetResourceReference(FluentGlyph.InkProperty, "OnAccent");
            var logo = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(9), Margin = new Thickness(0, 0, 8, 0), Child = logoGlyph };
            logo.SetResourceReference(Border.BackgroundProperty, "Accent"); brand.Children.Add(logo);
            var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            name.Children.Add(Text("SurfingWin", 14, "Text", true)); name.Children.Add(Text("快捷控制中心", 11, "Muted"));
            brand.Children.Add(name); header.Children.Add(brand);
            var headerActions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            badgeText = Text("正在检查", 11, "Muted");
            badge = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 3, 6, 3),
                VerticalAlignment = VerticalAlignment.Center, Child = badgeText };
            headerActions.Children.Add(badge);
            var more = MakeButton("more", new FluentGlyph("more"), "Quiet", "展开或收起退出选项");
            more.Width = 28; more.Height = 28; more.Padding = new Thickness(4); more.Margin = new Thickness(2, 0, -6, 0);
            more.Click += delegate
            {
                exitOptions.Visibility = exitOptions.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
                Dispatcher.BeginInvoke(new Action(PositionNearAnchor), DispatcherPriority.Loaded);
            };
            headerActions.Children.Add(more); Grid.SetColumn(headerActions, 1); header.Children.Add(headerActions);
            content.Children.Add(header);

            var hero = new Grid(); hero.ColumnDefinitions.Add(new ColumnDefinition());
            hero.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            heading = Text("正在检查…", 18, "Text", true); version = Text("正在读取本项目内核状态", 11, "Muted");
            version.TextWrapping = TextWrapping.Wrap; version.Margin = new Thickness(0, 3, 0, 0);
            titles.Children.Add(heading); titles.Children.Add(version); hero.Children.Add(titles);
            var powerGlyph = new FluentGlyph("power") { Width = 21, Height = 21 };
            powerGlyph.SetResourceReference(FluentGlyph.InkProperty, "OnAccent");
            var power = MakeButton("power", powerGlyph, "Power", "启动代理");
            power.Width = 44; power.Height = 44; power.Padding = new Thickness(9); power.Margin = new Thickness(0); Grid.SetColumn(power, 1); hero.Children.Add(power);
            power.Click += delegate { Request(power.Tag as string); };
            var heroBorder = new Border { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1),
                Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 12), Child = hero };
            heroBorder.SetResourceReference(Border.BackgroundProperty, "Inner"); heroBorder.SetResourceReference(Border.BorderBrushProperty, "Line");
            content.Children.Add(heroBorder);
            var modeLabel = Text("代理模式", 11, "Muted"); modeLabel.Margin = new Thickness(0, 0, 0, 6); content.Children.Add(modeLabel);
            var segment = new Grid(); int column = 0;
            foreach (string mode in new[] { "rule", "global", "direct" })
            {
                segment.ColumnDefinitions.Add(new ColumnDefinition());
                string captured = mode;
                var button = MakeButton("mode-" + mode, new TextBlock { Text = TrayContext.ModeName(mode), HorizontalAlignment = HorizontalAlignment.Center },
                    "Quiet", "切换到" + TrayContext.ModeName(mode) + "模式");
                button.Height = 32; button.Padding = new Thickness(6, 5, 6, 5); button.Margin = new Thickness(2, 0, 2, 0);
                button.Click += delegate { if (ModeRequested != null) ModeRequested(captured); };
                Grid.SetColumn(button, column++); segment.Children.Add(button);
            }
            var segments = new Border { CornerRadius = new CornerRadius(9), Padding = new Thickness(2), Child = segment };
            segments.SetResourceReference(Border.BackgroundProperty, "Segment"); content.Children.Add(segments);

            var tun = new Grid { Margin = new Thickness(0, 10, 0, 10) };
            tun.ColumnDefinitions.Add(new ColumnDefinition()); tun.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            tun.Children.Add(Text("TUN 状态", 12, "Muted"));
            var tunValue = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            tunDot = new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            tunText = Text("—", 12, "Text"); tunValue.Children.Add(tunDot); tunValue.Children.Add(tunText);
            AutomationProperties.SetName(tunValue, "TUN 状态，仅显示；开关可在 Zashboard 面板中操作");
            Grid.SetColumn(tunValue, 1); tun.Children.Add(tunValue); content.Children.Add(tun);
            detail = Text("", 12, "Warning"); detail.TextWrapping = TextWrapping.Wrap; detail.Visibility = Visibility.Collapsed;
            detail.Margin = new Thickness(0, 0, 0, 8); AutomationProperties.SetLiveSetting(detail, AutomationLiveSetting.Polite);
            content.Children.Add(detail);
            var panelContent = new Grid(); panelContent.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            panelContent.ColumnDefinitions.Add(new ColumnDefinition()); panelContent.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            panelContent.Children.Add(new FluentGlyph("panel") { Margin = new Thickness(0, 0, 8, 0) });
            var panelText = new TextBlock { Text = "打开 Zashboard 面板", VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(panelText, 1); panelContent.Children.Add(panelText);
            var arrow = new FluentGlyph("arrow") { Margin = new Thickness(8, 0, 0, 0) }; Grid.SetColumn(arrow, 2); panelContent.Children.Add(arrow);
            var panel = MakeButton("panel", panelContent, "Primary", "打开 Zashboard 面板");
            panel.Click += delegate { Request("panel"); }; panel.MinHeight = 38; content.Children.Add(panel);
            var links = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            string[] ids = { "config", "logs", "restart" }, labels = { "配置", "日志", "重启" };
            for (int i = 0; i < ids.Length; i++)
            {
                links.ColumnDefinitions.Add(new ColumnDefinition()); string captured = ids[i];
                var linkContent = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
                linkContent.Children.Add(new FluentGlyph(captured) { Width = 16, Height = 16, Margin = new Thickness(0, 0, 6, 0) });
                linkContent.Children.Add(new TextBlock { Text = labels[i], VerticalAlignment = VerticalAlignment.Center });
                var link = MakeButton(captured, linkContent, "Quiet", i == 0 ? "编辑配置文件" : i == 1 ? "打开日志目录" : "重启本项目内核");
                link.Height = 32; link.FontSize = 12; link.Click += delegate { Request(captured); };
                Grid.SetColumn(link, i); links.Children.Add(link);
            }
            content.Children.Add(links);
            feedback = Text("运行状态不代表外网连通性", 11, "Muted"); feedback.Margin = new Thickness(0, 6, 0, 0);
            content.Children.Add(feedback);
            var exits = new Grid();
            exits.ColumnDefinitions.Add(new ColumnDefinition()); exits.ColumnDefinitions.Add(new ColumnDefinition());
            var exitTray = MakeButton("exit-tray", "退出托盘", "Quiet", "退出托盘，保持代理运行");
            exitTray.FontSize = 12; exitTray.Click += delegate { Request("exit-tray"); }; exits.Children.Add(exitTray);
            var exitStop = MakeButton("exit-stop", "停止并退出", "Quiet", "停止代理并退出");
            exitStop.FontSize = 12; exitStop.Click += delegate { Request("exit-stop"); }; Grid.SetColumn(exitStop, 1); exits.Children.Add(exitStop);
            exitOptions = new Border { Child = exits, BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(0, 5, 0, 0), Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
            exitOptions.SetResourceReference(Border.BorderBrushProperty, "Line"); content.Children.Add(exitOptions);
            var scroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, CanContentScroll = false, Focusable = false };
            surface = new Border { Margin = new Thickness(12), CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Child = scroll };
            surface.SetResourceReference(Border.BackgroundProperty, "Window"); surface.SetResourceReference(Border.BorderBrushProperty, "Line");
            Content = surface; ApplyTheme(null);
            KeyDown += delegate(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) { Dismiss(); e.Handled = true; } };
            Deactivated += delegate { if (IsVisible) focusLostAt = DateTime.UtcNow; Dismiss(); };
            Closing += delegate(object sender, CancelEventArgs e) { if (!disposing) { e.Cancel = true; Dismiss(); } };
            SourceInitialized += delegate
            {
                source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
                if (source != null) source.AddHook(WindowMessage);
            };
            Forms.Integration.ElementHost.EnableModelessKeyboardInterop(this);
        }

        private TextBlock Text(string value, double size, string color, bool strong = false)
        {
            var text = new TextBlock { Text = value, FontSize = size, FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal,
                VerticalAlignment = VerticalAlignment.Center };
            text.SetResourceReference(TextBlock.ForegroundProperty, color); return text;
        }
        private Button MakeButton(string id, object content, string style, string label)
        {
            var button = new Button { Content = content, Style = (Style)Resources[style], HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(10, 6, 10, 6), MinHeight = 28, ToolTip = label };
            AutomationProperties.SetName(button, label); buttons.Add(id, button); return button;
        }
        private void Request(string action) { if (action != null && ActionRequested != null) ActionRequested(action); }

        internal void Render(TrayState state, bool busy, string operation, string error)
        {
            bool ready = state.Status == CoreStatus.Ready, stopped = !state.ProcessExists;
            string label = busy ? operation ?? "正在处理…" : ready ? "代理已启动" : stopped ? "代理已停止" : "内核需要处理";
            heading.Text = label;
            version.Text = state.Version.Length > 0 ? "Mihomo " + state.Version : stopped ? "点击按钮启动本项目内核" : "请检查配置或查看运行日志";
            badgeText.Text = busy ? "正在处理" : ready ? "内核就绪" : stopped ? "已停止" : state.Status == CoreStatus.Authentication ? "认证失败" : "接口异常";
            string ink = busy ? "Warning" : ready ? "Success" : stopped ? "Muted" : "Warning";
            badgeText.SetResourceReference(TextBlock.ForegroundProperty, ink);
            badge.SetResourceReference(Border.BackgroundProperty, ready && !busy ? "SuccessFill" : "Segment");
            var power = buttons["power"]; power.IsEnabled = !busy; power.Tag = state.ProcessExists ? "stop" : "start";
            AutomationProperties.SetName(power, busy ? label : state.ProcessExists ? "停止代理" : "启动代理");
            power.ToolTip = busy ? label : state.ProcessExists ? "停止代理" : "启动代理";
            power.SetResourceReference(Button.BackgroundProperty, state.ProcessExists ? "Accent" : "MutedPower");
            foreach (string mode in new[] { "rule", "global", "direct" })
            {
                var button = buttons["mode-" + mode]; bool selected = state.Mode == mode;
                button.IsEnabled = ready && !busy;
                button.SetResourceReference(Button.BackgroundProperty, selected ? "Inner" : "Segment");
                button.SetResourceReference(Button.ForegroundProperty, selected ? "AccentText" : "Text");
                button.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
                AutomationProperties.SetHelpText(button, selected ? "当前模式" : "仅修改当前运行模式，不改写配置文件");
            }
            buttons["panel"].IsEnabled = ready && !busy; buttons["restart"].IsEnabled = state.ProcessExists && !busy;
            buttons["exit-tray"].IsEnabled = buttons["exit-stop"].IsEnabled = !busy;
            tunText.Text = ready ? state.Tun ? "已开启" : "已关闭" : "—";
            tunDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, ready && state.Tun ? "Success" : "Muted");
            string message = error ?? (!ready && state.ProcessExists ? state.Detail : "");
            detail.Text = message; detail.Visibility = String.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
            feedback.Text = busy ? "正在执行，请稍候…" : "运行状态不代表外网连通性";
            if (IsVisible) Dispatcher.BeginInvoke(new Action(PositionNearAnchor), DispatcherPriority.Loaded);
        }

        internal void ShowAt(Drawing.Point point, bool animate)
        {
            if (disposing) return;
            anchor = point; ApplyTheme(appearanceOverride);
            Opacity = animate && AnimationEnabled ? 0 : 1;
            if (!IsVisible) Show();
            PositionNearAnchor(); Activate();
            if (animate && AnimationEnabled)
                BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)) { FillBehavior = FillBehavior.Stop });
            Opacity = 1;
            if (Keyboard.FocusedElement == null || !IsKeyboardFocusWithin) buttons["power"].Focus();
        }
        internal void Dismiss()
        {
            if (disposing || !IsVisible) return;
            BeginAnimation(OpacityProperty, null); exitOptions.Visibility = Visibility.Collapsed; Hide();
        }
        internal void SetAppearance(bool? value) { appearanceOverride = value; ApplyTheme(value); }
        private void ApplyTheme(bool? value)
        {
            highContrast = SystemParameters.HighContrast; dark = value ?? SystemUsesDarkTheme();
            var colors = dark ? new[] { "#1E222A", "#282E38", "#EDF2F8", "#A9B4C3", "#414A58", "#343D4B", "#2C3441", "#3970DE", "#4B80ED", "#8DB7FF", "#7EDBB1", "#243E35", "#F2C777", "#65738A" }
                : new[] { "#F7F9FC", "#FFFFFF", "#172331", "#626E7D", "#DEE5EF", "#E7EDF6", "#E9EEF5", "#3268D3", "#245BC6", "#265EC4", "#177D57", "#E7F5EE", "#94600B", "#697C99" };
            string[] names = { "Window", "Inner", "Text", "Muted", "Line", "Hover", "Segment", "Accent", "AccentHover", "AccentText", "Success", "SuccessFill", "Warning", "MutedPower" };
            for (int i = 0; i < names.Length; i++)
            {
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i])); brush.Freeze(); Resources[names[i]] = brush;
            }
            Resources["OnAccent"] = Brushes.White;
            if (highContrast)
            {
                foreach (string key in new[] { "Window", "Inner", "Segment", "SuccessFill" }) Resources[key] = SystemColors.WindowBrush;
                foreach (string key in new[] { "Text", "Muted", "Line", "Success", "Warning" }) Resources[key] = SystemColors.WindowTextBrush;
                foreach (string key in new[] { "Accent", "AccentHover", "MutedPower", "Hover" }) Resources[key] = SystemColors.HighlightBrush;
                Resources["AccentText"] = SystemColors.WindowTextBrush; Resources["OnAccent"] = SystemColors.HighlightTextBrush;
            }
            if (surface != null) surface.Effect = highContrast ? null : new DropShadowEffect { BlurRadius = 20, ShadowDepth = 3, Opacity = dark ? .34 : .15, Color = Colors.Black };
        }
        internal static bool SystemUsesDarkTheme()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object setting = key == null ? null : key.GetValue("AppsUseLightTheme", 1);
                    return setting is int && (int)setting == 0;
                }
            }
            catch (System.Security.SecurityException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (IOException) { return false; }
        }
        private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wparam, IntPtr lparam, ref bool handled)
        {
            if ((message == 0x1A || message == 0x31A || message == 0x7E || message == 0x2E0) && !disposing)
                Dispatcher.BeginInvoke(new Action(delegate { if (!disposing) { ApplyTheme(appearanceOverride); if (IsVisible) PositionNearAnchor(); } }), DispatcherPriority.ContextIdle);
            return IntPtr.Zero;
        }
        private void PositionNearAnchor()
        {
            if (disposing || !IsVisible || positioning) return;
            positioning = true;
            try
            {
                var area = Forms.Screen.FromPoint(anchor).WorkingArea;
                double scale = MonitorScale(anchor);
                MaxHeight = Math.Max(120, (area.Height - 16) / scale);
                Width = Math.Min(336, (area.Width - 16) / scale);
                UpdateLayout();
                double height = ActualHeight > 0 ? ActualHeight : DesiredSize.Height;
                var bounds = PlacePopup(area, anchor, (int)Math.Ceiling(Width * scale), (int)Math.Ceiling(height * scale));
                SetWindowPos(new WindowInteropHelper(this).Handle, IntPtr.Zero, bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x14);
            }
            finally { positioning = false; }
        }
        internal static Drawing.Rectangle PlacePopup(Drawing.Rectangle workArea, Drawing.Point point, int width, int height)
        {
            width = Math.Max(1, Math.Min(width, workArea.Width)); height = Math.Max(1, Math.Min(height, workArea.Height));
            int x = point.X - width + 24, y = point.Y - height - 8;
            if (y < workArea.Top && point.Y + height + 8 <= workArea.Bottom) y = point.Y + 8;
            x = Math.Max(workArea.Left, Math.Min(x, workArea.Right - width));
            y = Math.Max(workArea.Top, Math.Min(y, workArea.Bottom - height));
            return new Drawing.Rectangle(x, y, width, height);
        }
        private static double MonitorScale(Drawing.Point point)
        {
            try
            {
                uint x, y; if (GetDpiForMonitor(MonitorFromPoint(point, 2), 0, out x, out y) == 0 && x > 0) return x / 96.0;
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            return 1;
        }
        internal void SavePreview(string path, double scale)
        {
            UpdateLayout();
            var image = new RenderTargetBitmap((int)Math.Ceiling(ActualWidth * scale), (int)Math.Ceiling(ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            image.Render(this); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using (var output = File.Create(path)) encoder.Save(output);
        }
        public void Dispose()
        {
            if (disposing) return; disposing = true;
            if (source != null && !source.IsDisposed) source.RemoveHook(WindowMessage);
            Close();
        }
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(Drawing.Point point, uint flags);
        [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);

        private const string Styles = @"<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
<Style x:Key='Quiet' TargetType='{x:Type Button}'>
 <Setter Property='Background' Value='Transparent'/><Setter Property='BorderBrush' Value='Transparent'/><Setter Property='BorderThickness' Value='1'/>
 <Setter Property='Foreground' Value='{DynamicResource Text}'/><Setter Property='Cursor' Value='Hand'/>
 <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='{x:Type Button}'>
  <Border x:Name='Chrome' CornerRadius='8' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}' Padding='{TemplateBinding Padding}'>
   <ContentPresenter HorizontalAlignment='{TemplateBinding HorizontalContentAlignment}' VerticalAlignment='{TemplateBinding VerticalContentAlignment}' RecognizesAccessKey='True'/>
  </Border>
  <ControlTemplate.Triggers>
   <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Chrome' Property='Background' Value='{DynamicResource Hover}'/></Trigger>
   <Trigger Property='IsPressed' Value='True'><Setter TargetName='Chrome' Property='Opacity' Value='0.75'/></Trigger>
   <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='Chrome' Property='BorderBrush' Value='{DynamicResource AccentText}'/></Trigger>
   <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.42'/></Trigger>
  </ControlTemplate.Triggers>
 </ControlTemplate></Setter.Value></Setter>
</Style>
<Style x:Key='Primary' TargetType='{x:Type Button}' BasedOn='{StaticResource Quiet}'>
 <Setter Property='Background' Value='{DynamicResource Accent}'/><Setter Property='Foreground' Value='{DynamicResource OnAccent}'/>
 <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='{x:Type Button}'>
  <Border x:Name='Chrome' CornerRadius='9' Background='{TemplateBinding Background}' Padding='{TemplateBinding Padding}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='1'>
   <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
  </Border><ControlTemplate.Triggers>
   <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Chrome' Property='Background' Value='{DynamicResource AccentHover}'/></Trigger>
   <Trigger Property='IsPressed' Value='True'><Setter TargetName='Chrome' Property='Opacity' Value='0.8'/></Trigger>
   <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='Chrome' Property='BorderBrush' Value='{DynamicResource OnAccent}'/></Trigger>
   <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.42'/></Trigger>
  </ControlTemplate.Triggers>
 </ControlTemplate></Setter.Value></Setter>
</Style>
<Style x:Key='Power' TargetType='{x:Type Button}' BasedOn='{StaticResource Primary}'>
 <Setter Property='Padding' Value='12'/><Setter Property='Template'><Setter.Value><ControlTemplate TargetType='{x:Type Button}'>
  <Border x:Name='Chrome' CornerRadius='26' Background='{TemplateBinding Background}' Padding='{TemplateBinding Padding}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='2'>
   <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
  </Border><ControlTemplate.Triggers>
   <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Chrome' Property='Background' Value='{DynamicResource AccentHover}'/></Trigger>
   <Trigger Property='IsPressed' Value='True'><Setter TargetName='Chrome' Property='Opacity' Value='0.8'/></Trigger>
   <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='Chrome' Property='BorderBrush' Value='{DynamicResource OnAccent}'/></Trigger>
   <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.42'/></Trigger>
  </ControlTemplate.Triggers>
 </ControlTemplate></Setter.Value></Setter>
</Style></ResourceDictionary>";
    }

    // Small original vector glyphs, drawn at the monitor's DPI rather than bitmap scaling.
    internal sealed class FluentGlyph : FrameworkElement
    {
        internal static readonly DependencyProperty InkProperty = DependencyProperty.Register("Ink", typeof(Brush), typeof(FluentGlyph),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
        internal Brush Ink { get { return (Brush)GetValue(InkProperty); } set { SetValue(InkProperty, value); } }
        private readonly string kind;
        internal FluentGlyph(string kind)
        {
            this.kind = kind; Width = Height = 20; IsHitTestVisible = false;
            VerticalAlignment = VerticalAlignment.Center; HorizontalAlignment = HorizontalAlignment.Center;
            SetBinding(InkProperty, new System.Windows.Data.Binding("Foreground")
                { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Control), 1) });
        }
        protected override void OnRender(DrawingContext drawing)
        {
            base.OnRender(drawing);
            var brush = Ink ?? TextElementForeground();
            var pen = new Pen(brush, 1.65) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            drawing.PushTransform(new ScaleTransform(ActualWidth / 24, ActualHeight / 24));
            Action<double, double, double, double> line = (x, y, a, b) => drawing.DrawLine(pen, new Point(x, y), new Point(a, b));
            if (kind == "power")
            {
                var geometry = new StreamGeometry(); using (var g = geometry.Open())
                { g.BeginFigure(new Point(7, 5), false, false); g.ArcTo(new Point(17, 5), new Size(8.5, 8.5), 0, true, SweepDirection.Counterclockwise, true, false); }
                drawing.DrawGeometry(null, pen, geometry); line(12, 2, 12, 11);
            }
            else if (kind == "wave")
            {
                for (int i = 0; i < 3; i++)
                {
                    var geometry = new StreamGeometry(); using (var g = geometry.Open())
                    { g.BeginFigure(new Point(3, 7 + i * 5), false, false); g.BezierTo(new Point(8, 1 + i * 5), new Point(15, 13 + i * 5), new Point(21, 6 + i * 5), true, false); }
                    drawing.DrawGeometry(null, pen, geometry);
                }
            }
            else if (kind == "panel") { drawing.DrawRoundedRectangle(null, pen, new Rect(3, 4, 18, 16), 2, 2); line(3, 9, 21, 9); }
            else if (kind == "arrow") { line(6, 18, 18, 6); line(9, 6, 18, 6); line(18, 6, 18, 15); }
            else if (kind == "more") { for (int i = 0; i < 3; i++) drawing.DrawEllipse(brush, null, new Point(5 + i * 7, 12), 1.3, 1.3); }
            else if (kind == "restart")
            {
                var geometry = new StreamGeometry(); using (var g = geometry.Open())
                { g.BeginFigure(new Point(19, 8), false, false); g.ArcTo(new Point(20, 16), new Size(8.5, 8.5), 0, true, SweepDirection.Counterclockwise, true, false); }
                drawing.DrawGeometry(null, pen, geometry); line(19, 3, 19, 8); line(14, 8, 19, 8);
            }
            else if (kind == "config")
            {
                var geometry = new StreamGeometry(); using (var g = geometry.Open())
                { g.BeginFigure(new Point(6, 3), false, true); g.LineTo(new Point(14, 3), true, false); g.LineTo(new Point(19, 8), true, false); g.LineTo(new Point(19, 21), true, false); g.LineTo(new Point(6, 21), true, false); }
                drawing.DrawGeometry(null, pen, geometry); line(14, 3, 14, 9); line(14, 9, 19, 9); line(9, 13, 16, 13); line(9, 17, 14, 17);
            }
            else { for (int i = 0; i < 3; i++) { drawing.DrawEllipse(brush, null, new Point(4, 6 + i * 6), 1, 1); line(9, 6 + i * 6, 20, 6 + i * 6); } }
            drawing.Pop();
        }
        private Brush TextElementForeground()
        {
            var parent = VisualTreeHelper.GetParent(this);
            while (parent != null)
            {
                var control = parent as Control; if (control != null) return control.Foreground;
                parent = VisualTreeHelper.GetParent(parent);
            }
            return Brushes.Gray;
        }
    }
}
