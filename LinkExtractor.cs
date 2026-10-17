using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using System.Windows.Interop;
using System.ComponentModel;

namespace PickLight
{
    // ComboBoxItem 附加属性：存下拉项的日期副标题，供列表项模板绑定（选中框只显示名称，日期只在展开列表里显示）
    public static class ComboItemExt
    {
        public static readonly DependencyProperty SubTextProperty =
            DependencyProperty.RegisterAttached("SubText", typeof(string), typeof(ComboItemExt), new PropertyMetadata(null));
        public static string GetSubText(DependencyObject o) { return (string)o.GetValue(SubTextProperty); }
        public static void SetSubText(DependencyObject o, string v) { o.SetValue(SubTextProperty, v); }
    }

    public class LinkItem : INotifyPropertyChanged
    {
        public string Time { get; set; }
        public string Type { get; set; }
        public string Source { get; set; }
        public string Url { get; set; }
        public string Note { get; set; }
        public string Batch { get; set; } // 导入批次名：每次导入归入一批，可按批次筛选查看新旧
        public string ImportTime { get; set; } // 该记录导入到工具的时间（yyyy-MM-dd HH:mm:ss），批次导入日期取其最大值（覆盖导入自动更新）
        string _status;
        public string Status
        {
            get { return _status; }
            set { _status = value; OnPropertyChanged("Status"); }
        }
        public event PropertyChangedEventHandler PropertyChanged;
        void OnPropertyChanged(string p) { if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs(p)); }
    }

    // 媒体类型 → 徽章颜色：视频蓝 / 图片绿 / 音频橙 / 其他灰
    public class TypeBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            string t = value as string ?? "";
            if (t == "视频") return new SolidColorBrush(Color.FromRgb(59, 130, 246));
            if (t == "图片") return new SolidColorBrush(Color.FromRgb(16, 185, 129));
            if (t == "音频") return new SolidColorBrush(Color.FromRgb(245, 158, 11));
            if (t == "其他") return new SolidColorBrush(Color.FromRgb(100, 116, 139));
            return new SolidColorBrush(Color.FromRgb(71, 85, 105));
        }
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public partial class MainWindow : Window
    {
        ObservableCollection<LinkItem> _items = new ObservableCollection<LinkItem>();
        bool _timeSortAsc = false; // 时间排序方向：默认降序（最新在前）
        HashSet<string> _seen = new HashSet<string>();
        string _historyPath;
        string _settingsPath;
        string _downloadDir;
        // 批量下载受限并发：同时最多 4 个连接，完成一个补一个，避免几百条一次性并发打满带宽/连接
        readonly SemaphoreSlim _dlSem = new SemaphoreSlim(4, 4);

        TextBox _search;
        ComboBox _typeFilter;
        ComboBox _srcFilter;
        ComboBox _batchFilter;
        DataGrid _grid;
        TextBlock _statusText;
        TextBlock _pvTypeDot, _pvTypeTxt, _pvTimeTxt, _pvSrcTxt, _pvUrlTxt;
        Border _pvDetailHost; // 预览信息区"链接下面"的图片详情容器（浅灰底独立卡片）：每项一行，与时间/来源/地址区分
        Grid _pvDetailGrid;
        TextBlock _countText;
        CheckBox _filterCache;
        // ---- 在线更新（GitHub Releases）----
        const string AppVersion = "1.0.0"; // 当前版本号（更新检查基准）
        const string UpdateRepoDefault = "t828/PickLight"; // GitHub 仓库(owner/repo)：更新源，发布时在此配置
        string _updateRepo = UpdateRepoDefault;
        bool _autoUpdate = true; // 启动时自动检查更新（settings.ini 的 AutoUpdate=0 可关闭，手动「检查更新」按钮始终可用）
        bool _updatingGrid = false;
        int _newCount = 0;
        int _dupCount = 0;
        MediaElement _currentMedia; // 预览窗口当前内嵌播放的媒体
        System.Diagnostics.Process _currentFfplay; // 预览窗口当前启动的 ffplay 播放进程
        IntPtr _embeddedHwnd; // 伪嵌入：当前叠放在预览弹窗内容区上的 ffplay 窗口句柄（随弹窗移动/缩放同步）
        Window _previewWindow; // 预览弹窗（单例，避免多弹窗互相抢占播放）

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);

        // 预览图片区交互（拖动/双击）：WndProc 钩子用——客户区坐标转屏幕坐标
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool ClientToScreen(IntPtr hwnd, ref POINT pt);
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public struct POINT { public int X; public int Y; }

        static readonly Regex UrlRegex =
            new Regex(@"https?://[^\x00-\x20\x7f-\xff]+?\.(webm|mp4|png|jpg|jpeg|gif|webp|mp3|ogg|wav|m4a|mov)",
                RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly Regex PathDateRegex = new Regex(@"/(\d{4})/(\d{2})/(\d{2})/", RegexOptions.Compiled);
        static readonly Regex ContentTypeRegex = new Regex(@"content-type:([a-zA-Z0-9/.\-+]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly Regex LastModRegex = new Regex(@"last-modified:([A-Za-z]{3},\s*\d{2}\s+[A-Za-z]{3}\s+\d{4}\s+\d{2}:\d{2}:\d{2})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly Regex EtagRegex = new Regex(@"etag:""([^""]+)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public MainWindow()
        {
            _historyPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "links_history.txt");
            _settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.ini");
            Title = "拾光 v" + AppVersion;
            // 启动时激活窗口，避免被其他软件（如豆包）遮挡导致点击落到别的窗口；
            // 启动 1.5 秒后后台静默检查更新（仅当设置允许自动检查；发现新版本才弹窗）
            Loaded += (s, e) =>
            {
                Activate();
                if (_autoUpdate)
                    System.Threading.Tasks.Task.Run(() => { System.Threading.Thread.Sleep(1500); CheckUpdate(false); });
            };
            FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI");
            FontSize = 13.5;
            Width = 1120; Height = 740;
            MinWidth = 760; MinHeight = 520;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            AllowDrop = true;
            Background = (Brush)FindResource("AppBackground");
            // 窗口图标：优先从内置 base64 解码（编译无需外部 app.ico 文件，单文件自足）；失败则回退 exe 内嵌提取
            try
            {
                var ico = IconFromBase64();
                if (ico != null)
                {
                    Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(ico.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    ico.Dispose();
                }
            }
            catch { }

            RestoreWindowState();
            ReadUpdateConfig(); // 从 settings.ini 读取更新配置（AutoUpdate 开关 / UpdateRepo 仓库地址）

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // header
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // toolbar
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // grid+preview
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // status

            root.Children.Add(BuildHeader());
            root.Children.Add(BuildToolbar());
            root.Children.Add(BuildBody());
            root.Children.Add(BuildStatusBar());
            Grid.SetRow(root.Children[0], 0);
            Grid.SetRow(root.Children[1], 1);
            Grid.SetRow(root.Children[2], 2);
            Grid.SetRow(root.Children[3], 3);

            Content = root;

            // DPI 感知
            try { System.Windows.Media.VisualTreeHelper.GetDpi(this); } catch { }
            var mi = new System.Windows.Media.ScaleTransform();

            // 拖放：使用 AddHandler + handledEventsToo=true，确保 DataGrid 等子控件不吞掉事件
            DragEnter += (s, e) => DragOver_Inner(e);
            DragOver += (s, e) => DragOver_Inner(e);
            Drop += (s, e) => Drop_Inner(e);
            Closing += (s, e) => { SaveHistory(); SaveWindowState(); };

            LoadHistory();
            UpdateCount();
        }

        void DragOver_Inner(DragEventArgs e)
        {
            e.Handled = true;
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        }

        // 窗口位置/大小记忆：启动时从同目录 settings.ini 恢复。
        // 跨分辨率适配：保存时记录屏幕尺寸，若当前屏幕分辨率不同则按比例映射窗口位置/大小，再走工作区防护（显示器变化不至于跑丢/截断）
        void RestoreWindowState()
        {
            try
            {
                if (!File.Exists(_settingsPath)) return;
                int left = 0, top = 0, width = 0, height = 0; bool maximized = false; bool any = false;
                int sw = 0, sh = 0;
                foreach (var ln in File.ReadAllLines(_settingsPath))
                {
                    var kv = ln.Split('=');
                    if (kv.Length != 2) continue;
                    int v; bool b;
                    if (kv[0] == "Left" && int.TryParse(kv[1], out v)) { left = v; any = true; }
                    else if (kv[0] == "Top" && int.TryParse(kv[1], out v)) { top = v; any = true; }
                    else if (kv[0] == "Width" && int.TryParse(kv[1], out v)) { width = v; any = true; }
                    else if (kv[0] == "Height" && int.TryParse(kv[1], out v)) { height = v; any = true; }
                    else if (kv[0] == "Maximized" && bool.TryParse(kv[1], out b)) { maximized = b; any = true; }
                    else if (kv[0] == "ScreenW" && int.TryParse(kv[1], out v)) { sw = v; }
                    else if (kv[0] == "ScreenH" && int.TryParse(kv[1], out v)) { sh = v; }
                }
                if (!any) return;
                if (width < MinWidth) width = (int)MinWidth;
                if (height < MinHeight) height = (int)MinHeight;
                // 分辨率变化 → 按比例映射（不同设备/显示器缩放比例不同导致坐标误差，这里收敛回相对位置）
                var curScr = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
                if (sw > 0 && sh > 0 && (sw != curScr.Width || sh != curScr.Height))
                {
                    // 打开设备的屏幕分辨率与配置文件记录不一致：不套用记忆的位置/大小（避免跨设备坐标与尺寸误差），
                    // 直接使用默认设置（居中 + 默认 1120×740），下次正常关闭时会把当前设备的分辨率重新写入。
                    return;
                }
                var wa = System.Windows.Forms.Screen.GetWorkingArea(new System.Drawing.Rectangle(left, top, width, height));
                if (width > wa.Width) width = wa.Width;
                if (height > wa.Height) height = wa.Height;
                // 完整屏幕适配：窗口整体必须落在工作区内（防止记忆位置把窗口推到屏幕外导致内容被截）
                if (left < wa.Left) left = wa.Left;
                if (top < wa.Top) top = wa.Top;
                if (left + width > wa.Right) left = Math.Max(wa.Left, wa.Right - width);
                if (top + height > wa.Bottom) top = Math.Max(wa.Top, wa.Bottom - height);
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = left; Top = top; Width = width; Height = height;
                if (maximized) WindowState = WindowState.Maximized;
            }
            catch { }
        }

        // 读取更新相关配置：AutoUpdate 自动检查开关（更新源 UpdateRepo 由程序内置，发布时在代码常量中配置，不可修改）
        void ReadUpdateConfig()
        {
            try
            {
                if (!File.Exists(_settingsPath)) return;
                foreach (var ln in File.ReadAllLines(_settingsPath))
                {
                    var kv = ln.Split('=');
                    if (kv.Length != 2) continue;
                    if (kv[0] == "AutoUpdate") { bool b; if (bool.TryParse(kv[1], out b)) _autoUpdate = b; }
                }
            }
            catch { }
        }

        void SaveWindowState()
        {
            try
            {
                var scr = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
                File.WriteAllText(_settingsPath,
                    "Left=" + (int)Left + "\r\nTop=" + (int)Top + "\r\nWidth=" + (int)Width + "\r\nHeight=" + (int)Height + "\r\nMaximized=" + (WindowState == WindowState.Maximized) + "\r\nScreenW=" + scr.Width + "\r\nScreenH=" + scr.Height + "\r\n" +
                    "AutoUpdate=" + _autoUpdate + "\r\n");
            }
            catch { }
        }

        void Drop_Inner(DragEventArgs e)
        {
            e.Handled = true;
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var paths = (string[])e.Data.GetData(DataFormats.FileDrop);
                foreach (var p in paths) ProcessPath(p);
            }
        }

        #region UI building

        FrameworkElement BuildHeader()
        {
            // 头部：左侧 标识 + 操作提示；右侧 设置按钮悬浮右上角（主题色填充，与工具栏操作按钮区分）。
            // 窗口标题已含"拾光 v1.0.0"，此处不再重复品牌文字，避免与标题重复
            var dock = new DockPanel { Margin = new Thickness(20, 12, 20, 6), LastChildFill = false };
            var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var logo = new Border
            {
                Width = 34, Height = 34, CornerRadius = new CornerRadius(9),
                Background = new SolidColorBrush(Color.FromRgb(15, 118, 110))
            };
            var logoText = new TextBlock { Text = "拾", FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            logo.Child = logoText;
            var sub = new TextBlock
            {
                Text = "· 拖入缓存文件即可提取 · 双击复制地址",
                FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0),
                Foreground = (Brush)FindResource("TextSecondary")
            };
            left.Children.Add(logo); left.Children.Add(sub);

            // 设置：悬浮右上角，与头部同高度垂直居中；使用专属深墨绿样式（SettingsButtonStyle），
            // 与主操作(主题色)/中性(白底)/危险(红)全部区分
            var btnSettings = new Button { Style = SettingsButtonStyle(13.0), Height = 34, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center };
            var settingsFg = new SolidColorBrush(Color.FromRgb(15, 118, 110)); // 主题色文字（浅薄荷底）
            var gear = new TextBlock { Text = "\uE713", FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"), FontSize = 14, Foreground = settingsFg, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            var settingsTxt = new TextBlock { Text = "设置", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = settingsFg, VerticalAlignment = VerticalAlignment.Center };
            var settingsContent = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            settingsContent.Children.Add(gear); settingsContent.Children.Add(settingsTxt);
            btnSettings.Content = settingsContent;
            btnSettings.Click += (s, e) => ShowSettingsDialog();
            DockPanel.SetDock(btnSettings, Dock.Right);

            dock.Children.Add(left); dock.Children.Add(btnSettings);
            return dock;
        }

        StackPanel BuildToolbar()
        {
            var bar = new StackPanel { Margin = new Thickness(20, 2, 20, 2) };

            // 第一行：操作按钮（与行内/弹窗按钮同一套现代样式，去 emoji 纯文字）。
            // 设置按钮已移至头部右上角，此行只保留数据操作按钮
            var row1 = new WrapPanel { VerticalAlignment = VerticalAlignment.Top };
            // 中性白渐变按钮（工具栏标准字号 13.5），hover 加深 + 按压反馈
            var style = BuildActionButtonStyle(Color.FromRgb(255, 255, 255), Color.FromRgb(248, 250, 252), Color.FromRgb(241, 245, 249), Color.FromRgb(226, 232, 240), Color.FromRgb(203, 213, 225), new SolidColorBrush(Color.FromRgb(30, 41, 59)), true, 13.0);

            var btnFolder = MakeBtn("选择文件夹", style);
            btnFolder.Click += (s, e) => ChooseFolder();
            var btnFile = MakeBtn("选择文件", style);
            btnFile.Click += (s, e) => ChooseFiles();
            var btnDownloadAll = MakeBtn("下载全部", style);
            btnDownloadAll.Click += (s, e) => Download(true);
            // 下载全部（批量受限并发）功能保留，按钮暂不展示，后续需要可启用
            btnDownloadAll.Visibility = Visibility.Collapsed;
            var btnCopy = MakeBtn("复制全部", style);
            btnCopy.Click += (s, e) => CopyAll();
            var btnClear = MakeBtn("清空", style);
            btnClear.Click += (s, e) =>
            {
                if (_items.Count == 0) { SetStatus("当前没有可清空的记录。"); return; }
                var r = System.Windows.MessageBox.Show(this, "确定清空全部 " + _items.Count + " 条记录？此操作不可恢复。", "拾光", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (r != MessageBoxResult.Yes) return;
                _items.Clear(); _seen.Clear(); SaveHistory(); UpdateCount(); SetStatus("已清空，可拖入新文件。");
            };

            row1.Children.Add(btnFolder); row1.Children.Add(btnFile);
            row1.Children.Add(btnDownloadAll);
            row1.Children.Add(btnCopy); row1.Children.Add(btnClear);

            // 工具栏按钮统一等宽等高（与"记录管理"一致的外观）：一处控制，避免逐个修改
            foreach (var b in new[] { btnFolder, btnFile, btnDownloadAll, btnCopy, btnClear }) { b.Width = 90; b.Height = 38; }

            // 工具栏按钮统一间距：左右 gap（首项 0）、上下 4 —— 窗口窄换行时行距恒为 8，与筛选行同一套规则
            double bgap = 10;
            btnFolder.Margin = new Thickness(0, 4, 0, 4);
            foreach (var b in new FrameworkElement[] { btnFile, btnDownloadAll, btnCopy, btnClear })
                b.Margin = new Thickness(bgap, 4, 0, 4);

            // 第二行：搜索 + 筛选 + 勾选框。全部放同一个 WrapPanel，
            // 所有子项统一间距（左右 gap、上下 padding 一致）——窗口窄时换行行距也统一，不会出现多行混杂
            var row2 = new WrapPanel { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 8, 0, 0) };

            // 搜索框：行高自适应（MinHeight 保底，随字号/内容自动），不固定高度
            _search = new TextBox { Width = 300, MinHeight = 38, Style = InputBoxStyle() };
            _search.TextChanged += (s, e) => ApplyFilter();
            var searchGrid = MakeHintedInput(_search, "搜索 时间 / 类型 / 来源 / 地址 / 备注");
            searchGrid.Width = 300;
            row2.Children.Add(searchGrid);

            // 筛选下拉框统一走公共工厂（样式/尺寸/对齐一处定义）
            _typeFilter = MakeFilterCombo(112);
            _typeFilter.Items.Add(MakeBatchItem("全部类型"));
            _typeFilter.Items.Add(MakeBatchItem("视频"));
            _typeFilter.Items.Add(MakeBatchItem("图片"));
            _typeFilter.Items.Add(MakeBatchItem("音频"));
            _typeFilter.SelectedIndex = 0;
            _typeFilter.SelectionChanged += TypeFilterChanged;
            row2.Children.Add(_typeFilter);

            _srcFilter = MakeFilterCombo(152);
            _srcFilter.Items.Add(MakeBatchItem("全部来源"));
            _srcFilter.SelectedIndex = 0;
            _srcFilter.SelectionChanged += SrcFilterChanged;
            row2.Children.Add(_srcFilter);

            _batchFilter = MakeFilterCombo(210);
            _batchFilter.Items.Add(MakeBatchItem("全部记录"));
            _batchFilter.SelectedIndex = 0;
            _batchFilter.SelectionChanged += BatchFilterChanged;
            row2.Children.Add(_batchFilter);
            var btnBatchMgr = MakeBtn("记录管理", PrimaryButtonStyle(13.0), 90, 38); // 主操作类：主题色填充，与普通工具按钮区分
            btnBatchMgr.Click += (s, e) => ManageBatches();
            row2.Children.Add(btnBatchMgr);

            _filterCache = new CheckBox
            {
                Content = "文件夹扫描仅处理缓存文件(data_*/f_*)",
                IsChecked = true,
                VerticalAlignment = VerticalAlignment.Center, FontSize = 13,
                Foreground = (Brush)FindResource("TextSecondary"),
                ToolTip = "勾选后：扫描文件夹时仅处理文件名以 data_ 或 f_ 开头的缓存文件（游戏启动器缓存文件常见命名），其余文件自动跳过。\n取消勾选：扫描文件夹下全部文件。\n选择单个文件导入或拖入文件不受此选项影响。"
            };
            row2.Children.Add(_filterCache);

            // 统一筛选行控件间距：左右 gap（首项 0）、上下 4 —— WrapPanel 换行后行距恒为 8，窗口任意宽度都整齐
            double gap = 10;
            searchGrid.Margin = new Thickness(0, 4, 0, 4);
            foreach (var c in new FrameworkElement[] { _typeFilter, _srcFilter, _batchFilter, btnBatchMgr, _filterCache })
                c.Margin = new Thickness(gap, 4, 0, 4);

            bar.Children.Add(row1); bar.Children.Add(row2);
            return bar;
        }

        // 公共按钮组件：默认内容自适应撑开（不设固定宽度，字号/内边距由 Style 决定）；
        // 需要固定大小时通过 width/height 参数单独传入（>0 才生效），一处定义全局复用
        Button MakeBtn(string text, Style style, double width = 0, double height = 0)
        {
            var b = new Button { Content = text, Style = style, Margin = new Thickness(0, 0, 12, 0) };
            if (width > 0) b.Width = width;
            if (height > 0) b.Height = height;
            return b;
        }

        Grid BuildBody()
        {
            var g = new Grid { Margin = new Thickness(20, 8, 20, 10) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            _grid = new DataGrid
            {
                ItemsSource = _items,
                AutoGenerateColumns = false,
                IsReadOnly = false,
                CanUserAddRows = false, // 关闭底部空新增行，避免 0 条数据时显示占位行
                HeadersVisibility = DataGridHeadersVisibility.Column, // 仅列头；序号通过首列显示
                GridLinesVisibility = DataGridGridLinesVisibility.All, // 行间水平+列间竖直分隔线，行/列区分清晰
                HorizontalGridLinesBrush = (Brush)FindResource("RowLine"),
                VerticalGridLinesBrush = new SolidColorBrush(Color.FromRgb(203, 213, 225)), // 竖线比横线略深，列界清晰可辨
                RowBackground = Brushes.White,
                AlternatingRowBackground = (Brush)FindResource("RowAlt"),
                Background = Brushes.White,
                BorderThickness = new Thickness(1),
                BorderBrush = (Brush)FindResource("BorderColor"),
                SelectionMode = DataGridSelectionMode.Extended, // 行选中保留多选高亮反馈（单元格浅绿高亮，文字不变白）
                CanUserSortColumns = true,
                SelectionUnit = DataGridSelectionUnit.FullRow,
                EnableRowVirtualization = true,
                // ColumnHeaderHeight 不设固定值：表头行高随内容+内边距自适应，改字号也不会截字
                ColumnHeaderStyle = (Style)FindResource("ColHeaderCenter"),
                CellStyle = BuildCellStyle(),
                RowStyle = BuildRowStyle()
            };
            // 行号：左侧首列显示序号（1,2,3…），跟随筛选/排序动态编号；LoadingRow 用 GetIndex 写入行 Tag，模板列绑定 Tag 显示
            _grid.LoadingRow += (s, e) => { e.Row.Tag = (e.Row.GetIndex() + 1).ToString(); };
            // 序号列：行号（1,2,3…），绑定行 Tag（LoadingRow 写入），排序/筛选后动态重排
            var seqTemplate = new DataTemplate();
            var seqFactory = new FrameworkElementFactory(typeof(TextBlock));
            seqFactory.SetValue(TextBlock.TextProperty, new Binding("Tag") { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(DataGridRow), 1) });
            seqFactory.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            seqFactory.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            seqFactory.SetValue(TextBlock.ForegroundProperty, (Brush)FindResource("TextSecondary"));
            seqFactory.SetValue(TextBlock.FontSizeProperty, 12.5);
            seqFactory.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold); // 序号加粗，行号更醒目
            seqFactory.SetValue(TextBlock.TextWrappingProperty, TextWrapping.NoWrap); // 4 位数不换行
            seqFactory.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.None);
            seqTemplate.VisualTree = seqFactory;
            _grid.Columns.Add(new DataGridTemplateColumn { Header = "#", Width = 52, MinWidth = 48, IsReadOnly = true, CanUserSort = false, CellTemplate = seqTemplate });
            // 时间列：小号灰字，弱化次要信息（垂直居中）；可点击表头切换升/降序
            var timeCol = new DataGridTextColumn { Header = "时间 ↓", Binding = new Binding("Time"), Width = 108, MinWidth = 96, IsReadOnly = true, SortMemberPath = "Time" };
            var timeStyle = new Style(typeof(TextBlock));
            timeStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, (Brush)FindResource("TextSecondary")));
            timeStyle.Setters.Add(new Setter(TextBlock.FontSizeProperty, 12.5));
            timeStyle.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
            timeStyle.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Center));
            timeCol.ElementStyle = timeStyle;
            _grid.Columns.Add(timeCol);
            // 类型列：彩色徽章（视频蓝/图片绿/音频橙），一眼区分媒体类型；模板列不支持排序
            _grid.Columns.Add(new DataGridTemplateColumn { Header = "类型", Width = 78, MinWidth = 62, IsReadOnly = true, CellTemplate = BuildTypeTemplate(), CanUserSort = false });
            // 来源/游戏列：垂直+水平居中；显式指定前景色，锁定文字颜色不被选中态覆盖
            var srcStyle = new Style(typeof(TextBlock));
            srcStyle.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
            srcStyle.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Center));
            srcStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, (Brush)FindResource("TextPrimary")));
            var srcCol = new DataGridTextColumn { Header = "来源", Binding = new Binding("Source"), Width = 140, MinWidth = 110, IsReadOnly = true, ElementStyle = srcStyle, CanUserSort = false };
            _grid.Columns.Add(srcCol);
            // 地址列：完整换行显示 + 下方内嵌「预览/复制/下载」按钮；表头与其他列一致居中；模板列不支持排序
            _grid.Columns.Add(new DataGridTemplateColumn { Header = "地址", Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 300, IsReadOnly = true, CellTemplate = BuildAddressTemplate(), CanUserSort = false });
            // 备注列：可双击编辑（显示态垂直+水平居中）；显式前景色，选中态不变化
            var noteStyle = new Style(typeof(TextBlock));
            noteStyle.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
            noteStyle.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Center));
            noteStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, (Brush)FindResource("TextPrimary")));
            _grid.Columns.Add(new DataGridTextColumn { Header = "备注(双击编辑)", Binding = new Binding("Note") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 150, MinWidth = 120, ElementStyle = noteStyle, CanUserSort = false });
            // 排序：仅时间列可切换（默认降序/最新在前），点击表头切换升/降序并显示箭头
            _grid.Sorting += (s, e) =>
            {
                e.Handled = true;
                if (e.Column == null || e.Column.SortMemberPath != "Time") return;
                _timeSortAsc = !_timeSortAsc;
                var view = CollectionViewSource.GetDefaultView(_items);
                view.SortDescriptions.Clear();
                view.SortDescriptions.Add(new SortDescription("Time", _timeSortAsc ? ListSortDirection.Ascending : ListSortDirection.Descending));
                // 表头文字直接标注排序方向（DataGrid 内置箭头在自定义样式下不渲染）
                e.Column.Header = _timeSortAsc ? "时间 ↑" : "时间 ↓";
                foreach (var c in _grid.Columns) c.SortDirection = null;
                e.Column.SortDirection = _timeSortAsc ? ListSortDirection.Ascending : ListSortDirection.Descending;
            };
            timeCol.SortDirection = ListSortDirection.Descending; // 初始状态与默认排序一致（最新在前）
            _grid.MouseDoubleClick += (s, e) =>
            {
                var row = _grid.SelectedItem as LinkItem;
                if (row != null && !string.IsNullOrEmpty(row.Url)) { Clipboard.SetText(row.Url); SetStatus("已复制地址：" + row.Url); }
            };
            _grid.CellEditEnding += (s, e) => { if (e.Row.Item is LinkItem) SaveHistory(); };
            g.Children.Add(_grid);
            Grid.SetColumn(_grid, 0); Grid.SetRow(_grid, 0);

            return g;
        }

        Style BuildCellStyle()
        {
            var s = new Style(typeof(DataGridCell));
            s.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            s.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            s.Setters.Add(new Setter(FrameworkElement.FocusVisualStyleProperty, null));
            // 单元格内边距与垂直居中：内容与单元格边缘留足间距，不贴边
            s.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(20, 12, 20, 12)));
            s.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
            // 自定义单元格模板：只负责渲染内容，选中态不干预背景/前景（交给样式层触发器）——
            // 避免系统默认模板选中时把文字变白，同时让样式层的绿色高亮正常生效
            var cellTpl = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
                "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='DataGridCell'>" +
                "<Border x:Name='Bd' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}' SnapsToDevicePixels='True'>" +
                "<ContentPresenter SnapsToDevicePixels='{TemplateBinding SnapsToDevicePixels}' VerticalAlignment='Center'/>" +
                "</Border>" +
                "</ControlTemplate>");
            s.Setters.Add(new Setter(Control.TemplateProperty, cellTpl));
            // 选中高亮：浅绿单元格背景 + 深色文字（样式层触发器，与原始可选中样式一致）
            var sel = new Trigger { Property = DataGridCell.IsSelectedProperty, Value = true };
            sel.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(209, 250, 229))));
            sel.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(30, 41, 59))));
            s.Triggers.Add(sel);
            return s;
        }

        Style BuildRowStyle()
        {
            var s = new Style(typeof(DataGridRow));
            s.Setters.Add(new Setter(FrameworkElement.FocusVisualStyleProperty, null));
            // 行选中无批量操作，不再显示醒目高亮（选中行背景与普通行一致，仅保留悬停提示）：
            // 自定义行模板完全接管 背景交替/悬停/选中 三层视觉，避免系统默认选中蓝底
            var rowTpl = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
                "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='DataGridRow'>" +
                "<Border x:Name='Bd' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}' SnapsToDevicePixels='True'>" +
                "<SelectiveScrollingGrid>" +
                "<SelectiveScrollingGrid.ColumnDefinitions>" +
                "<ColumnDefinition Width='Auto'/>" +
                "<ColumnDefinition Width='*'/>" +
                "</SelectiveScrollingGrid.ColumnDefinitions>" +
                "<DataGridCellsPresenter Grid.Column='1' ItemsPanel='{TemplateBinding ItemsPanel}' SnapsToDevicePixels='{TemplateBinding SnapsToDevicePixels}'/>" +
                "</SelectiveScrollingGrid>" +
                "</Border>" +
                "<ControlTemplate.Triggers>" +
                "<Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Bd' Property='Background' Value='#EBF1FA'/></Trigger>" +
                "</ControlTemplate.Triggers></ControlTemplate>");
            s.Setters.Add(new Setter(Control.TemplateProperty, rowTpl));
            return s;
        }

        // 垂直渐变画刷（现代按钮上浅下深）
        LinearGradientBrush Gradient(Color top, Color bottom)
        {
            var lg = new LinearGradientBrush();
            lg.StartPoint = new Point(0, 0);
            lg.EndPoint = new Point(0, 1);
            lg.GradientStops.Add(new GradientStop(top, 0.0));
            lg.GradientStops.Add(new GradientStop(bottom, 1.0));
            return lg;
        }

        // 棋盘格画刷：透明图片（透明+白色内容）预览的标准垫底，透明区域显示灰白格
        // 下拉框选中框样式：圆角 + 右侧箭头 + hover 边框加深 + 展开时主题色边框，
        // 与按钮现代风格统一，替换系统默认外观
        Style BuildComboBoxStyle()
        {
            var st = new Style(typeof(ComboBox));
            st.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.White)); // 白底 + 深边框，与页面拉开层次
            st.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(203, 213, 225)))); // #CBD5E1 明显边框
            st.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
            st.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 4, 10, 4)));
            st.Setters.Add(new Setter(Control.FontSizeProperty, 13.0));
            st.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(30, 41, 59))));
            st.Setters.Add(new Setter(Control.CursorProperty, Cursors.Hand));

            // 模板用 XamlReader.Parse 字符串构造（FrameworkElementFactory 构造含 Popup 的模板运行时不可靠，已弃用）
            var tpl = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
                "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='ComboBox'>" +
                "<Grid>" +
                "<Border x:Name='Bd' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}' CornerRadius='6' SnapsToDevicePixels='True'>" +
                "<Grid>" +
                "<ToggleButton x:Name='Toggle' Focusable='False' ClickMode='Press' IsChecked='{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}'>" +
                "<ToggleButton.Template><ControlTemplate TargetType='ToggleButton'><Border Background='Transparent'/></ControlTemplate></ToggleButton.Template>" +
                "</ToggleButton>" +
                "<TextBlock Text='{Binding SelectedItem.Tag, RelativeSource={RelativeSource TemplatedParent}, FallbackValue=}' TextTrimming='CharacterEllipsis' VerticalAlignment='Center' HorizontalAlignment='Left' Margin='{TemplateBinding Padding}' IsHitTestVisible='False'/>" +
                "<Path x:Name='Arrow' Data='M 0 0 L 4 4 L 8 0 Z' Fill='#334155' Width='10' Height='5' VerticalAlignment='Center' HorizontalAlignment='Right' Margin='0,0,10,0' IsHitTestVisible='False'/>" +
                "</Grid>" +
                "</Border>" +
                "<Popup x:Name='PART_Popup' Placement='Bottom' IsOpen='{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}' AllowsTransparency='True' PopupAnimation='Fade'>" +
                "<Border Background='White' BorderBrush='#CBD5E1' BorderThickness='1' CornerRadius='6' Margin='0,4,0,0' Padding='4' SnapsToDevicePixels='True'>" +
                "<Border.Effect><DropShadowEffect BlurRadius='16' ShadowDepth='3' Opacity='0.28' Color='Black'/></Border.Effect>" +
                "<ScrollViewer MaxHeight='300' VerticalScrollBarVisibility='Auto' HorizontalScrollBarVisibility='Disabled'><ItemsPresenter/></ScrollViewer>" +
                "</Border>" +
                "</Popup>" +
                "</Grid>" +
                "<ControlTemplate.Triggers>" +
                "<Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Bd' Property='BorderBrush' Value='#94A3B8'/></Trigger>" +
                "<Trigger Property='IsDropDownOpen' Value='True'><Setter TargetName='Bd' Property='BorderBrush' Value='#0F766E'/></Trigger>" +
                "<Trigger Property='IsEnabled' Value='False'><Setter TargetName='Bd' Property='Background' Value='#F1F5F9'/><Setter TargetName='Bd' Property='BorderBrush' Value='#E2E8F0'/><Setter TargetName='Arrow' Property='Fill' Value='#CBD5E1'/><Setter Property='Foreground' Value='#94A3B8'/></Trigger>" +
                "</ControlTemplate.Triggers></ControlTemplate>");
            st.Setters.Add(new Setter(Control.TemplateProperty, tpl));
            return st;
        }

        // 下拉列表项样式：圆角 hover 浅灰高亮、选中浅蓝底 + 深蓝字，与记录管理弹窗列表风格一致
        Style BuildComboBoxItemStyle()
        {
            var st = new Style(typeof(ComboBoxItem));
            st.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 6, 10, 6)));
            st.Setters.Add(new Setter(Control.FontSizeProperty, 13.0));
            st.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(30, 41, 59))));
            st.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            st.Setters.Add(new Setter(Control.CursorProperty, Cursors.Hand));
            var tpl = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
                "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='ComboBoxItem'>" +
                "<Border x:Name='Bd' Background='{TemplateBinding Background}' Padding='{TemplateBinding Padding}' CornerRadius='4' SnapsToDevicePixels='True'>" +
                "<ContentPresenter HorizontalAlignment='Left' VerticalAlignment='Center'/>" +
                "</Border>" +
                "<ControlTemplate.Triggers>" +
                "<Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Bd' Property='Background' Value='#F1F5F9'/></Trigger>" +
                "<Trigger Property='IsSelected' Value='True'><Setter TargetName='Bd' Property='Background' Value='#DBEAFE'/><Setter Property='Foreground' Value='#1D4ED8'/><Setter Property='FontWeight' Value='SemiBold'/></Trigger>" +
                "</ControlTemplate.Triggers></ControlTemplate>");
            st.Setters.Add(new Setter(Control.TemplateProperty, tpl));
            return st;
        }

        DrawingBrush CheckerBrush()
        {
            var g = new DrawingGroup();
            g.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(255, 255, 255)), null, new RectangleGeometry(new Rect(0, 0, 2, 2))));
            g.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(216, 222, 228)), null, new RectangleGeometry(new Rect(0, 0, 1, 1))));
            g.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(216, 222, 228)), null, new RectangleGeometry(new Rect(1, 1, 1, 1))));
            var db = new DrawingBrush { Drawing = g, TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 16, 16), ViewportUnits = BrushMappingMode.Absolute };
            return db;
        }

        // 统一操作按钮样式（借鉴现代流行 UI：圆角6 + 垂直渐变 + 轻阴影 + hover 加深 + 按压反馈）
        // 行内小按钮(fontSize=12.5)与预览弹窗按钮(fontSize=14)共用，保证全局视觉统一
        Style BuildActionButtonStyle(Color top, Color bottom, Color hoverTop, Color hoverBottom, Color press, Brush fg, bool border, double fontSize)
        {
            var st = new Style(typeof(Button));
            st.Setters.Add(new Setter(Button.BackgroundProperty, Gradient(top, bottom)));
            st.Setters.Add(new Setter(Button.ForegroundProperty, fg));
            st.Setters.Add(new Setter(Button.BorderThicknessProperty, new Thickness(border ? 1 : 0)));
            if (border)
                st.Setters.Add(new Setter(Button.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(203, 213, 225))));
            st.Setters.Add(new Setter(Button.PaddingProperty, new Thickness(11, 5, 11, 5)));
            st.Setters.Add(new Setter(Button.CursorProperty, Cursors.Hand));
            st.Setters.Add(new Setter(Button.FontSizeProperty, fontSize));
            st.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
            st.Setters.Add(new Setter(Control.FontFamilyProperty, FontFamily));

            var template = new ControlTemplate(typeof(Button));
            var borderF = new FrameworkElementFactory(typeof(Border));
            borderF.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
            borderF.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Button.BorderBrushProperty));
            borderF.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Button.BorderThicknessProperty));
            borderF.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            borderF.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            borderF.AppendChild(cp);
            template.VisualTree = borderF;
            st.Setters.Add(new Setter(Button.TemplateProperty, template));

            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Button.BackgroundProperty, Gradient(hoverTop, hoverBottom)));
            st.Triggers.Add(hover);
            var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
            pressed.Setters.Add(new Setter(Button.BackgroundProperty, new SolidColorBrush(press)));
            st.Triggers.Add(pressed);
            // 禁用态：灰底灰字，与输入框/下拉框禁用态视觉统一
            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(Button.BackgroundProperty, new SolidColorBrush(Color.FromRgb(226, 232, 240))));
            disabled.Setters.Add(new Setter(Button.ForegroundProperty, new SolidColorBrush(Color.FromRgb(148, 163, 184))));
            st.Triggers.Add(disabled);
            return st;
        }

        // 主操作按钮（主题色填充白字）：确定/重命名/记录管理等强调动作，与普通白底按钮一眼区分
        Style PrimaryButtonStyle(double fontSize)
        {            return BuildActionButtonStyle(
                Color.FromRgb(15, 118, 110), Color.FromRgb(11, 94, 87),   // #0F766E → #0B5E57
                Color.FromRgb(13, 104, 96), Color.FromRgb(10, 84, 78),
                Color.FromRgb(8, 70, 65),
                new SolidColorBrush(Colors.White), false, fontSize);
        }

        // 危险操作按钮（红色填充白字）：删除分组等不可恢复动作，警示强度明显
        Style DangerButtonStyle(double fontSize)
        {
            return BuildActionButtonStyle(
                Color.FromRgb(220, 38, 38), Color.FromRgb(185, 28, 28),   // #DC2626 → #B91C1C
                Color.FromRgb(200, 32, 32), Color.FromRgb(168, 24, 24),
                Color.FromRgb(150, 21, 21),
                new SolidColorBrush(Colors.White), false, fontSize);
        }

        // 设置按钮专属样式：极淡薄荷渐变填充 + 主题色文字（#F0FDFA → #CCFBF1 底 / #0F766E 字），
        // 清新通透、贴近白色背景，最适配浅色界面；区别于中性白底按钮（带淡薄荷调）、
        // 主题绿深填充按钮（深浅相反）、红色危险按钮（色相不同）
        Style SettingsButtonStyle(double fontSize)
        {
            return BuildActionButtonStyle(
                Color.FromRgb(240, 253, 250), Color.FromRgb(204, 251, 241), // #F0FDFA → #CCFBF1 极淡薄荷
                Color.FromRgb(209, 250, 229), Color.FromRgb(187, 247, 208), // hover 加深一档
                Color.FromRgb(167, 243, 208),                               // press
                new SolidColorBrush(Color.FromRgb(15, 118, 110)), false, fontSize); // 主题色文字
        }

        // 公共输入框样式：白底 + 明显边框(#CBD5E1) + 圆角6 + 聚焦主题色边框 + 禁用灰化。
        // 提示词内置于模板（PART_Hint）：与内容同在边框内、共用同一 Padding 绑定 → 边距/字号天然一致，
        // 可见性由代码控制（MakeHintedInput 传入提示词文本）。所有输入框统一使用
        Style InputBoxStyle()
        {
            var st = new Style(typeof(TextBox));
            st.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.White));
            st.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(203, 213, 225))));
            st.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
            st.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 5, 32, 5)));
            st.Setters.Add(new Setter(Control.FontSizeProperty, 13.0));
            st.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
            var tpl = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
                "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='TextBox'>" +
                "<Border x:Name='bd' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}' CornerRadius='6' SnapsToDevicePixels='True'>" +
                "<Grid>" +
                "<ScrollViewer x:Name='PART_ContentHost' VerticalAlignment='Center'/>" +
                "<TextBlock x:Name='PART_Hint' Foreground='#64748B' FontSize='{TemplateBinding FontSize}' Margin='{TemplateBinding Padding}' VerticalAlignment='Center' HorizontalAlignment='Left' IsHitTestVisible='False' Visibility='Collapsed' TextTrimming='CharacterEllipsis'/>" +
                "</Grid>" +
                "</Border>" +
                "<ControlTemplate.Triggers>" +
                "<Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='bd' Property='BorderBrush' Value='#0F766E'/></Trigger>" +
                "<Trigger Property='IsEnabled' Value='False'><Setter TargetName='bd' Property='Background' Value='#F1F5F9'/><Setter Property='Foreground' Value='#94A3B8'/><Setter TargetName='PART_Hint' Property='Foreground' Value='#94A3B8'/></Trigger>" +
                "</ControlTemplate.Triggers></ControlTemplate>");
            st.Setters.Add(new Setter(Control.TemplateProperty, tpl));
            return st;
        }

        // 统一输入框容器：提示词由 InputBoxStyle 模板内置的 PART_Hint 显示（与内容同 Padding、边距完全一致），
        // 此处通过模板 FindName 写入提示词文本并按内容控制显示；外层负责右侧清空按钮。所有输入框共用
        Grid MakeHintedInput(TextBox box, string hintText)
        {
            TextBlock hint = null;
            box.ApplyTemplate(); // 确保模板已应用，可定位 PART_Hint
            var h = box.Template.FindName("PART_Hint", box) as TextBlock;
            if (h != null)
            {
                hint = h;
                hint.Text = hintText;
                // 起点与内容文字精确一致：WPF TextEditor 内容起点 = 边框 + Padding.Left + 3px 内部偏移；
                // PART_Hint 与内容同在边框内（边框两侧抵消），故 Margin 左 = Padding.Left + 3，动态跟随样式
                hint.Margin = new Thickness(box.Padding.Left + 3, 0, box.Padding.Right, 0);
                box.TextChanged += (s, e) => hint.Visibility = string.IsNullOrEmpty(box.Text) ? Visibility.Visible : Visibility.Collapsed;
                hint.Visibility = string.IsNullOrEmpty(box.Text) ? Visibility.Visible : Visibility.Collapsed; // 初始状态同步
            }
            // 清空按钮：圆形浅灰底，hover 加深，现代输入框风格
            var clear = new Button
            {
                Content = "✕",
                Width = 22,
                Height = 22,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 0, 5, 0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139))
            };
            var clearTpl = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
                "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'>" +
                "<Border x:Name='Bd' Background='{TemplateBinding Background}' CornerRadius='11' SnapsToDevicePixels='True'>" +
                "<ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>" +
                "</Border>" +
                "<ControlTemplate.Triggers>" +
                "<Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Bd' Property='Background' Value='#E2E8F0'/></Trigger>" +
                "<Trigger Property='IsPressed' Value='True'><Setter TargetName='Bd' Property='Background' Value='#CBD5E1'/></Trigger>" +
                "</ControlTemplate.Triggers></ControlTemplate>");
            clear.Template = clearTpl;
            clear.Click += (s, e) => { box.Text = ""; box.Focus(); };
            // 初始状态同步一次：空内容 → 隐藏清空按钮
            clear.Visibility = string.IsNullOrEmpty(box.Text) ? Visibility.Collapsed : Visibility.Visible;
            box.TextChanged += (s, e) =>
                clear.Visibility = string.IsNullOrEmpty(box.Text) ? Visibility.Collapsed : Visibility.Visible;
            var g = new Grid();
            g.Children.Add(box);
            g.Children.Add(clear);
            return g;
        }

        // 类型徽章：彩色圆点 + 彩色文字（视频蓝/图片绿/音频橙/其他灰），媒体类型一眼可辨
        DataTemplate BuildTypeTemplate()
        {
            var root = new FrameworkElementFactory(typeof(StackPanel));
            root.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
            root.SetValue(StackPanel.VerticalAlignmentProperty, VerticalAlignment.Center);
            root.SetValue(StackPanel.HorizontalAlignmentProperty, HorizontalAlignment.Center);

            var dot = new FrameworkElementFactory(typeof(TextBlock));
            dot.SetValue(TextBlock.TextProperty, "●");
            dot.SetValue(TextBlock.FontSizeProperty, 9.5);
            dot.SetValue(TextBlock.MarginProperty, new Thickness(0, 0, 5, 0));
            dot.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            dot.SetBinding(TextBlock.ForegroundProperty, new Binding("Type") { Converter = new TypeBrushConverter() });
            root.AppendChild(dot);

            var label = new FrameworkElementFactory(typeof(TextBlock));
            label.SetValue(TextBlock.FontSizeProperty, 13.0);
            label.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
            label.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            label.SetBinding(TextBlock.TextProperty, new Binding("Type"));
            label.SetBinding(TextBlock.ForegroundProperty, new Binding("Type") { Converter = new TypeBrushConverter() });
            root.AppendChild(label);

            var dt = new DataTemplate();
            dt.VisualTree = root;
            return dt;
        }

        DataTemplate BuildAddressTemplate()
        {
            // 布局：左侧链接文本（可换行，占满剩余宽度），右侧竖向排「预览/复制/下载」按钮组，整体垂直居中
            var root = new FrameworkElementFactory(typeof(Grid));
            root.SetValue(Grid.MarginProperty, new Thickness(20, 10, 20, 10));
            root.SetValue(Grid.VerticalAlignmentProperty, VerticalAlignment.Center);

            var colUrl = new FrameworkElementFactory(typeof(ColumnDefinition));
            colUrl.SetValue(ColumnDefinition.WidthProperty, new GridLength(1, GridUnitType.Star));
            var colBtns = new FrameworkElementFactory(typeof(ColumnDefinition));
            colBtns.SetValue(ColumnDefinition.WidthProperty, GridLength.Auto);
            root.AppendChild(colUrl);
            root.AppendChild(colBtns);

            var urlText = new FrameworkElementFactory(typeof(TextBlock));
            urlText.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
            urlText.SetValue(TextBlock.FontSizeProperty, 13.0);
            urlText.SetValue(TextBlock.ForegroundProperty, (Brush)FindResource("TextSecondary"));
            urlText.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            urlText.SetValue(TextBlock.MarginProperty, new Thickness(0, 0, 12, 0));
            urlText.SetBinding(TextBlock.TextProperty, new Binding("Url"));
            root.AppendChild(urlText);
            urlText.SetValue(Grid.ColumnProperty, 0);

            // 右侧按钮组：竖向堆叠，窄高紧凑，鼠标移入移出用统一样式
            var btnCol = new FrameworkElementFactory(typeof(StackPanel));
            btnCol.SetValue(StackPanel.OrientationProperty, Orientation.Vertical);
            btnCol.SetValue(StackPanel.VerticalAlignmentProperty, VerticalAlignment.Center);

            var previewBtn = new FrameworkElementFactory(typeof(Button));
            previewBtn.SetValue(Button.ContentProperty, "预览");
            previewBtn.SetValue(Button.WidthProperty, 64.0);
            previewBtn.SetValue(Button.HeightProperty, 26.0);
            previewBtn.SetValue(Button.MarginProperty, new Thickness(0, 0, 0, 8));
            previewBtn.SetValue(Button.CursorProperty, Cursors.Hand);
            previewBtn.SetValue(Button.StyleProperty,
                BuildActionButtonStyle(Color.FromRgb(15, 118, 110), Color.FromRgb(13, 148, 136), Color.FromRgb(13, 148, 136), Color.FromRgb(17, 94, 89), Color.FromRgb(17, 94, 89), Brushes.White, false, 12.5));
            previewBtn.AddHandler(Button.ClickEvent, new RoutedEventHandler(OnRowPreviewClick));
            btnCol.AppendChild(previewBtn);

            var copyBtn = new FrameworkElementFactory(typeof(Button));
            copyBtn.SetValue(Button.ContentProperty, "复制");
            copyBtn.SetValue(Button.WidthProperty, 64.0);
            copyBtn.SetValue(Button.HeightProperty, 26.0);
            copyBtn.SetValue(Button.MarginProperty, new Thickness(0, 0, 0, 8));
            copyBtn.SetValue(Button.CursorProperty, Cursors.Hand);
            copyBtn.SetValue(Button.StyleProperty,
                BuildActionButtonStyle(Color.FromRgb(255, 255, 255), Color.FromRgb(248, 250, 252), Color.FromRgb(241, 245, 249), Color.FromRgb(226, 232, 240), Color.FromRgb(203, 213, 225), new SolidColorBrush(Color.FromRgb(30, 41, 59)), true, 12.5));
            copyBtn.AddHandler(Button.ClickEvent, new RoutedEventHandler(OnRowCopyClick));
            btnCol.AppendChild(copyBtn);

            var dlBtn = new FrameworkElementFactory(typeof(Button));
            dlBtn.SetValue(Button.ContentProperty, "下载");
            dlBtn.SetValue(Button.WidthProperty, 64.0);
            dlBtn.SetValue(Button.HeightProperty, 26.0);
            dlBtn.SetValue(Button.CursorProperty, Cursors.Hand);
            dlBtn.SetValue(Button.StyleProperty,
                BuildActionButtonStyle(Color.FromRgb(34, 197, 94), Color.FromRgb(22, 163, 74), Color.FromRgb(22, 163, 74), Color.FromRgb(21, 128, 61), Color.FromRgb(21, 128, 61), Brushes.White, false, 12.5));
            dlBtn.AddHandler(Button.ClickEvent, new RoutedEventHandler(OnRowDownloadClick));
            btnCol.AppendChild(dlBtn);

            root.AppendChild(btnCol);
            btnCol.SetValue(Grid.ColumnProperty, 1);

            var dt = new DataTemplate();
            dt.VisualTree = root;
            return dt;
        }

        LinkItem ItemFromButton(object sender)
        {
            var fe = sender as FrameworkElement;
            return fe == null ? null : fe.DataContext as LinkItem;
        }

        void OnRowPreviewClick(object sender, RoutedEventArgs e)
        {
            var it = ItemFromButton(sender);
            if (it != null && !string.IsNullOrEmpty(it.Url)) ShowPreviewWindow(it);
            else SetStatus("选中行无可预览的地址。");
        }

        void OnRowCopyClick(object sender, RoutedEventArgs e)
        {
            var it = ItemFromButton(sender);
            if (it != null && !string.IsNullOrEmpty(it.Url)) { Clipboard.SetText(it.Url); SetStatus("已复制地址：" + it.Url); }
            else SetStatus("无可复制的地址。");
        }

        void OnRowDownloadClick(object sender, RoutedEventArgs e)
        {
            var it = ItemFromButton(sender);
            if (it != null && !string.IsNullOrEmpty(it.Url)) DownloadOne(it);
            else SetStatus("无可下载的地址。");
        }

        void DownloadOne(LinkItem it)
        {
            var dlg = new System.Windows.Forms.SaveFileDialog();
            dlg.Title = "另存为";
            dlg.FileName = GetFileName(it.Url);
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            string dest = dlg.FileName;
            it.Status = "下载中";
            SetStatus("开始下载 → " + dest);
            Task.Run(() =>
            {
                try
                {
                    using (var wc = new WebClient())
                    {
                        wc.Headers[HttpRequestHeader.UserAgent] = "Mozilla/5.0";
                        wc.DownloadFile(it.Url, dest);
                    }
                    Dispatcher.BeginInvoke(new Action(() => { it.Status = "✓ 已下载"; SetStatus("已下载 → " + dest); }));
                }
                catch
                {
                    Dispatcher.BeginInvoke(new Action(() => { it.Status = "✗ 失败"; SetStatus("下载失败：" + it.Url); }));
                }
            });
        }

        System.Diagnostics.Process LaunchFfplay(string ffplayPath, string url, string source, bool embed = false)
        {
            // 隐藏控制台黑窗（CreateNoWindow + UseShellExecute=false），窗口限制 960×540 避免全屏；
            // -loop 0 循环播放（网络流需服务器支持 seek，CDN 一般支持）；embed=true 时加 -noborder（嵌入预览弹窗内无边框）
            var psi = new System.Diagnostics.ProcessStartInfo();
            psi.FileName = ffplayPath;
            psi.Arguments = "-loop 0 -x 960 -y 540" + (embed ? " -noborder" : "") + " -window_title \"" + (source ?? "播放").Replace("\"", "'") + "\" \"" + url + "\"";
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            return System.Diagnostics.Process.Start(psi);
        }

        // ---- ffplay 嵌入宿主：把 ffplay 窗口 SetParent 到预览弹窗内容区，实现 webm 在弹窗内动态播放 ----
        class MediaHost : System.Windows.Interop.HwndHost
        {
            IntPtr _placeholder;
            IntPtr _target;
            protected override System.Runtime.InteropServices.HandleRef BuildWindowCore(System.Runtime.InteropServices.HandleRef hwndParent)
            {
                _placeholder = CreateWindowEx(0, "static", "", 0x40000000 /*WS_CHILD*/ | 0x10000000 /*WS_VISIBLE*/,
                    0, 0, 10, 10, hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                return new System.Runtime.InteropServices.HandleRef(this, _placeholder);
            }
            protected override void DestroyWindowCore(System.Runtime.InteropServices.HandleRef hwnd)
            {
                if (hwnd.Handle != IntPtr.Zero) DestroyWindow(hwnd.Handle);
                _placeholder = IntPtr.Zero;
            }
            public void Attach(IntPtr target)
            {
                _target = target;
                if (_placeholder == IntPtr.Zero) return;
                SetParent(target, _placeholder);
                SetWindowPos(target, IntPtr.Zero, 0, 0, Math.Max(8, (int)ActualWidth), Math.Max(8, (int)ActualHeight), 0x0040);
            }
            protected override void OnRenderSizeChanged(System.Windows.SizeChangedInfo sizeInfo)
            {
                base.OnRenderSizeChanged(sizeInfo);
                if (_target != IntPtr.Zero && _placeholder != IntPtr.Zero)
                    SetWindowPos(_target, IntPtr.Zero, 0, 0, Math.Max(8, (int)ActualWidth), Math.Max(8, (int)ActualHeight), 0x0040);
            }
            [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
            static extern IntPtr CreateWindowEx(int exStyle, string cls, string name, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
            [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
            static extern bool DestroyWindow(IntPtr h);
            [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
            static extern IntPtr SetParent(IntPtr child, IntPtr parent);
            [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
            static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int height, uint flags);
        }

        StackPanel BuildStatusBar()
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(20, 4, 20, 12) };
            _statusText = new TextBlock { FontSize = 12, Foreground = (Brush)FindResource("TextSecondary"), VerticalAlignment = VerticalAlignment.Center };
            _countText = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("TextPrimary"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20, 0, 0, 0) };
            sp.Children.Add(_statusText); sp.Children.Add(_countText);
            return sp;
        }
        #endregion

        #region Actions

        void ChooseFolder()
        {
            var d = new System.Windows.Forms.FolderBrowserDialog();
            d.Description = "选择包含缓存的文件夹（可勾选仅处理缓存文件）";
            if (d.ShowDialog() == System.Windows.Forms.DialogResult.OK) ProcessPath(d.SelectedPath);
        }

        void ChooseFiles()
        {
            var d = new System.Windows.Forms.OpenFileDialog();
            d.Multiselect = true;
            d.Title = "选择一个或多个缓存文件";
            if (d.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                foreach (var f in d.FileNames) ProcessPath(f);
        }

        void ProcessPath(string path)
        {
            try
            {
                _newCount = 0; _dupCount = 0;
                var pending = new List<LinkItem>(); // 本次扫描的新记录缓冲：确认批次后才真正加入列表
                if (Directory.Exists(path))
                {
                    var files = Directory.GetFiles(path, "*.*", SearchOption.AllDirectories);
                    if (_filterCache.IsChecked == true)
                        files = files.Where(f => Regex.IsMatch(System.IO.Path.GetFileName(f), @"^(data_|f_)", RegexOptions.IgnoreCase)).ToArray();
                    foreach (var f in files) ScanFile(f, pending);
                    SetStatus("已扫描文件夹 " + path + "，处理文件 " + files.Length + " 个 · 新增 " + _newCount + " 条，跳过重复 " + _dupCount + " 条");
                }
                else if (File.Exists(path))
                {
                    ScanFile(path, pending);
                    SetStatus("已扫描文件：" + path + " · 新增 " + _newCount + " 条，跳过重复 " + _dupCount + " 条");
                }
                else SetStatus("路径不存在：" + path);

                // 有新记录 → 询问归入哪个批次（合并已有 / 新建），避免新旧混在一起无法区分
                if (pending.Count > 0)
                {
                    string defaultName = Directory.Exists(path) ? new DirectoryInfo(path).Name : System.IO.Path.GetFileNameWithoutExtension(path);
                    var existing = _items.Select(i => i.Batch).Where(b => !string.IsNullOrEmpty(b)).Distinct().ToList();
                    // 弹窗前激活主窗口：拖入文件时前台可能是资源管理器，不激活的话模态弹窗会被其他窗口遮住
                    this.Activate();
                    string batch = AskBatch(existing, defaultName, pending.Count,
                        Directory.Exists(path) ? new DirectoryInfo(path).Name : System.IO.Path.GetFileName(path));
                    this.Activate(); // 弹窗关闭后把激活/前台拉回主窗口，避免被其他窗口抢占
                    if (batch == null)
                    {
                        // 取消导入：把已登记去重的 URL 释放，允许下次重新导入
                        foreach (var it in pending)
                            if (!string.IsNullOrEmpty(it.Url)) _seen.Remove(it.Url);
                        SetStatus("已取消导入，丢弃 " + pending.Count + " 条新记录。");
                        return;
                    }
                    // 只记录有效记录：无有效地址链接的记录（响应头辅助段）不进入列表、不计入统计
                    int validCnt = 0;
                    string importNow = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    foreach (var it in pending) { if (it.Url.Length == 0) continue; it.Batch = batch; it.ImportTime = importNow; _items.Add(it); validCnt++; }
                    SetStatus("已导入 " + validCnt + " 条 → 记录「" + batch + "」（合并/新建完成，可切换记录筛选查看）");
                }
                else
                {
                    // 没提取到新记录：明显提示（弹窗），避免用户以为导入成功了
                    // 模态提示（Owner=主窗口）：弹窗期间主窗口不可操作，避免误点其他功能
                    System.Windows.MessageBox.Show(this, "本次未提取到新记录：新增 0 条，跳过重复 " + _dupCount + " 条。\n请确认所选文件/文件夹中包含 webm / mp4 / png / jpg 等媒体链接。",
                        "拾光", MessageBoxButton.OK, MessageBoxImage.Information);
                    SetStatus("未提取到新记录：新增 0 条，跳过重复 " + _dupCount + " 条。");
                }
                SaveHistory(); UpdateCount(); ApplyFilter(); RebuildBatchFilter();
            }
            catch (Exception ex) { SetStatus("扫描出错：" + ex.Message); }
        }

        // 导入批次选择对话框：合并到已有批次 / 新建批次；返回 null 表示取消
        // sourceName：当前处理的来源文件/文件夹名（逐个弹窗时提示当前处理的是哪个），为空则不显示来源
        string AskBatch(List<string> existing, string defaultName, int count, string sourceName)
        {
            var win = new Window
            {
                Title = "选择导入记录",
                Width = 480,
                SizeToContent = SizeToContent.Height, // 高度随内容自适应，避免按钮被窗口边缘截掉
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                // 不用 Topmost：弹窗前已 this.Activate() 保证主窗口在前台，模态弹窗自然可见；
                // 用户切到其他软件时弹窗自然退后让位，不再压盖任何窗口
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Background = (Brush)FindResource("AppBackground"),
                FontFamily = FontFamily,
                FontSize = 13.5
            };
            // ESC 关闭导入弹窗 = 取消导入
            win.PreviewKeyDown += (s2, e2) => { if (e2.Key == Key.Escape) win.Close(); };
            var root = new StackPanel { Margin = new Thickness(20, 16, 20, 14) };
            var hintText = string.IsNullOrEmpty(sourceName)
                ? "本次导入 " + count + " 条新记录，归入哪条记录？"
                : "本次从「" + sourceName + "」提取 " + count + " 条新记录，归入哪条记录？";
            root.Children.Add(new TextBlock { Text = hintText, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(15, 118, 110)), Margin = new Thickness(0, 0, 0, 14) });

            // 单选禁用态样式：文字灰化 + 半透明，与正常态明显区分
            var rbStyle = new Style(typeof(RadioButton));
            rbStyle.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(30, 41, 59))));
            var rbDis = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            rbDis.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(148, 163, 184))));
            rbDis.Setters.Add(new Setter(UIElement.OpacityProperty, 0.65));
            rbStyle.Triggers.Add(rbDis);
            var rbMerge = new RadioButton { Content = "合并到已有记录", FontSize = 13, Margin = new Thickness(0, 0, 0, 6), Style = rbStyle };
            var cbBatch = new ComboBox { Width = 404, Height = 36, Margin = new Thickness(22, 0, 0, 14), HorizontalAlignment = HorizontalAlignment.Left, Style = BuildComboBoxStyle(), ItemContainerStyle = BuildComboBoxItemStyle() };
            foreach (var b in existing) cbBatch.Items.Add(MakeBatchItem(b)); // 显示批次名 + 导入日期副标题
            if (existing.Count > 0) cbBatch.SelectedIndex = 0;

            var rbNew = new RadioButton { Content = "新建记录", FontSize = 13, Margin = new Thickness(0, 0, 0, 6), Style = rbStyle };
            var tbNew = new TextBox { Text = defaultName, MinHeight = 38, MaxLines = 3, Style = InputBoxStyle(), TextWrapping = TextWrapping.Wrap }; // 默认 38 高与按钮一致；多行时增高至最多 3 行完整显示
            var tbNewGrid = MakeHintedInput(tbNew, "输入记录名称"); // 空时显示提示词，输入后隐藏
            tbNewGrid.Width = 404;
            tbNewGrid.Margin = new Thickness(22, 0, 0, 16);

            rbMerge.IsChecked = existing.Count > 0;
            if (existing.Count == 0) rbNew.IsChecked = true;
            rbMerge.IsEnabled = existing.Count > 0; // 没有任何已有记录时合并选项不可选，只能新建
            cbBatch.IsEnabled = existing.Count > 0;
            tbNew.IsEnabled = existing.Count == 0;
            rbMerge.Checked += (s, e) => { cbBatch.IsEnabled = true; tbNew.IsEnabled = false; };
            rbNew.Checked += (s, e) => { cbBatch.IsEnabled = false; tbNew.IsEnabled = true; };

            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 2, 0, 0) };
            var style = BuildActionButtonStyle(Color.FromRgb(255, 255, 255), Color.FromRgb(248, 250, 252), Color.FromRgb(241, 245, 249), Color.FromRgb(226, 232, 240), Color.FromRgb(203, 213, 225), new SolidColorBrush(Color.FromRgb(30, 41, 59)), true, 13.0);
            var btnOk = MakeBtn("确定", PrimaryButtonStyle(13.0), 84, 38); // 主操作：主题色
            btnOk.Margin = new Thickness(0, 0, 10, 0);
            var btnCancel = MakeBtn("取消", style, 84, 38); // 次操作：白底
            string result = null;
            btnOk.Click += (s, e) =>
            {
                string v = tbNew.Text;
                var ci = cbBatch.SelectedItem as ComboBoxItem;
                if (rbMerge.IsChecked == true && ci != null) v = (ci.Tag ?? ci.Content).ToString(); // 优先取纯批次名（Content 可能带日期副标题）
                if (string.IsNullOrWhiteSpace(v)) v = defaultName;
                result = v.Trim();
                win.DialogResult = true;
            };
            btnCancel.Click += (s, e) => { win.DialogResult = false; };
            row.Children.Add(btnOk); row.Children.Add(btnCancel);

            root.Children.Add(rbMerge); root.Children.Add(cbBatch);
            root.Children.Add(rbNew); root.Children.Add(tbNewGrid);
            root.Children.Add(row);
            win.Content = root;
            return win.ShowDialog() == true ? result : null;
        }

        void ScanFile(string file, List<LinkItem> pending)
        {
            byte[] bytes;
            // 读取文件：优先直接打开；若被其他进程独占锁定，则复制到临时文件再解析
            bool readOk = false;
            try
            {
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    bytes = new byte[fs.Length];
                    int off = 0;
                    while (off < bytes.Length)
                    {
                        int rd = fs.Read(bytes, off, bytes.Length - off);
                        if (rd <= 0) break;
                        off += rd;
                    }
                }
                readOk = true;
            }
            catch
            {
                bytes = null;
            }

            if (!readOk)
            {
                // 直接读取失败（文件被占用）→ 复制到临时目录再读取
                string tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "linkext_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".bin");
                try
                {
                    File.Copy(file, tmp, true);
                    using (var fs = new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        bytes = new byte[fs.Length];
                        int off = 0;
                        while (off < bytes.Length)
                        {
                            int rd = fs.Read(bytes, off, bytes.Length - off);
                            if (rd <= 0) break;
                            off += rd;
                        }
                    }
                    try { File.Delete(tmp); } catch { }
                }
                catch { return; }
            }

            string text;
            try { text = Encoding.ASCII.GetString(bytes); } catch { return; }

            foreach (Match m in UrlRegex.Matches(text))
            {
                string url = m.Value;
                if (!_seen.Add(url)) { _dupCount++; continue; }
                _newCount++;
                var it = new LinkItem();
                it.Url = url;
                it.Type = ClassifyType(url);
                it.Source = ClassifySource(url);
                var pd = PathDateRegex.Match(url);
                if (pd.Success) it.Time = pd.Groups[1].Value + "-" + pd.Groups[2].Value + "-" + pd.Groups[3].Value;
                it.Note = ""; it.Status = "";
                pending.Add(it);
            }

            foreach (Match cm in ContentTypeRegex.Matches(text))
            {
                string ct = cm.Groups[1].Value.ToLower();
                if (!(ct.StartsWith("video/") || ct.StartsWith("image/") || ct.StartsWith("audio/"))) continue;
                int len = Math.Min(400, text.Length - cm.Index);
                string seg = text.Substring(cm.Index, len);
                string etag = EtagRegex.Match(seg).Groups[1].Value;
                string time = PathDateFromSegment(text, cm.Index);
                bool linked = false;
                if (etag.Length > 0)
                    foreach (var rec in _items)
                        if (rec.Url.IndexOf(etag, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            // 仅当时间缺失时用响应头补全；不修改已有记录的备注（备注归用户手动管理）
                            if (string.IsNullOrEmpty(rec.Time) && time.Length > 0) rec.Time = time;
                            linked = true; break;
                        }
                if (linked) continue;
                if (_seen.Add("CT:" + cm.Index))
                {
                    // 无有效地址的响应头段：仅做本次运行的去重标记，不生成记录（不算"记录"、不显示、不写入）
                }
            }
        }

        string PathDateFromSegment(string text, int start)
        {
            int back = Math.Max(0, start - 1200);
            int len = Math.Min(1200, text.Length - back);
            var pd = PathDateRegex.Match(text.Substring(back, len));
            return pd.Success ? pd.Groups[1].Value + "-" + pd.Groups[2].Value + "-" + pd.Groups[3].Value : "";
        }

        string ClassifyType(string url)
        {
            string u = url.ToLowerInvariant();
            if (u.EndsWith(".webm") || u.EndsWith(".mp4") || u.EndsWith(".mov")) return "视频";
            if (u.EndsWith(".png") || u.EndsWith(".jpg") || u.EndsWith(".jpeg") || u.EndsWith(".gif") || u.EndsWith(".webp")) return "图片";
            if (u.EndsWith(".mp3") || u.EndsWith(".ogg") || u.EndsWith(".wav") || u.EndsWith(".m4a")) return "音频";
            return "其他";
        }

        string MapContentType(string ct)
        {
            if (ct.StartsWith("video/")) return "视频";
            if (ct.StartsWith("image/")) return "图片";
            if (ct.StartsWith("audio/")) return "音频";
            return "其他";
        }

        string ClassifySource(string url)
        {
            string u = url.ToLowerInvariant();
            if (u.Contains("mihoyo") || u.Contains("launcher-webstatic")) return "启动器缓存";
            if (u.Contains("hoyoverse")) return "游戏缓存";
            if (u.Contains("wegame") || u.Contains("tencent")) return "腾讯/WeGame";
            if (u.Contains("mi.com") || u.Contains("xiaomi")) return "小米";
            var dm = Regex.Match(url, @"https?://([^/]+)", RegexOptions.IgnoreCase);
            return dm.Success ? dm.Groups[1].Value : "未知";
        }

        void ApplyFilter()
        {
            if (_updatingGrid) return;
            _updatingGrid = true;
            try
            {
                string kw = (_search.Text ?? "").Trim().ToLowerInvariant();
                var selType = _typeFilter.SelectedItem as ComboBoxItem;
                var selSrc = _srcFilter.SelectedItem as ComboBoxItem;
                string ttype = (selType != null && selType.Tag != null) ? selType.Tag.ToString() : "全部类型";
                string tsrc = (selSrc != null && selSrc.Tag != null) ? selSrc.Tag.ToString() : "全部来源";
                var selBatch = _batchFilter.SelectedItem as ComboBoxItem;
                string tbatch = (selBatch != null && selBatch.Tag != null) ? selBatch.Tag.ToString() : "全部记录";
                var view = CollectionViewSource.GetDefaultView(_items);
                view.Filter = o =>
                {
                    var it = (LinkItem)o;
                    if (string.IsNullOrEmpty(it.Url)) return false; // 无地址的辅助记录（响应头记录）不参与列表显示
                    if (ttype != "全部类型" && it.Type != ttype) return false;
                    if (tsrc != "全部来源" && it.Source != tsrc) return false;
                    if (tbatch != "全部记录" && (it.Batch ?? "历史记录") != tbatch) return false;
                    if (kw.Length > 0)
                    {
                        if (!(it.Url.ToLowerInvariant().Contains(kw) || (it.Time ?? "").Contains(kw) || (it.Source ?? "").ToLowerInvariant().Contains(kw) || (it.Note ?? "").ToLowerInvariant().Contains(kw))) return false;
                    }
                    return true;
                };
                UpdateCount(view);
            }
            finally
            {
                _updatingGrid = false;
            }
            // 重建下拉必须在守卫解除后执行，否则会被 _updatingGrid 直接跳过，导致筛选下拉永远只有默认项
            RebuildSourceFilter();
            RebuildBatchFilter();
        }

        void TypeFilterChanged(object s, SelectionChangedEventArgs e) { ApplyFilter(); }
        void SrcFilterChanged(object s, SelectionChangedEventArgs e) { ApplyFilter(); }
        void BatchFilterChanged(object s, SelectionChangedEventArgs e) { ApplyFilter(); }

        void RebuildSourceFilter()
        {
            if (_updatingGrid) return;
            string cur = "";
            var sel = _srcFilter.SelectedItem as ComboBoxItem;
            if (sel != null && sel.Tag != null) cur = sel.Tag.ToString();
            var srcs = _items.Select(i => i.Source).Where(s => !string.IsNullOrEmpty(s)).Distinct().OrderBy(s => s).ToList();
            _srcFilter.SelectionChanged -= SrcFilterChanged;
            _srcFilter.Items.Clear();
            _srcFilter.Items.Add(MakeBatchItem("全部来源"));
            foreach (var s in srcs) _srcFilter.Items.Add(MakeBatchItem(s));
            int idx = _srcFilter.Items.Cast<ComboBoxItem>().ToList().FindIndex(c => c.Tag != null && c.Tag.ToString() == cur);
            _srcFilter.SelectedIndex = idx >= 0 ? idx : 0;
            _srcFilter.SelectionChanged += SrcFilterChanged;
        }

        // 批次导入日期：该批次内记录 ImportTime 的最大值（覆盖导入自动更新为新时间），格式 yyyy-MM-dd
        string BatchDate(string b)
        {
            string latest = "";
            foreach (var i in _items)
                if ((i.Batch ?? "") == b && !string.IsNullOrEmpty(i.ImportTime) && string.CompareOrdinal(i.ImportTime, latest) > 0) latest = i.ImportTime;
            return latest.Length >= 10 ? latest.Substring(0, 10) : "";
        }

        // 筛选下拉框公共工厂：样式/尺寸/对齐一处定义，所有筛选下拉共用，杜绝各自初始化导致的样式漂移
        ComboBox MakeFilterCombo(double width)
        {
            var c = new ComboBox
            {
                Width = width,
                Height = 38,
                VerticalAlignment = VerticalAlignment.Center,
                Style = BuildComboBoxStyle(),
                ItemContainerStyle = BuildComboBoxItemStyle()
            };
            // 点击框体任意位置强制展开（兜底逻辑：不依赖 ToggleButton 命中）。
            // 已展开时不再拦截——展开后的点击（选列表项/点外部收起）全部交给默认交互，避免误收起导致"选不了"
            c.PreviewMouseLeftButtonDown += (s, e) =>
            {
                var combo = (ComboBox)s;
                if (combo.IsDropDownOpen) return;
                combo.IsDropDownOpen = true;
                e.Handled = true;
            };
            return c;
        }

        // 下拉项公共样式：列表项内容为"名称 + 条数 + 导入日期"完整混排（长名自动换行）；
        // 选中框由 ComboBox 模板绑定 SelectedItem.Tag 只显示纯名称（单行省略），互不干扰
        ComboBoxItem MakeBatchItem(string b)
        {
            var ci = new ComboBoxItem { Tag = b };
            string dt = BatchDate(b);
            int cnt = (b == "全部记录") ? _items.Count : _items.Count(i => (i.Batch ?? "历史记录") == b);
            var tb = new TextBlock
            {
                FontSize = 13.0,
                Foreground = new SolidColorBrush(Color.FromRgb(30, 41, 59)),
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 440 // 列表项最大宽度：长名称超过后自动换行，不横向撑破下拉列表
            };
            tb.Inlines.Add(new Run { Text = b, FontSize = 13.0, Foreground = new SolidColorBrush(Color.FromRgb(30, 41, 59)) });
            tb.Inlines.Add(new Run { Text = "　·　" + cnt + " 条", FontSize = 11, Foreground = (Brush)FindResource("TextSecondary") });
            if (dt.Length > 0)
                tb.Inlines.Add(new Run { Text = "　·　导入于 " + dt, FontSize = 11, Foreground = (Brush)FindResource("TextSecondary") });
            ci.Content = tb;
            ci.ToolTip = dt.Length > 0 ? b + " · 导入于 " + dt : b; // 悬停显示完整名称
            return ci;
        }

        void RebuildBatchFilter()
        {
            if (_updatingGrid) return;
            string cur = "";
            var sel = _batchFilter.SelectedItem as ComboBoxItem;
            if (sel != null && sel.Tag != null) cur = sel.Tag.ToString();
            var batches = _items.Select(i => i.Batch).Where(b => !string.IsNullOrEmpty(b)).Distinct().OrderBy(b => b).ToList();
            _batchFilter.SelectionChanged -= BatchFilterChanged;
            _batchFilter.Items.Clear();
            _batchFilter.Items.Add(MakeBatchItem("全部记录"));
            foreach (var b in batches) _batchFilter.Items.Add(MakeBatchItem(b));
            int idx = cur.Length > 0 ? _batchFilter.Items.Cast<ComboBoxItem>().ToList().FindIndex(c => (c.Tag != null && c.Tag.ToString() == cur)) : 0;
            _batchFilter.SelectedIndex = idx >= 0 ? idx : 0;
            _batchFilter.SelectionChanged += BatchFilterChanged;
        }

        void RefreshBatchList(ListBox list)
        {
            list.Items.Clear();
            foreach (var b in _items.Select(i => i.Batch).Where(x => !string.IsNullOrEmpty(x)).Distinct().OrderBy(x => x))
            {
                int cnt = _items.Count(i => (i.Batch ?? "历史记录") == b);
                string dt = BatchDate(b);
                string txt = b + "　·　" + cnt + " 条" + (dt.Length > 0 ? "　·　导入于 " + dt : "");
                // 长批次名在弹窗列表内换行显示完整，不横向撑破窗口
                list.Items.Add(new ListBoxItem
                {
                    Content = new TextBlock { Text = txt, TextWrapping = TextWrapping.Wrap, MaxWidth = 400 },
                    Tag = b
                });
            }
        }

        void ManageBatches()
        {
            var dlg = new Window
            {
                Title = "管理记录",
                Width = 470,
                Height = 500,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                Background = (Brush)FindResource("AppBackground"),
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.SingleBorderWindow,
                FontSize = 13
            };
            dlg.PreviewKeyDown += (s2, e2) => { if (e2.Key == Key.Escape) dlg.Close(); }; // ESC 关闭管理窗口
            var root = new StackPanel { Margin = new Thickness(16) };
            var tip = new TextBlock
            {
                Text = "这里管理的是记录的导入分组。选中分组后可重命名或删除。删除将移除该分组全部记录，不可恢复。",
                Foreground = (Brush)FindResource("TextSecondary"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            };
            var list = new ListBox { Height = 230, FontSize = 13, Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(203, 213, 225)), BorderThickness = new Thickness(1) };
            // 列表项样式：自定义 ControlTemplate 彻底接管选中/悬停样式
            // （WPF 默认 ListBoxItem 模板在选中态会用系统高亮覆盖自定义背景，导致选中不明显）
            var liStyle = new Style(typeof(ListBoxItem));
            liStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 6, 10, 6)));
            liStyle.Setters.Add(new Setter(Control.FontSizeProperty, 13.0));
            liStyle.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(30, 41, 59))));
            liStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            var liTpl = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
                "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='ListBoxItem'>" +
                "<Border x:Name='Bd' Background='{TemplateBinding Background}' Padding='{TemplateBinding Padding}' SnapsToDevicePixels='true'><ContentPresenter HorizontalAlignment='Left' VerticalAlignment='Center'/></Border>" +
                "<ControlTemplate.Triggers>" +
                "<Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Bd' Property='Background' Value='#F1F5F9'/></Trigger>" +
                "<Trigger Property='IsSelected' Value='True'><Setter TargetName='Bd' Property='Background' Value='#DBEAFE'/><Setter Property='Foreground' Value='#1D4ED8'/><Setter Property='FontWeight' Value='SemiBold'/></Trigger>" +
                "</ControlTemplate.Triggers></ControlTemplate>");
            liStyle.Setters.Add(new Setter(Control.TemplateProperty, liTpl));
            list.ItemContainerStyle = liStyle;
            RefreshBatchList(list);
            var renameLabel = new TextBlock { Text = "新名称（重命名后点「重命名」）：", Foreground = (Brush)FindResource("TextSecondary"), Margin = new Thickness(0, 10, 0, 4) };
            var renameBox = new TextBox { Style = InputBoxStyle(), MinHeight = 38, MaxLines = 3, TextWrapping = TextWrapping.Wrap }; // 默认 38 高与按钮一致；多行时增高至最多 3 行完整显示
            var renameBoxGrid = MakeHintedInput(renameBox, "输入新名称"); // 空时显示提示词，输入后隐藏
            renameBoxGrid.Margin = new Thickness(0, 0, 0, 2);
            // 选中批次后自动把旧名填入输入框，直接改即可重命名，交互更明确
            // 未选中任何分组时，"重命名/删除分组"禁用（灰化），限制无效操作
            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
            var btnRename = MakeBtn("重命名", PrimaryButtonStyle(13.0), 96, 38); // 主操作：主题色
            var btnDelete = MakeBtn("删除分组", DangerButtonStyle(13.0), 96, 38); // 危险操作：红色
            var btnClose = MakeBtn("关闭", (Style)FindResource("ToolButton"), 96, 38); // 次操作：白底
            btnRename.IsEnabled = false; // 初始未选中任何分组：重命名/删除禁用
            btnDelete.IsEnabled = false;
            // 选中批次后自动把旧名填入输入框，直接改即可重命名，交互更明确
            // 未选中任何分组时，"重命名/删除分组"禁用（灰化），限制无效操作
            list.SelectionChanged += (s, e) =>
            {
                var sel = list.SelectedItem as ListBoxItem;
                bool hasSel = sel != null && sel.Tag != null;
                renameBox.Text = hasSel ? sel.Tag.ToString() : "";
                btnRename.IsEnabled = hasSel;
                btnDelete.IsEnabled = hasSel;
            };
            btnRename.Margin = new Thickness(0, 0, 8, 0);
            btnDelete.Margin = new Thickness(0, 0, 8, 0);
            btnRename.Click += (s, e) =>
            {
                var sel = list.SelectedItem as ListBoxItem;
                if (sel == null) { renameBox.Text = "请先选择分组"; return; }
                string oldName = sel.Tag as string;
                string newName = renameBox.Text.Trim();
                if (newName.Length == 0) return;
                if (newName == oldName) return;
                // 同名保护：仅检查"其他分组"是否已占用新名（排除自己），存在则需确认合并
                bool nameExists = _items.Any(i => (i.Batch ?? "历史记录") != oldName && (i.Batch ?? "历史记录") == newName);
                if (nameExists)
                {
                    if (System.Windows.MessageBox.Show(this, "已存在同名分组「" + newName + "」，重命名后将与其合并，是否继续？", "拾光",
                        MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                }
                int n = 0;
                foreach (var it in _items)
                    if ((it.Batch ?? "历史记录") == oldName) { it.Batch = newName; n++; }
                SaveHistory();
                // 先重建主下拉（选项名已变），再保持筛选状态选中新名，最后刷新列表——
                // 顺序颠倒会导致下拉/列表按旧名互相错位（列表空、下拉却显示其他项）
                RebuildBatchFilter();
                int idxNew = _batchFilter.Items.Cast<ComboBoxItem>().ToList().FindIndex(c => (c.Tag != null && c.Tag.ToString() == newName));
                _batchFilter.SelectionChanged -= BatchFilterChanged;
                _batchFilter.SelectedIndex = idxNew >= 0 ? idxNew : 0;
                _batchFilter.SelectionChanged += BatchFilterChanged;
                ApplyFilter();
                RefreshBatchList(list);
                SetStatus("已重命名分组「" + oldName + "」→「" + newName + "」（" + n + " 条记录）。");
            };
            btnDelete.Click += (s, e) =>
            {
                var sel = list.SelectedItem as ListBoxItem;
                if (sel == null) return;
                string name = sel.Tag as string;
                if (System.Windows.MessageBox.Show(this, "确定删除该分组「" + name + "」及其全部记录？此操作不可恢复。", "拾光",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                var doomed = _items.Where(i => (i.Batch ?? "历史记录") == name).ToList();
                foreach (var it in doomed) if (!string.IsNullOrEmpty(it.Url)) _seen.Remove(it.Url);
                foreach (var it in doomed) _items.Remove(it);
                SaveHistory();
                RebuildBatchFilter(); // 主下拉选项重建（被删批次消失，选中自动回落"全部记录"）
                ApplyFilter();
                RefreshBatchList(list);
                SetStatus("已删除分组「" + name + "」，移除 " + doomed.Count + " 条记录。");
            };
            btnClose.Click += (s, e) => dlg.Close();
            btnRow.Children.Add(btnRename); btnRow.Children.Add(btnDelete); btnRow.Children.Add(btnClose);
            root.Children.Add(tip);
            root.Children.Add(list);
            root.Children.Add(renameLabel);
            root.Children.Add(renameBoxGrid);
            root.Children.Add(btnRow);
            dlg.Content = root;
            dlg.ShowDialog();
        }

        void PreviewSelected()
        {
            var it = _grid.SelectedItem as LinkItem;
            if (it != null && !string.IsNullOrEmpty(it.Url)) ShowPreviewWindow(it);
            else SetStatus("选中行无可预览的地址。");
        }

        void ShowPreviewWindow(LinkItem it)
        {
            // 预览弹窗单例：已有一个弹窗时先关闭（Closed 里会停止播放并清理），
            // 避免多个弹窗并存、互相抢占 _currentFfplay/_currentMedia 造成播放混乱
            if (_previewWindow != null)
            {
                try { _previewWindow.Close(); } catch { }
                _previewWindow = null;
            }
            // 构建当前可见的、有地址的项导航列表（与筛选保持一致：类型+来源+批次+关键词）
            var selType2 = _typeFilter.SelectedItem as ComboBoxItem;
            var selSrc2 = _srcFilter.SelectedItem as ComboBoxItem;
            string ftype = (selType2 != null && selType2.Tag != null) ? selType2.Tag.ToString() : "全部类型";
            string fsrc = (selSrc2 != null && selSrc2.Tag != null) ? selSrc2.Tag.ToString() : "全部来源";
            var selBatch2 = _batchFilter.SelectedItem as ComboBoxItem;
            string fbatch = (selBatch2 != null && selBatch2.Tag != null) ? selBatch2.Tag.ToString() : "全部记录";
            string fkw = (_search.Text ?? "").Trim().ToLowerInvariant();
            var items = _items.Where(x =>
                !string.IsNullOrEmpty(x.Url) &&
                (ftype == "全部类型" || x.Type == ftype) &&
                (fsrc == "全部来源" || x.Source == fsrc) &&
                (fbatch == "全部记录" || (x.Batch ?? "历史记录") == fbatch) &&
                (fkw.Length == 0 || x.Url.ToLowerInvariant().Contains(fkw) || (x.Time ?? "").Contains(fkw) || (x.Source ?? "").ToLowerInvariant().Contains(fkw) || (x.Note ?? "").ToLowerInvariant().Contains(fkw))
            ).ToList();
            // 弹窗上下切换顺序跟随列表当前排序（时间列：默认降序/最新在前，与 DataGrid 显示顺序一致）
            var srcView = CollectionViewSource.GetDefaultView(_items);
            if (srcView.SortDescriptions.Count > 0 && srcView.SortDescriptions[0].PropertyName == "Time")
            {
                if (srcView.SortDescriptions[0].Direction == ListSortDirection.Descending)
                    items = items.OrderByDescending(x => x.Time ?? "").ToList();
                else
                    items = items.OrderBy(x => x.Time ?? "").ToList();
            }
            int cur = items.FindIndex(x => x == it);
            if (cur < 0) cur = items.Count > 0 ? 0 : -1;
            if (cur < 0 || items.Count == 0) { SetStatus("当前没有可预览的资源。"); return; }

            var win = new Window
            {
                Title = "预览",
                Width = 720, Height = 720,
                MinWidth = 560, MinHeight = 560,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                // 弹窗背景用交替行浅灰(240,244,250)：与白色卡片拉开层次，卡片结构一目了然
                Background = (Brush)FindResource("RowAlt")
            };
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // 图片查看区：标准图片查看器架构——ScrollViewer 承载平移（滚动条由 WPF 框架保证正确、自动限界），
            // 内容容器尺寸 = 图片×缩放比例，缩放只改容器尺寸一个变量，平移零手动坐标计算。
            var svImg = new Grid
            {
                // 必须裁剪：放大后图片只显示在查看区内，不溢出到弹窗其他区域
                ClipToBounds = true,
                Margin = new Thickness(0), // 无内边距：图片查看区贴住卡片容器四边
                Background = Brushes.Transparent
            };
            var scroller = new ScrollViewer
            {
                // 隐藏滚动条：与 Windows 照片查看器一致，平移靠按住左键拖动（程序化滚动，边界由框架保证）
                HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                HorizontalContentAlignment = HorizontalAlignment.Center, // 内容小于视口时自动居中
                VerticalContentAlignment = VerticalAlignment.Center,
                Background = Brushes.Transparent,
                Focusable = false
            };
            // 内容容器：显式尺寸 = 图片×scale；对齐 Center——内容小于视口时由 ScrollViewer 居中，
            // 大于视口时自动滚动（显式 Left/Top 会覆盖 ScrollViewer 的居中对齐，导致图片贴左上角）
            var imgHost = new Border { Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var img = new Image { Stretch = Stretch.Fill };
            imgHost.Child = img;
            scroller.Content = imgHost;
            svImg.Children.Add(scroller);
            // 图片交互（缩放/拖动/双击）不挂在 svImg/img 上——本环境实测 svImg 树（含其内任何元素）
            // 的 MouseDown 路由不可靠（命中测试疑似在 svImg 树失效），而滚轮隧道与 Window 级事件稳定。
            // 因此统一挂到弹窗 Window 的 Preview（隧道）系列上，用 GetPosition(svImg) 换算坐标，
            // 用 OriginalSource 是 Button 时跳过（避免干扰 bBg/底部操作按钮）。
            double curPw = 1.0, curPh = 1.0; // 当前图片 DIP 尺寸
            double _scale = 1.0;   // 当前缩放比例（imgHost 尺寸 = 图片×scale）
            bool _isFit = true;    // 是否处于完整适配状态（切换图片/复原后 true，主动缩放后 false）
            bool _imgDrag = false;
            bool _dragCandidate = false;
            System.Windows.Point _imgDragStart = new System.Windows.Point();
            System.Windows.Point _imgDragLast = new System.Windows.Point();
            Func<double> Cw = () => curPw * _scale;
            Func<double> Ch = () => curPh * _scale;
            TextBox zoomTxt = null; // 右下角缩放比例输入框（zoomBar 构建后赋值）
            // 完整适配比例：大图缩小到完整可见，小图 1:1（CSS contain 语义）
            Func<double> FitScale = () =>
            {
                double vw = svImg.ActualWidth, vh = svImg.ActualHeight;
                if (vw <= 1 || vh <= 1 || curPw <= 0 || curPh <= 0) return 1.0;
                return Math.Min(1.0, Math.Min(vw / curPw, vh / curPh));
            };
            // 应用缩放：只改 imgHost 尺寸一个变量（滚动条自动适配内容尺寸）
            Action<double> ApplyScale = (s) =>
            {
                s = Math.Max(0.05, Math.Min(10.0, s));
                _scale = s;
                imgHost.Width = curPw * s;
                imgHost.Height = curPh * s;
                if (zoomTxt != null) zoomTxt.Text = Math.Round(s * 100) + "%";
            };
            // 完整适配并居中（打开/切换/复原/窗口变化）
            Action FitAndCenter = () =>
            {
                if (img.Source == null) return;
                ApplyScale(FitScale());
                _isFit = true;
                double vw = scroller.ViewportWidth, vh = scroller.ViewportHeight;
                if (vw > 1 && vh > 1)
                {
                    scroller.ScrollToHorizontalOffset(Math.Max(0, (imgHost.Width - vw) / 2));
                    scroller.ScrollToVerticalOffset(Math.Max(0, (imgHost.Height - vh) / 2));
                }
            };
            // 围绕指定视口点缩放（滚轮/按钮/输入统一走此），保持该点内容位置不跳
            Action<double, double, double> ZoomAround = (ns, vpx, vpy) =>
            {
                if (img.Source == null) return;
                double s0 = Math.Max(_scale, 1e-6);
                double cx = (vpx + scroller.HorizontalOffset) / s0;
                double cy = (vpy + scroller.VerticalOffset) / s0;
                ApplyScale(ns);
                scroller.ScrollToHorizontalOffset(cx * _scale - vpx);
                scroller.ScrollToVerticalOffset(cy * _scale - vpy);
                _isFit = false;
            };
            // 播放状态/触发区：现代蓝渐变按钮（点击播放/重新播放），与行内按钮同一套风格
            var hint = new Button
            {
                Content = "加载中…",
                Style = BuildActionButtonStyle(Color.FromRgb(15, 118, 110), Color.FromRgb(13, 148, 136), Color.FromRgb(13, 148, 136), Color.FromRgb(17, 94, 89), Color.FromRgb(17, 94, 89), Brushes.White, false, 14),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(30, 0, 30, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
                Padding = new Thickness(18, 8, 18, 8)
            };
            // 内容区卡片：白底 + 圆角边框（与浅灰弹窗背景形成明显层次）
            var contentHost = new Border
            {
                Background = (Brush)FindResource("CardBg"),
                BorderBrush = (Brush)FindResource("BorderColor"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Margin = new Thickness(14, 14, 14, 6)
            };
            var content = new Grid();
            // 透明图默认棋盘格垫底：透明+白内容的图在棋盘格上清晰可见
            var checker = CheckerBrush();
            var previewBgs = new List<Brush> { checker, new SolidColorBrush(Color.FromRgb(31, 41, 55)), Brushes.White };
            int bgIdx = 0;
            content.Background = previewBgs[bgIdx];
            // 背景切换按钮：浮在图片区左下角（图片查看器惯例），文字实时显示当前模式
            var bBg = new Button
            {
                Content = "背景：棋盘格",
                Style = BuildActionButtonStyle(Color.FromRgb(255, 255, 255), Color.FromRgb(248, 250, 252), Color.FromRgb(241, 245, 249), Color.FromRgb(226, 232, 240), Color.FromRgb(203, 213, 225), new SolidColorBrush(Color.FromRgb(30, 41, 59)), true, 12),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(10, 0, 0, 10),
                Cursor = System.Windows.Input.Cursors.Hand,
                Padding = new Thickness(11, 5, 11, 5),
                ToolTip = "切换透明图片预览背景：棋盘格 / 深色 / 浅色"
            };
            var bgNames = new[] { "背景：棋盘格", "背景：深色", "背景：浅色" };
            bBg.Click += (s, e) => { bgIdx = (bgIdx + 1) % previewBgs.Count; content.Background = previewBgs[bgIdx]; bBg.Content = bgNames[bgIdx]; };
            // 右下角缩放控制条：实时比例（可直接输入数值，回车生效）+ 缩小/放大/100%/适配（标准看图软件控制，风格与 bBg 统一）
            zoomTxt = new TextBox
            {
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(30, 41, 59)),
                Background = new SolidColorBrush(Color.FromRgb(255, 255, 255)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(203, 213, 225)),
                BorderThickness = new Thickness(1),
                VerticalContentAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                Width = 50,
                Padding = new Thickness(3, 1, 3, 1),
                ToolTip = "直接输入比例数值，回车生效（如 150 = 150%）"
            };
            zoomTxt.KeyDown += (s, e2) =>
            {
                if (e2.Key == Key.Enter)
                {
                    double v;
                    if (double.TryParse(zoomTxt.Text.Replace("%", "").Trim(), out v))
                    {
                        v = Math.Max(5, Math.Min(1000, v));
                        if (img.Source != null)
                        {
                            double ns = v / 100.0;
                            if (ns <= FitScale() + 0.001) FitAndCenter();
                            else ZoomAround(ns, scroller.ViewportWidth / 2, scroller.ViewportHeight / 2);
                        }
                    }
                    else if (img.Source != null) FitAndCenter();
                    e2.Handled = true;
                }
            };
            // 失焦时还原为当前实际比例（未回车提交的输入不生效）
            zoomTxt.LostFocus += (s, e2) => { if (zoomTxt != null) zoomTxt.Text = Math.Round(_scale * 100) + "%"; };
            var zoomStyle = BuildActionButtonStyle(Color.FromRgb(255, 255, 255), Color.FromRgb(248, 250, 252), Color.FromRgb(241, 245, 249), Color.FromRgb(226, 232, 240), Color.FromRgb(203, 213, 225), new SolidColorBrush(Color.FromRgb(30, 41, 59)), true, 12);
            var btnZoomOut = new Button { Content = "-", Style = zoomStyle, Cursor = System.Windows.Input.Cursors.Hand, Padding = new Thickness(7, 3, 7, 3), ToolTip = "缩小" };
            var btnZoomIn = new Button { Content = "+", Style = zoomStyle, Cursor = System.Windows.Input.Cursors.Hand, Padding = new Thickness(7, 3, 7, 3), ToolTip = "放大" };
            var btnZoom100 = new Button { Content = "复原", Style = zoomStyle, Cursor = System.Windows.Input.Cursors.Hand, Padding = new Thickness(7, 3, 7, 3), ToolTip = "回到默认完整展示" };
            // 步进缩放：围绕视口中心缩放（标准看图软件行为）
            Action<double> zoomStep = (factor) =>
            {
                if (img.Source == null) return;
                double ns = Math.Max(0.05, Math.Min(10.0, _scale * factor));
                ZoomAround(ns, scroller.ViewportWidth / 2, scroller.ViewportHeight / 2);
            };
            // 复原：回到完整适配并居中（与打开/切换时初始状态一致）
            Action zoomFitNow = () => FitAndCenter();
            btnZoomOut.Click += (s, e) =>
            {
                if (img.Source == null) return;
                if (!_isFit) { zoomStep(1.0 / 1.15); if (_scale <= FitScale() + 0.001) FitAndCenter(); }
            };
            btnZoomIn.Click += (s, e) =>
            {
                if (img.Source == null) return;
                zoomStep(1.15);
            };
            btnZoom100.Click += (s, e) => zoomFitNow();
            var zoomBar = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(210, 255, 255, 255)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(3, 2, 3, 2),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 8, 8)
            };
            var zoomPanel = new StackPanel { Orientation = Orientation.Horizontal };
            zoomPanel.Children.Add(btnZoomOut); zoomPanel.Children.Add(zoomTxt); zoomPanel.Children.Add(btnZoomIn);
            zoomPanel.Children.Add(btnZoom100);
            zoomBar.Child = zoomPanel;
            content.Children.Add(hint);
            content.Children.Add(svImg);
            // 背景按钮与缩放条浮在图片区内（左上/右下角，看图软件惯例）；不要放 content 底部——会被信息区挤到图片下方
            svImg.Children.Add(bBg);
            svImg.Children.Add(zoomBar);
            // ffplay 嵌入宿主：webm/ogg/mov 等 WMF 不支持的格式在弹窗内播放（SetParent 嵌入），与图片同区展示
            var mediaHost = new MediaHost { Visibility = Visibility.Collapsed, Margin = new Thickness(12) };
            content.Children.Add(mediaHost);
            contentHost.Child = content;
            Grid.SetRow(contentHost, 0);
            root.Children.Add(contentHost);

            // 信息区：结构化布局，与主界面表格统一（彩色类型徽章 + 灰色时间 + 深色来源 + 灰蓝完整链接），内容区分一目了然
            var infoHost = new Border
            {
                Background = (Brush)FindResource("CardBg"),
                BorderBrush = (Brush)FindResource("BorderColor"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Margin = new Thickness(14, 6, 14, 0),
                Padding = new Thickness(10, 8, 10, 8)
            };
            var infoPanel = new StackPanel();
            var metaRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 2) };
            _pvTypeDot = new TextBlock { Text = "●", FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            _pvTypeTxt = new TextBlock { FontSize = 13.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            var sep1 = new TextBlock { Text = "  ·  ", FontSize = 13.5, Foreground = (Brush)FindResource("TextSecondary"), VerticalAlignment = VerticalAlignment.Center };
            _pvTimeTxt = new TextBlock { FontSize = 13.5, Foreground = (Brush)FindResource("TextSecondary"), VerticalAlignment = VerticalAlignment.Center };
            var sep2 = new TextBlock { Text = "  ·  ", FontSize = 13.5, Foreground = (Brush)FindResource("TextSecondary"), VerticalAlignment = VerticalAlignment.Center };
            _pvSrcTxt = new TextBlock { FontSize = 13.5, Foreground = (Brush)FindResource("TextPrimary"), VerticalAlignment = VerticalAlignment.Center };
            metaRow.Children.Add(_pvTypeDot); metaRow.Children.Add(_pvTypeTxt); metaRow.Children.Add(sep1);
            metaRow.Children.Add(_pvTimeTxt); metaRow.Children.Add(sep2); metaRow.Children.Add(_pvSrcTxt);
            _pvUrlTxt = new TextBlock { FontSize = 13.5, Foreground = (Brush)FindResource("TextSecondary"), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
            infoPanel.Children.Add(metaRow); infoPanel.Children.Add(_pvUrlTxt);
            // 图片详情卡片：浅灰底圆角，与上方时间/来源/地址区分；每项一行（标签列+值列），有数据才显示对应行
            _pvDetailHost = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(241, 245, 249)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 8, 0, 0),
                Visibility = Visibility.Collapsed
            };
            _pvDetailGrid = new Grid();
            _pvDetailGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _pvDetailGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _pvDetailHost.Child = _pvDetailGrid;
            infoPanel.Children.Add(_pvDetailHost);
            infoHost.Child = infoPanel;
            Grid.SetRow(infoHost, 1);
            root.Children.Add(infoHost);

            // 操作区：与列表行内按钮同一套现代样式（渐变圆角 + hover 加深 + 按压反馈），保证全局视觉统一
            // WrapPanel：窗口缩小时按钮自动换行，避免溢出
            var bar = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center };
            var stNeutral = BuildActionButtonStyle(Color.FromRgb(255, 255, 255), Color.FromRgb(248, 250, 252), Color.FromRgb(241, 245, 249), Color.FromRgb(226, 232, 240), Color.FromRgb(203, 213, 225), new SolidColorBrush(Color.FromRgb(30, 41, 59)), true, 14);
            var stCopyBig = BuildActionButtonStyle(Color.FromRgb(255, 255, 255), Color.FromRgb(248, 250, 252), Color.FromRgb(241, 245, 249), Color.FromRgb(226, 232, 240), Color.FromRgb(203, 213, 225), new SolidColorBrush(Color.FromRgb(30, 41, 59)), true, 14);
            var stDlBig = BuildActionButtonStyle(Color.FromRgb(34, 197, 94), Color.FromRgb(22, 163, 74), Color.FromRgb(22, 163, 74), Color.FromRgb(21, 128, 61), Color.FromRgb(21, 128, 61), Brushes.White, false, 14);
            var bPrev = new Button { Content = "◀ 上一个", Style = stNeutral, Margin = new Thickness(4, 2, 4, 2) };
            var bNext = new Button { Content = "▶ 下一个", Style = stNeutral, Margin = new Thickness(4, 2, 4, 2) };
            var bOpen = new Button { Content = "浏览器打开", Style = stNeutral, Margin = new Thickness(4, 2, 4, 2) };
            var bCopy = new Button { Content = "复制地址", Style = stCopyBig, Margin = new Thickness(4, 2, 4, 2) };
            var bDl = new Button { Content = "下载", Style = stDlBig, Margin = new Thickness(4, 2, 4, 2) };
            bar.Children.Add(bPrev); bar.Children.Add(bNext); bar.Children.Add(bOpen); bar.Children.Add(bCopy); bar.Children.Add(bDl);
            // 按钮栏背景条：白底圆角边框，与内容/信息卡片统一为卡片风格
            var barHost = new Border
            {
                Background = (Brush)FindResource("CardBg"),
                BorderBrush = (Brush)FindResource("BorderColor"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Margin = new Thickness(14, 6, 14, 14),
                Padding = new Thickness(10, 8, 10, 8)
            };
            barHost.Child = bar;
            Grid.SetRow(barHost, 2);
            root.Children.Add(barHost);

            win.Content = root;

            // ESC 关闭预览弹窗（图片查看器标准交互）
            win.PreviewKeyDown += (s2, e2) => { if (e2.Key == Key.Escape) win.Close(); };


            int nav = cur;

            // 监控 ffplay 进程：用户关闭播放窗口后，弹窗内提示"点击重新播放"，无需切换
            Action<System.Diagnostics.Process> watch = null;
            watch = (proc) =>
            {
                try
                {
                    proc.EnableRaisingEvents = true;
                    proc.Exited += (s2, e2) => win.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        // 仅当该进程仍是当前播放、且弹窗还开着时，才提示重新播放
                        if (_currentFfplay == (System.Diagnostics.Process)s2 && _previewWindow == win)
                        {
                            _currentFfplay = null;
                            hint.Content = "▶ 播放窗口已关闭，点击此处重新播放";
                            hint.Visibility = Visibility.Visible;
                        }
                    }));
                }
                catch { }
            };

            // 伪嵌入：把 ffplay 无边框窗口精确叠放到预览弹窗内容区位置并置于弹窗之上（不 SetParent——
            // SDL 窗口 SetParent 后画面会冻结，叠放方式保持 ffplay 正常渲染、画面动态）
            Action<IntPtr> placeFfplay = (ffHwnd) =>
            {
                try
                {
                    var pt = mediaHost.PointToScreen(new System.Windows.Point(0, 0));
                    int w = Math.Max(50, (int)mediaHost.ActualWidth);
                    int h = Math.Max(50, (int)mediaHost.ActualHeight);
                    IntPtr winH = new System.Windows.Interop.WindowInteropHelper(win).Handle;
                    SetWindowPos(ffHwnd, winH, (int)pt.X, (int)pt.Y, w, h, 0x0040 | 0x0010); // SWP_SHOWWINDOW | SWP_NOACTIVATE
                    _embeddedHwnd = ffHwnd;
                }
                catch { }
            };

            // 启动 ffplay 并（webm/ogg/mov）叠放嵌入弹窗内容区；拿不到窗口句柄/定位失败时回退为外置播放提示
            Action<System.Diagnostics.Process, bool> startEmbedded = (proc, doEmbed) =>
            {
                if (!doEmbed) { watch(proc); return; }
                img.Visibility = Visibility.Collapsed;
                mediaHost.Visibility = Visibility.Visible;
                System.Threading.Tasks.Task.Run(() =>
                {
                    IntPtr hwnd = IntPtr.Zero;
                    for (int i = 0; i < 60; i++) // 最多 6s 等 ffplay 窗口创建
                    {
                        if (proc.HasExited) return;
                        try { proc.Refresh(); } catch { return; }
                        if (proc.MainWindowHandle != IntPtr.Zero) { hwnd = proc.MainWindowHandle; break; }
                        System.Threading.Thread.Sleep(100);
                    }
                    if (hwnd == IntPtr.Zero) return; // 拿不到句柄：保持外置播放（无边框窗口）
                    IntPtr target = hwnd;
                    win.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try { placeFfplay(target); }
                        catch
                        {
                            // 定位失败：恢复提示，告知正在外置窗口播放
                            hint.Content = "▶ 正在播放中（外置窗口，循环播放；关闭后点击此处可重新播放）";
                            hint.Visibility = Visibility.Visible;
                        }
                    }));
                });
                watch(proc);
            };

            // 弹窗移动/缩放时同步叠放的 ffplay 窗口位置与尺寸
            win.LocationChanged += (s2, e2) => { if (_embeddedHwnd != IntPtr.Zero) win.Dispatcher.BeginInvoke(new Action(() => placeFfplay(_embeddedHwnd))); };
            win.SizeChanged += (s2, e2) => { if (_embeddedHwnd != IntPtr.Zero) win.Dispatcher.BeginInvoke(new Action(() => placeFfplay(_embeddedHwnd))); };

            // 图片初始展示：等图片完全解码后完整适配查看区（大图自动缩小到全部可见，小图原始尺寸居中）。
            // 打开与切换上下页均走此处；用户可放大到 100% 看细节，超出后拖动平移。
            // 弹窗刚打开时 svImg 可能尚未完成布局（ActualWidth=0），此时延迟到下一帧重试。
            int fitRetry = 0;
            Action<BitmapImage> fitImage = null;
            fitImage = (bmp) =>
            {
                // 用 Width/Height（WPF 按图片 DPI 换算后的 DIP 尺寸）而非 PixelWidth/Height（物理像素）：
                // 图片 DPI≠96 时物理像素与渲染尺寸不一致，会导致居中/缩放计算全部错位
                curPw = bmp.Width; curPh = bmp.Height;
                if (curPw <= 0 || curPh <= 0) { curPw = bmp.PixelWidth; curPh = bmp.PixelHeight; }
                double vw = svImg.ActualWidth, vh = svImg.ActualHeight;
                if ((vw <= 1 || vh <= 1) && fitRetry < 60)
                {
                    fitRetry++;
                    win.Dispatcher.BeginInvoke(new Action(() => fitImage(bmp)), System.Windows.Threading.DispatcherPriority.Loaded);
                    return;
                }
                FitAndCenter();
                // 视口完全就绪后再居中一次（首次打开时 ViewportWidth 可能尚未准确）
                win.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (img.Source == null) return;
                    if (_isFit) FitAndCenter();
                }), System.Windows.Threading.DispatcherPriority.Loaded);
            };
            // 弹窗尺寸变化：适配状态跟随窗口重排居中；手动放大状态由 ScrollViewer 自动限界
            win.SizeChanged += (s2, e2) =>
            {
                if (img.Source == null) return;
                if (_isFit) FitAndCenter();
            };
            // 滚轮缩放：围绕鼠标指针位置（标准看图软件交互）；适配状态下向下滚不缩小
            svImg.PreviewMouseWheel += (s2, e2) =>
            {
                if (img.Source == null) return;
                if (_isFit && e2.Delta <= 0) { e2.Handled = true; return; }
                double s0 = _scale;
                double s1 = Math.Max(0.05, Math.Min(10.0, s0 * (e2.Delta > 0 ? 1.15 : 1.0 / 1.15)));
                var mp = e2.GetPosition(scroller);
                ZoomAround(s1, mp.X, mp.Y);
                if (_scale <= FitScale() + 0.001) FitAndCenter(); // 缩小回完整适配
                e2.Handled = true;
            };
            // 双击：还原为"适配容器"比例（不干扰拖动）；单击拖动仅当图片放大超出查看区时才启用平移。
            // 方案：WndProc（HwndSource.AddHook）手动处理 WM_LBUTTONDOWN/MOUSEMOVE/UP——
            // 本环境 WPF 的 MouseDown 路由在弹窗图片区失效（隧道/冒泡/任意宿主均不触发，仅 Button Click 正常），
            // Win32 消息必然到达窗口；坐标走 ClientToScreen + PointFromScreen 换算到 svImg 相对坐标，绕过 WPF 命中测试。
            // 双击用手动检测（两次按下间隔<350ms 且位移<8px），不依赖 WPF ClickCount。
            DateTime _lastDownTime = DateTime.MinValue;
            System.Windows.Point _lastDownPos = new System.Windows.Point();
            bool _lbtn = false;
            HwndSourceHook srcHook = (IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                const int WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_MOUSEMOVE = 0x0200;
                if (msg == WM_LBUTTONDOWN || msg == WM_LBUTTONUP || msg == WM_MOUSEMOVE)
                {
                    int cx = (short)(lParam.ToInt32() & 0xFFFF);
                    int cy = (short)((lParam.ToInt32() >> 16) & 0xFFFF);
                    var wpt = new POINT { X = cx, Y = cy };
                    ClientToScreen(h, ref wpt);
                    var rel = svImg.PointFromScreen(new System.Windows.Point(wpt.X, wpt.Y));
                    bool inImg = rel.X >= 0 && rel.Y >= 0 && rel.X <= svImg.ActualWidth && rel.Y <= svImg.ActualHeight;
                    // 点击落在左下背景按钮 / 右下缩放条上时不当作图片区交互（避免连点缩放被误判成双击还原）
                    Func<FrameworkElement, bool> hitUi = (el) =>
                    {
                        if (el == null || el.Visibility != Visibility.Visible) return false;
                        var tl = el.PointToScreen(new System.Windows.Point(0, 0));
                        return wpt.X >= tl.X && wpt.Y >= tl.Y && wpt.X <= tl.X + el.ActualWidth && wpt.Y <= tl.Y + el.ActualHeight;
                    };
                    if (msg == WM_LBUTTONDOWN)
                    {
                        _lbtn = true;
                        // 手动命中 bBg（左下）与 zoomBar（右下）：不依赖 PointToScreen / WPF Button Click（本环境 svImg 内按钮命中不可靠）
                        double bbW = Math.Max(bBg.ActualWidth, 1), bbH = Math.Max(bBg.ActualHeight, 1);
                        double bbTop = Math.Max(svImg.ActualHeight - bbH - 10, 0);
                        double zbW = Math.Max(zoomBar.ActualWidth, 1), zbH = Math.Max(zoomBar.ActualHeight, 1);
                        double zbLeft = Math.Max(svImg.ActualWidth - zbW - 8, 0), zbTop = Math.Max(svImg.ActualHeight - zbH - 8, 0);
                        if (rel.X >= 10 && rel.X <= 10 + bbW && rel.Y >= bbTop && rel.Y <= bbTop + bbH)
                        {
                            bgIdx = (bgIdx + 1) % previewBgs.Count; content.Background = previewBgs[bgIdx]; bBg.Content = bgNames[bgIdx];
                            handled = true; return IntPtr.Zero;
                        }
                        if (rel.X >= zbLeft && rel.X <= zbLeft + zbW && rel.Y >= zbTop && rel.Y <= zbTop + zbH)
                        {
                            double lx = rel.X - zbLeft, acc = 0;
                            bool matchedBar = false;
                            foreach (var child in zoomPanel.Children)
                            {
                                var fe = child as FrameworkElement;
                                if (fe == null || fe.Visibility != Visibility.Visible) continue;
                                double cw = Math.Max(fe.ActualWidth, 1);
                                if (lx >= acc && lx <= acc + cw)
                                {
                                    matchedBar = true;
                                    if (fe == btnZoomOut && img.Source != null) zoomStep(1.0 / 1.15);
                                    else if (fe == btnZoomIn && img.Source != null) zoomStep(1.15);
                                    else if (fe == btnZoom100 && img.Source != null) zoomFitNow();
                                    else if (fe == zoomTxt) { zoomTxt.Focus(); zoomTxt.SelectAll(); }
                                    handled = true; return IntPtr.Zero;
                                }
                                acc += cw;
                            }
                            // zoomBar 内未命中具体控件（padding/缝隙）：也不进入图片区交互
                            if (!matchedBar) { handled = true; return IntPtr.Zero; }
                        }
                        if (inImg && !hitUi(bBg) && !hitUi(zoomBar))
                        {
                            bool isDouble = (DateTime.Now - _lastDownTime).TotalMilliseconds < 350
                                && Math.Abs(rel.X - _lastDownPos.X) < 8
                                && Math.Abs(rel.Y - _lastDownPos.Y) < 8;
                            _lastDownTime = DateTime.Now;
                            _lastDownPos = rel;
                            if (isDouble)
                            {
                                // 双击复原：回到默认完整展示并居中（与打开/切换时一致）
                                zoomFitNow();
                                return IntPtr.Zero;
                            }
                            if (img.Source != null && (imgHost.Width > scroller.ViewportWidth + 0.5 || imgHost.Height > scroller.ViewportHeight + 0.5))
                            {
                                _dragCandidate = true;
                                _imgDragStart = _imgDragLast = rel;
                            }
                        }
                    }
                    else if (msg == WM_LBUTTONUP)
                    {
                        _lbtn = false;
                        _imgDrag = false; _dragCandidate = false;
                        win.ReleaseMouseCapture();
                    }
                    else if (msg == WM_MOUSEMOVE && _lbtn && _dragCandidate)
                    {
                        // 移动超过阈值才真正进入拖动（避免单击/双击被误判成拖动）
                        if (!_imgDrag && (Math.Abs(rel.X - _imgDragStart.X) > 5 || Math.Abs(rel.Y - _imgDragStart.Y) > 5))
                            _imgDrag = true;
                        if (_imgDrag)
                        {
                            double dx = rel.X - _imgDragLast.X;
                            double dy = rel.Y - _imgDragLast.Y;
                            scroller.ScrollToHorizontalOffset(scroller.HorizontalOffset - dx);
                            scroller.ScrollToVerticalOffset(scroller.VerticalOffset - dy);
                            _imgDragLast = rel;
                        }
                    }
                }
                return IntPtr.Zero;
            };
            bool _hookInstalled = false;
            Action installHook = () =>
            {
                if (_hookInstalled) return;
                _hookInstalled = true;
                IntPtr hwnd = new WindowInteropHelper(win).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    var hsrc = HwndSource.FromHwnd(hwnd);
                    if (hsrc != null) hsrc.AddHook(srcHook);
                }
            };
            win.Loaded += (s2, e2) => installHook();
            win.ContentRendered += (s2, e2) => installHook();

            Action render = null;
            render = () =>
            {
                // 清理上一次内嵌播放的媒体
                if (_currentMedia != null)
                {
                    try { _currentMedia.Stop(); _currentMedia.Source = null; content.Children.Remove(_currentMedia); } catch { }
                    _currentMedia = null;
                }
                // 切换项时关闭上一个 ffplay 播放进程，避免窗口堆积。
                // 先置 null 再 Kill：防止 Kill 触发的 Exited 回调误判成"播放被关闭"而弹出重新播放提示
                if (_currentFfplay != null)
                {
                    var old = _currentFfplay;
                    _currentFfplay = null;
                    try { if (!old.HasExited) old.Kill(); } catch { }
                }
                _embeddedHwnd = IntPtr.Zero;
                var item = items[nav];
                bool isMedia = (item.Type == "视频" || item.Type == "音频");
                win.Title = "预览  " + (item.Time ?? "") + "  " + (item.Source ?? "") + "  (" + (nav + 1) + "/" + items.Count + ")";
                var tbConv = new TypeBrushConverter();
                var tBrush = (Brush)tbConv.Convert(item.Type ?? "", typeof(string), null, null);
                _pvTypeDot.Foreground = tBrush;
                _pvTypeTxt.Foreground = tBrush;
                _pvTypeTxt.Text = item.Type ?? "其他";
                _pvTimeTxt.Text = item.Time ?? "";
                _pvSrcTxt.Text = item.Source ?? "";
                _pvUrlTxt.Text = item.Url;
                _pvUrlTxt.ToolTip = item.Url;
                // 切到新项：图片保留旧图直到新图加载成功直接替换（消除清空画面造成的翻页闪烁）；
                // 视频需停旧播放器换源，必须立即清场
                if (isMedia)
                {
                    img.Source = null;
                    _scale = 1.0; _isFit = true;
                    imgHost.Width = 1; imgHost.Height = 1;
                    scroller.ScrollToHorizontalOffset(0); scroller.ScrollToVerticalOffset(0);
                    hint.Content = "加载中…";
                    hint.Visibility = Visibility.Visible;
                }
                else
                {
                    hint.Visibility = Visibility.Collapsed; // 图片加载期间旧图保持显示，无"加载中"闪烁
                }
                fitRetry = 0;
                _imgDrag = false;
                mediaHost.Visibility = Visibility.Collapsed;
                if (isMedia) _pvDetailHost.Visibility = Visibility.Collapsed; // 视频无详情卡片
                // 图片切换：保留旧详情卡片显示（加载成功后再更新为新图详情）——
                // 避免详情卡先隐藏再显示导致信息区高度变化、图片查看区重新布局造成的翻页闪烁
                img.Visibility = isMedia ? Visibility.Collapsed : Visibility.Visible;

                string url = item.Url;
                if (isMedia)
                {
                    // 优先内嵌播放（MediaElement/WMF：支持 mp4/m4a/mp3/wav 等）；
                    // webm/ogg/mov 等 WMF 不支持的格式 → 调用同目录 ffplay.exe（隐藏黑窗、限制窗口尺寸、非全屏）
                    string ffplay = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffplay.exe");
                    string lower = url.ToLowerInvariant();
                    bool wmfUnsupported = lower.EndsWith(".webm") || lower.EndsWith(".ogg") || lower.EndsWith(".mov");
                    bool noFfplay = !System.IO.File.Exists(ffplay);

                    if (wmfUnsupported)
                    {
                        if (noFfplay)
                        {
                            hint.Content = "该格式（webm）系统无法内嵌播放，且未找到同目录 ffplay.exe。点「🌐 浏览器打开」或「⬇ 下载」查看。";
                        }
                        else
                        {
                            try
                            {
                                // webm/ogg/mov：外置 ffplay 窗口循环播放（保持原始行为）
                                var pr = LaunchFfplay(ffplay, url, item.Source);
                                _currentFfplay = pr;
                                watch(pr);
                                hint.Content = "▶ 正在播放中（循环播放；关闭 ffplay 窗口后，点击此处可重新播放）";
                                hint.Visibility = Visibility.Visible;
                            }
                            catch
                            {
                                hint.Content = "启动 ffplay 失败。点「🌐 浏览器打开」或「⬇ 下载」查看。";
                            }
                        }
                    }
                    else
                    {
                        // MediaElement 内嵌播放
                        var me = new MediaElement
                        {
                            Stretch = Stretch.Uniform,
                            LoadedBehavior = MediaState.Manual,
                            UnloadedBehavior = MediaState.Stop,
                            Margin = new Thickness(12)
                        };
                        me.MediaOpened += (s2, e2) =>
                        {
                            win.Dispatcher.BeginInvoke(new Action(() =>
                            {
                                hint.Visibility = Visibility.Collapsed;
                                try { me.Play(); } catch { }
                            }));
                        };
                        me.MediaFailed += (s2, e2) =>
                        {
                            win.Dispatcher.BeginInvoke(new Action(() =>
                            {
                                try { content.Children.Remove(me); } catch { }
                                if (_currentMedia == me) _currentMedia = null;
                                if (noFfplay)
                                {
                                    hint.Content = "内嵌播放失败（可能格式或网络问题），且未找到 ffplay.exe。";
                                    hint.Visibility = Visibility.Visible;
                                }
                                else
                                {
                                    try
                                    {
                                        var pr = LaunchFfplay(ffplay, url, item.Source);
                                        _currentFfplay = pr;
                                        watch(pr);
                                        hint.Content = "▶ 正在播放中（内嵌失败已改用 ffplay，循环播放；关窗后点击此处可重新播放）";
                                        hint.Visibility = Visibility.Visible;
                                    }
                                    catch
                                    {
                                        hint.Content = "内嵌播放失败，且启动 ffplay 失败。点「🌐 浏览器打开」或「⬇ 下载」查看。";
                                        hint.Visibility = Visibility.Visible;
                                    }
                                }
                            }));
                        };
                        content.Children.Add(me);
                        _currentMedia = me;
                        try { me.Source = new Uri(url); }
                        catch
                        {
                            content.Children.Remove(me); _currentMedia = null;
                            hint.Content = "地址无效，无法播放。";
                        }
                    }
                }
                else
                {
                    int curNav = nav;
                    bool isWebp = url.ToLowerInvariant().EndsWith(".webp");
                    Task.Run(() =>
                    {
                        try
                        {
                            using (var wc = new WebClient())
                            {
                                wc.Headers[HttpRequestHeader.UserAgent] = "Mozilla/5.0";
                                byte[] data = wc.DownloadData(url);
                                // 动画 webp 检测：RIFF..WEBP 容器 + VP8X chunk + ANIM 标志(0x02) → 动态图，
                                // 用 ffplay 伪嵌入到弹窗内容区循环播放；普通静态图仍走 Image 显示
                                bool animWebp = isWebp && data.Length > 24
                                    && data[0] == (byte)'R' && data[1] == (byte)'I' && data[2] == (byte)'F' && data[3] == (byte)'F'
                                    && data[8] == (byte)'W' && data[9] == (byte)'E' && data[10] == (byte)'B' && data[11] == (byte)'P'
                                    && data[12] == (byte)'V' && data[13] == (byte)'P' && data[14] == (byte)'8' && data[15] == (byte)'X'
                                    && (data[20] & 0x02) != 0;
                                if (animWebp)
                                {
                                    win.Dispatcher.BeginInvoke(new Action(() =>
                                    {
                                        if (curNav != nav) return;
                                        string ffplay = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffplay.exe");
                                        if (System.IO.File.Exists(ffplay))
                                        {
                                            try
                                            {
                                                img.Visibility = Visibility.Collapsed;
                                                _pvDetailHost.Visibility = Visibility.Collapsed; // 动画图走 ffplay，无静态画面，详情卡片隐藏
                                                var pr = LaunchFfplay(ffplay, url, item.Source, true);
                                                _currentFfplay = pr;
                                                startEmbedded(pr, true);
                                                hint.Content = "▶ 动画图片，正在播放中（循环播放）";
                                                hint.Visibility = Visibility.Collapsed;
                                            }
                                            catch
                                            {
                                                mediaHost.Visibility = Visibility.Collapsed;
                                                hint.Content = "启动 ffplay 播放动画图片失败。";
                                                hint.Visibility = Visibility.Visible;
                                            }
                                        }
                                        else
                                        {
                                            // 无 ffplay：退回首帧显示
                                            using (var ms = new MemoryStream(data))
                                            {
                                                var bmp = new BitmapImage();
                                                bmp.BeginInit();
                                                bmp.CacheOption = BitmapCacheOption.OnLoad;
                                                bmp.StreamSource = ms;
                                                bmp.EndInit();
                                                bmp.Freeze();
                                                img.Source = bmp; img.ToolTip = url; hint.Visibility = Visibility.Collapsed;
                                                fitImage(bmp);
                                                // 动画图退回首帧：无静态完整画面，详情行隐藏
                                            }
                                        }
                                    }));
                                }
                                else
                                {
                                    using (var ms = new MemoryStream(data))
                                    {
                                        var bmp = new BitmapImage();
                                        bmp.BeginInit();
                                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                                        bmp.StreamSource = ms;
                                        bmp.EndInit();
                                        bmp.Freeze();
                                        win.Dispatcher.BeginInvoke(new Action(() =>
                                        {
                                            if (curNav == nav)
                                            {
                                                img.Source = bmp; img.ToolTip = url; hint.Visibility = Visibility.Collapsed; fitImage(bmp);
                                                // 图片详情（链接下方独立卡片）：每项一行"标签 值"；分辨率·格式·大小固定，色彩/DPI/EXIF 有数据才显示
                                                var pairs = new List<KeyValuePair<string, string>>();
                                                pairs.Add(new KeyValuePair<string, string>("分辨率", bmp.PixelWidth + " × " + bmp.PixelHeight));
                                                pairs.Add(new KeyValuePair<string, string>("格式", System.IO.Path.GetExtension(url).TrimStart('.').ToUpperInvariant()));
                                                pairs.Add(new KeyValuePair<string, string>("大小", data.Length >= 1048576 ? (data.Length / 1048576.0).ToString("0.0") + " MB" : (data.Length / 1024.0).ToString("0") + " KB"));
                                                string fmtName = bmp.Format.ToString().ToLowerInvariant();
                                                bool hasAlpha = fmtName.Contains("bgra") || fmtName.Contains("pbgra") || fmtName.Contains("rgba") || fmtName.Contains("argb") || fmtName.Contains("prgba");
                                                pairs.Add(new KeyValuePair<string, string>("色彩", bmp.Format.BitsPerPixel + "位" + (hasAlpha ? " · 含透明通道" : "")));
                                                if (bmp.DpiX > 0 && bmp.DpiY > 0)
                                                    pairs.Add(new KeyValuePair<string, string>("DPI", (int)Math.Round(bmp.DpiX) + " × " + (int)Math.Round(bmp.DpiY)));
                                                // EXIF 元数据：拍摄时间 / 相机厂商 / 相机型号（多数网络图无 EXIF，无则跳过）
                                                try
                                                {
                                                    BitmapMetadata md = bmp.Metadata as BitmapMetadata;
                                                    if (md != null)
                                                    {
                                                        string dt = md.GetQuery("System.Photo.DateTaken") != null ? md.GetQuery("System.Photo.DateTaken").ToString() : "";
                                                        string manu = md.GetQuery("System.Photo.CameraManufacturer") != null ? md.GetQuery("System.Photo.CameraManufacturer").ToString() : "";
                                                        string model = md.GetQuery("System.Photo.CameraModel") != null ? md.GetQuery("System.Photo.CameraModel").ToString() : "";
                                                        string exif = (dt + " " + manu + " " + model).Trim();
                                                        if (exif.Length > 0) pairs.Add(new KeyValuePair<string, string>("EXIF", exif));
                                                    }
                                                }
                                                catch { }
                                                _pvDetailGrid.Children.Clear();
                                                _pvDetailGrid.RowDefinitions.Clear();
                                                var textSec = (Brush)FindResource("TextSecondary");
                                                var textPri = (Brush)FindResource("TextPrimary");
                                                for (int i = 0; i < pairs.Count; i++)
                                                {
                                                    _pvDetailGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                                                    var lb = new TextBlock { Text = pairs[i].Key, FontSize = 13.5, Foreground = textSec, TextAlignment = TextAlignment.Right, Margin = new Thickness(0, 0, 18, 5), VerticalAlignment = VerticalAlignment.Top };
                                                    var vb = new TextBlock { Text = pairs[i].Value, FontSize = 13.5, Foreground = textPri, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 5), VerticalAlignment = VerticalAlignment.Top };
                                                    Grid.SetRow(lb, i); Grid.SetColumn(lb, 0);
                                                    Grid.SetRow(vb, i); Grid.SetColumn(vb, 1);
                                                    _pvDetailGrid.Children.Add(lb);
                                                    _pvDetailGrid.Children.Add(vb);
                                                }
                                                _pvDetailHost.Visibility = Visibility.Visible;
                                            }
                                        }));
                                    }
                                }
                            }
                        }
                        catch
                        {
                            win.Dispatcher.BeginInvoke(new Action(() =>
                            {
                                if (curNav != nav) return;
                                img.Source = null;
                                _scale = 1.0; _isFit = true;
                                imgHost.Width = 1; imgHost.Height = 1;
                                scroller.ScrollToHorizontalOffset(0); scroller.ScrollToVerticalOffset(0);
                                hint.Content = "资源加载失败：地址可能已失效。可点「🌐 浏览器打开」或「⬇ 下载」查看。";
                                hint.Visibility = Visibility.Visible;
                                _pvDetailHost.Visibility = Visibility.Collapsed;
                            }));
                        }
                    });
                }
            };

            // 点击提示按钮：播放/重新播放当前项（遵循全局只有一个 ffplay：已有播放进程则先关闭再启动）
            hint.Click += (s, e) =>
            {
                var curItem = items[nav];
                string lowerUrl = (curItem.Url ?? "").ToLowerInvariant();
                // 视频/音频，或动画 webp（加载失败/播放退出后提示重播）都可走 ffplay
                bool isMedia = (curItem.Type == "视频" || curItem.Type == "音频") || lowerUrl.EndsWith(".webp");
                if (!isMedia) return;
                string ffp = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffplay.exe");
                if (!System.IO.File.Exists(ffp)) { hint.Content = "未找到同目录 ffplay.exe，无法播放。"; return; }
                // 保证唯一播放实例：先关掉已有的 ffplay 再启动新的
                if (_currentFfplay != null)
                {
                    var old = _currentFfplay;
                    _currentFfplay = null;
                    try { if (!old.HasExited) old.Kill(); } catch { }
                }
                try
                {
                    // webp 动画/静态图、webm/ogg/mov 都交给 ffplay 循环播放；
                    // 动画 webp 用伪嵌入叠放内容区，其余外置窗口
                    bool embed = lowerUrl.EndsWith(".webp");
                    if (embed) img.Visibility = Visibility.Collapsed;
                    var pr = LaunchFfplay(ffp, curItem.Url, curItem.Source, embed);
                    _currentFfplay = pr;
                    startEmbedded(pr, embed);
                    hint.Content = embed ? "▶ 正在播放中（循环播放）" : "▶ 正在播放中（循环播放；关闭 ffplay 窗口后，点击此处可重新播放）";
                    hint.Visibility = embed ? Visibility.Collapsed : Visibility.Visible;
                }
                catch
                {
                    hint.Content = "启动 ffplay 失败。点「🌐 浏览器打开」或「⬇ 下载」查看。";
                }
            };

            bPrev.Click += (s, e) => { nav = (nav - 1 + items.Count) % items.Count; render(); };
            bNext.Click += (s, e) => { nav = (nav + 1) % items.Count; render(); };
            bOpen.Click += (s, e) => { var c = items[nav]; if (!string.IsNullOrEmpty(c.Url)) { try { System.Diagnostics.Process.Start(c.Url); } catch { SetStatus("无法打开浏览器：" + c.Url); } } };
            bCopy.Click += (s, e) => { var c = items[nav]; if (!string.IsNullOrEmpty(c.Url)) { Clipboard.SetText(c.Url); SetStatus("已复制地址：" + c.Url); } };
            bDl.Click += (s, e) => { DownloadOne(items[nav]); };

            _previewWindow = win;
            render();
            // 弹窗关闭：停止当前播放（ffplay/MediaElement 一并清理，不留残留窗口），并重新激活主窗口
            win.Closed += (s, e) =>
            {
                if (_currentMedia != null)
                {
                    try { _currentMedia.Stop(); _currentMedia.Source = null; } catch { }
                    _currentMedia = null;
                }
                if (_currentFfplay != null)
                {
                    var old = _currentFfplay;
                    _currentFfplay = null;
                    try { if (!old.HasExited) old.Kill(); } catch { }
                }
                _embeddedHwnd = IntPtr.Zero;
                _previewWindow = null;
                try { Activate(); } catch { }
            };
            win.Show();
        }

        List<LinkItem> SelectedItems()
        {
            var list = new List<LinkItem>();
            foreach (var o in _grid.SelectedItems)
                if (o is LinkItem && !string.IsNullOrEmpty(((LinkItem)o).Url)) list.Add((LinkItem)o);
            return list;
        }

        void Download(bool all)
        {
            // all = 下载当前筛选后列表（用户看到的，受限并发批量下载）；false = 下载选中项
            var targets = all
                ? CollectionViewSource.GetDefaultView(_items).Cast<object>().Where(o => o != CollectionView.NewItemPlaceholder).Cast<LinkItem>().Where(i => !string.IsNullOrEmpty(i.Url)).ToList()
                : SelectedItems();
            if (targets.Count == 0) { SetStatus("没有可下载的地址。"); return; }
            if (string.IsNullOrEmpty(_downloadDir))
            {
                var d = new System.Windows.Forms.FolderBrowserDialog();
                d.Description = "选择下载保存文件夹";
                if (d.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                _downloadDir = d.SelectedPath;
            }
            // 批量下载前确认：告知数量与保存位置，避免误触几百条并发下载
            if (all)
            {
                var r = System.Windows.MessageBox.Show(this, "将下载当前列表共 " + targets.Count + " 个文件到：\n" + _downloadDir + "\n\n是否继续？", "下载全部", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (r != MessageBoxResult.Yes) return;
            }
            SetStatus((all ? "开始下载当前列表 " : "开始下载选中 ") + targets.Count + " 个文件 → " + _downloadDir);
            int done = 0, fail = 0, total = targets.Count;
            foreach (var rec in targets)
            {
                string dest = System.IO.Path.Combine(_downloadDir, GetFileName(rec.Url));
                rec.Status = "排队中";
                Task.Run(() =>
                {
                    _dlSem.Wait(); // 受限并发：最多 4 个同时下载，完成一个放行下一个
                    rec.Status = "下载中";
                    try
                    {
                        using (var wc = new WebClient()) { wc.Headers[HttpRequestHeader.UserAgent] = "Mozilla/5.0"; wc.DownloadFile(rec.Url, dest); }
                        Dispatcher.BeginInvoke(new Action(() => { rec.Status = "✓ 已下载"; Interlocked.Increment(ref done); SetProgress(done, fail, total); }));
                    }
                    catch (Exception)
                    {
                        Dispatcher.BeginInvoke(new Action(() => { rec.Status = "✗ 失败"; Interlocked.Increment(ref fail); SetProgress(done, fail, total); }));
                    }
                    finally { _dlSem.Release(); }
                });
            }
        }

        void SetProgress(int done, int fail, int total)
        {
            SetStatus("下载进度：" + (done + fail) + "/" + total + "（成功 " + done + "，失败 " + fail + "）");
        }

        string GetFileName(string url)
        {
            var m = Regex.Match(url, @"/([^/]+)\.[a-zA-Z0-9]+$");
            if (m.Success)
            {
                var ext = Regex.Match(url, @"\.([a-zA-Z0-9]+)$").Groups[1].Value.ToLower();
                return m.Groups[1].Value + "." + ext;
            }
            return "file_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        void CopyAll()
        {
            var sb = new StringBuilder();
            foreach (var it in _items) if (!string.IsNullOrEmpty(it.Url)) sb.AppendLine((it.Time ?? "") + "\t" + it.Type + "\t" + it.Source + "\t" + it.Url);
            if (sb.Length > 0) { Clipboard.SetText(sb.ToString()); SetStatus("已复制全部地址到剪贴板。"); }
        }

        void SaveHistory()
        {
            try
            {
                var sb = new StringBuilder();
                foreach (var r in _items)
                    sb.AppendLine((r.Time ?? "") + "\t" + r.Type + "\t" + r.Source + "\t" + r.Url + "\t" + (r.Note ?? "") + "\t" + (r.Batch ?? "历史记录") + "\t" + (r.ImportTime ?? ""));
                File.WriteAllText(_historyPath, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }

        // 图标以 base64 内嵌于代码：编译时无需外部 app.ico 文件，解码 PNG 生成窗口图标（单文件自足）
        const string ICON_BASE64 = "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";
        System.Drawing.Icon IconFromBase64()
        {
            try
            {
                byte[] data = Convert.FromBase64String(ICON_BASE64);
                using (var ms = new System.IO.MemoryStream(data))
                using (var bmp = new System.Drawing.Bitmap(ms))
                {
                    var h = bmp.GetHicon();
                    using (var ic = System.Drawing.Icon.FromHandle(h))
                        return (System.Drawing.Icon)ic.Clone();
                }
            }
            catch { return null; }
        }

        void LoadHistory()
        {
            try
            {
                if (!File.Exists(_historyPath)) return;
                foreach (var ln in File.ReadAllLines(_historyPath, Encoding.UTF8))
                {
                    if (ln.Length == 0) continue;
                    var p = ln.Split('\t');
                    if (p.Length < 4) continue;
                    var it = new LinkItem();
                    it.Time = p[0]; it.Type = p[1]; it.Source = p[2]; it.Url = p[3]; it.Note = p.Length > 4 ? p[4] : "";
                    it.Batch = p.Length > 5 ? p[5] : "历史记录"; // 旧格式（5列）自动归入"历史记录"批次
                    it.ImportTime = p.Length > 6 ? p[6] : ""; // 旧格式无导入时间
                    if (it.Url.Length == 0) continue; // 只记录有效记录：无地址的辅助行（响应头记录等）不进入列表
                    if (!_seen.Add(it.Url)) continue;
                    _items.Add(it);
                }
                UpdateCount();
                // 按时间降序排列（最新在前）：Time 为 ISO 格式字符串，字典序即时间序；视图级排序，后续新拖入条目自动排入
                var view = CollectionViewSource.GetDefaultView(_items);
                view.SortDescriptions.Clear();
                view.SortDescriptions.Add(new SortDescription("Time", ListSortDirection.Descending));
                ApplyFilter();
                SetStatus("已加载历史 " + _items.Count + " 条，可继续拖入新文件增量提取。");
            }
            catch { }
        }

        void UpdateCount(ICollectionView view = null)
        {
            // DataGrid.Items.Count 直接反映筛选后的视图行数，避免 CollectionView.Refresh/Count 的竞态与枚举差异
            int shown = _grid.Items.Count;
            // "共"只统计有有效地址链接的记录（无地址的辅助记录不计入），保证与"当前筛选显示"语义一致
            int valid = _items.Count(i => i.Url.Length > 0);
            _countText.Text = "共 " + valid + " 条有效记录，当前筛选显示 " + shown + " 条";
        }

        void SetStatus(string s)
        {
            _statusText.Text = s;
        }

        // ---- 在线更新（GitHub Releases）：检查 / 提示 / 下载 / 替换重启 ----

        // 检查更新：manual=true 手动点击（结果都提示）；manual=false 启动后台自动（仅发现新版本才弹窗）
        void CheckUpdate(bool manual)
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                string latest = "", note = "", exeUrl = "";
                bool ok = false; string err = "未知错误";
                try
                {
                    if (_updateRepo == UpdateRepoDefault)
                    {
                        err = "更新源未配置（程序内置占位）。\n待作者发布带更新源的版本后即可检查更新。";
                    }
                    else
                    {
                        using (var wc = new System.Net.WebClient())
                        {
                            wc.Headers[System.Net.HttpRequestHeader.UserAgent] = "PickLight-Updater";
                            wc.Headers[System.Net.HttpRequestHeader.Accept] = "application/vnd.github+json";
                            string json = wc.DownloadString("https://api.github.com/repos/" + _updateRepo + "/releases/latest");
                            var mTag = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
                            var mBody = Regex.Match(json, "\"body\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
                            var mExe = Regex.Match(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+\\.exe)\"");
                            if (mTag.Success) latest = mTag.Groups[1].Value;
                            if (mBody.Success) note = DecodeJsonStr(mBody.Groups[1].Value);
                            if (mExe.Success) exeUrl = mExe.Groups[1].Value;
                            ok = true;
                        }
                    }
                }
                catch (Exception ex) { err = ex.Message; ok = false; }

                string tag = latest, desc = note, url = exeUrl;
                bool fetched = ok; string errMsg = err;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!fetched)
                    {
                        if (manual)
                            System.Windows.MessageBox.Show(this, "检查更新失败：" + errMsg, "检查更新", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }
                    Version curV = null, newV = null;
                    Version.TryParse(AppVersion.TrimStart('v', 'V'), out curV);
                    Version.TryParse(tag.TrimStart('v', 'V'), out newV);
                    bool hasNew = curV != null && newV != null && newV > curV;
                    if (!hasNew)
                    {
                        if (manual) System.Windows.MessageBox.Show(this, "当前已是最新版本 v" + AppVersion + "。", "检查更新", MessageBoxButton.OK, MessageBoxImage.Information);
                        else SetStatus("已是最新版本 v" + AppVersion + "。");
                        return;
                    }
                    if (url.Length == 0)
                    {
                        System.Windows.MessageBox.Show(this, "发现新版本 v" + tag + "，但该 Release 未附带 .exe 安装包，请到 GitHub Releases 页面手动下载。", "发现新版本", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }
                    ShowUpdateDialog(tag, desc, url);
                }));
            });
        }

        // JSON 字符串解码：把 \\r\\n、\\"、\\\\ 等转义还原为真实字符（GitHub body 是 JSON 字符串）
        static string DecodeJsonStr(string s)
        {
            if (s == null) return "";
            var sb = new System.Text.StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\\' && i + 1 < s.Length)
                {
                    char n = s[++i];
                    if (n == 'n') sb.Append('\n');
                    else if (n == 'r') { } // 忽略 \r（GitHub 说明换行为 \n）
                    else if (n == 't') sb.Append('\t');
                    else if (n == '"') sb.Append('"');
                    else if (n == '\\') sb.Append('\\');
                    else sb.Append(n);
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        // 更新确认弹窗：版本信息 + 更新说明 + 自动更新开关 + 立即更新
        void ShowUpdateDialog(string latestTag, string note, string exeUrl)
        {
            var dlg = new Window
            {
                Title = "发现新版本",
                Width = 540,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Background = (Brush)FindResource("AppBackground"),
                FontFamily = FontFamily,
                FontSize = 13.5
            };
            dlg.PreviewKeyDown += (s2, e2) => { if (e2.Key == Key.Escape) dlg.Close(); }; // ESC 关闭
            var root = new StackPanel { Margin = new Thickness(20, 16, 20, 14) };
            var theme = new SolidColorBrush(Color.FromRgb(15, 118, 110));
            root.Children.Add(new TextBlock { Text = "发现新版本 v" + latestTag, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = theme, Margin = new Thickness(0, 0, 0, 6) });
            root.Children.Add(new TextBlock { Text = "当前版本 v" + AppVersion + " → 最新版本 v" + latestTag, FontSize = 13, Foreground = (Brush)FindResource("TextSecondary") });
            if (note.Length > 0)
            {
                root.Children.Add(new TextBlock { Text = "更新说明：", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("TextPrimary"), Margin = new Thickness(0, 10, 0, 4) });
                root.Children.Add(new TextBlock { Text = note, FontSize = 13, Foreground = (Brush)FindResource("TextPrimary"), TextWrapping = TextWrapping.Wrap, MaxHeight = 200 });
            }
            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
            var btnLater = MakeBtn("稍后再说", (Style)FindResource("ToolButton"), 110, 38);
            btnLater.Click += (s, e) => dlg.Close();
            var btnNow = MakeBtn("立即更新", PrimaryButtonStyle(13.0), 110, 38);
            btnNow.Click += (s, e) => DoUpdate(exeUrl, dlg);
            btnLater.Margin = new Thickness(0, 0, 8, 0);
            btnRow.Children.Add(btnLater); btnRow.Children.Add(btnNow);
            root.Children.Add(btnRow);
            dlg.Content = root;
            dlg.ShowDialog();
        }

        // 设置弹窗：自动更新开关（写入 settings.ini）+ 检查更新 + 版本/更新源状态
        void ShowSettingsDialog()
        {
            var dlg = new Window
            {
                Title = "设置",
                Width = 500,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Background = (Brush)FindResource("AppBackground"),
                FontFamily = FontFamily,
                FontSize = 13.5
            };
            dlg.PreviewKeyDown += (s2, e2) => { if (e2.Key == Key.Escape) dlg.Close(); }; // ESC 关闭
            var root = new StackPanel { Margin = new Thickness(20, 16, 20, 14) };
            var theme = new SolidColorBrush(Color.FromRgb(15, 118, 110));
            root.Children.Add(new TextBlock { Text = "设置", FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = theme, Margin = new Thickness(0, 0, 0, 12) });

            // 更新源状态（只读展示：更新源由程序内置，发布时在代码常量配置，不可修改）
            bool configured = _updateRepo != UpdateRepoDefault;
            root.Children.Add(new TextBlock
            {
                Text = configured ? "更新源：" + _updateRepo : "更新源：未配置",
                FontSize = 13,
                Foreground = (Brush)FindResource("TextPrimary"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 4)
            });
            if (!configured)
                root.Children.Add(new TextBlock
                {
                    Text = "更新源由程序内置、不可修改；待作者发布带更新源的版本后即可在线更新。",
                    FontSize = 12,
                    Foreground = (Brush)FindResource("TextSecondary"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 8)
                });

            // 自动更新开关
            var chkAuto = new CheckBox { Content = "启动时自动检查更新", IsChecked = _autoUpdate, FontSize = 13, Foreground = (Brush)FindResource("TextPrimary"), Margin = new Thickness(0, 8, 0, 0) };
            chkAuto.Checked += (s, e) => { _autoUpdate = true; SaveWindowState(); };
            chkAuto.Unchecked += (s, e) => { _autoUpdate = false; SaveWindowState(); };
            root.Children.Add(chkAuto);
            root.Children.Add(new TextBlock
            {
                Text = "当前版本 v" + AppVersion + " · 关闭后仅停止启动自动检查，手动检查更新仍可用",
                FontSize = 12,
                Foreground = (Brush)FindResource("TextSecondary"),
                Margin = new Thickness(0, 4, 0, 0)
            });

            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
            var btnCheck = MakeBtn("检查更新", PrimaryButtonStyle(13.0), 110, 38);
            btnCheck.Click += (s, e) => CheckUpdate(true);
            var btnClose = MakeBtn("关闭", (Style)FindResource("ToolButton"), 110, 38);
            btnClose.Click += (s, e) => dlg.Close();
            btnCheck.Margin = new Thickness(0, 0, 8, 0);
            btnRow.Children.Add(btnCheck); btnRow.Children.Add(btnClose);
            root.Children.Add(btnRow);
            dlg.Content = root;
            dlg.ShowDialog();
        }

        // 下载新版 exe → 提示重启 → 写替换批处理（等本进程退出后覆盖 exe 再重新启动）
        void DoUpdate(string exeUrl, Window dlg)
        {
            dlg.Close();
            SetStatus("正在下载更新包…");
            string tmpDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PickLightUpdate");
            string newExe = System.IO.Path.Combine(tmpDir, "PickLight_new.exe");
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    System.IO.Directory.CreateDirectory(tmpDir);
                    using (var wc = new System.Net.WebClient())
                    {
                        wc.Headers[System.Net.HttpRequestHeader.UserAgent] = "Mozilla/5.0";
                        wc.DownloadFile(exeUrl, newExe);
                    }
                    long size = new System.IO.FileInfo(newExe).Length;
                    string sz = size >= 1048576 ? (size / 1048576.0).ToString("0.0") + " MB" : (size / 1024.0).ToString("0") + " KB";
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        var r = System.Windows.MessageBox.Show(this, "更新包已下载（" + sz + "），重启应用后生效。\n是否立即重启完成更新？", "拾光",
                            MessageBoxButton.YesNo, MessageBoxImage.Question);
                        if (r == MessageBoxResult.Yes) RestartForUpdate(newExe);
                        else SetStatus("更新包已下载（" + sz + "），可稍后重启完成更新。");
                    }));
                }
                catch (Exception ex)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        System.Windows.MessageBox.Show(this, "下载更新包失败：" + ex.Message, "拾光", MessageBoxButton.OK, MessageBoxImage.Warning);
                        SetStatus("更新下载失败。");
                    }));
                }
            });
        }

        // 替换重启：批处理延时等待本进程退出 → 强杀兜底 → 覆盖 exe → 重新启动 → 自清理
        void RestartForUpdate(string newExe)
        {
            try
            {
                string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
                string dir = System.IO.Path.GetDirectoryName(exePath);
                string bat = System.IO.Path.Combine(dir, "update_tmp.bat");
                System.IO.File.WriteAllText(bat,
                    "@echo off\r\n" +
                    "timeout /t 2 /nobreak >nul\r\n" +
                    "taskkill /im PickLight.exe /f >nul 2>&1\r\n" +
                    "copy /y \"" + newExe + "\" \"" + exePath + "\" >nul\r\n" +
                    "start \"\" \"" + exePath + "\"\r\n" +
                    "del /f /q \"" + bat + "\" >nul 2>&1\r\n" +
                    "del /f /q \"" + newExe + "\" >nul 2>&1\r\n",
                    new System.Text.UTF8Encoding(false));
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = bat, WorkingDirectory = dir, UseShellExecute = true });
                Close(); // 关闭主窗口（触发 Closing → 保存历史/窗口状态）
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(this, "启动更新失败：" + ex.Message + "\n可手动将新 exe 覆盖后重启。", "拾光", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        #endregion
    }

    public partial class App : Application
    {
        [STAThread]
        public static void Main()
        {
            // 单例互斥：只允许一个实例运行。重复启动（双击 exe）时激活已有窗口后退出，
            // 杜绝多实例窗口重叠导致点击落到被遮挡窗口上的"点不开/无响应"问题
            bool createdNew;
            using (var mutex = new System.Threading.Mutex(true, "PickLight_Single_8F3A2C", out createdNew))
            {
                if (!createdNew)
                {
                    try
                    {
                        var existing = System.Diagnostics.Process.GetProcessesByName("PickLight")
                            .Where(p => p.Id != System.Diagnostics.Process.GetCurrentProcess().Id && p.MainWindowHandle != IntPtr.Zero)
                            .FirstOrDefault();
                        if (existing != null)
                        {
                            ShowWindow(existing.MainWindowHandle, 9); // SW_RESTORE：最小化则还原
                            SetForegroundWindow(existing.MainWindowHandle);
                        }
                    }
                    catch { }
                    return;
                }
                RunApp();
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hWnd);

        static void RunApp()
        {
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                try { File.WriteAllText(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "error.log"), (e.ExceptionObject ?? new Exception("null")).ToString()); } catch { }
            };
            var app = new App();
            app.DispatcherUnhandledException += (s, e) =>
            {
                try { File.WriteAllText(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "error.log"), e.Exception.ToString()); e.Handled = true; } catch { }
            };
            var res = new ResourceDictionary();
            res["TextPrimary"] = new SolidColorBrush(Color.FromRgb(30, 41, 59));
            res["TextSecondary"] = new SolidColorBrush(Color.FromRgb(100, 116, 139));
            res["AppBackground"] = new SolidColorBrush(Color.FromRgb(248, 250, 252));
            res["CardBg"] = new SolidColorBrush(Color.FromRgb(255, 255, 255));
            res["RowAlt"] = new SolidColorBrush(Color.FromRgb(240, 244, 250)); // 交替行背景：与白色拉开差异，行区分更明显
            res["RowLine"] = new SolidColorBrush(Color.FromRgb(228, 233, 240)); // 行间水平分隔线
            res["BorderColor"] = new SolidColorBrush(Color.FromRgb(226, 232, 240));
            res["InputBg"] = new SolidColorBrush(Color.FromRgb(241, 245, 249));
            var toolBtn = new Style(typeof(Button));
            toolBtn.Setters.Add(new Setter(Button.BackgroundProperty, new SolidColorBrush(Color.FromRgb(255, 255, 255))));
            toolBtn.Setters.Add(new Setter(Button.ForegroundProperty, new SolidColorBrush(Color.FromRgb(30, 41, 59))));
            toolBtn.Setters.Add(new Setter(Button.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(203, 213, 225))));
            toolBtn.Setters.Add(new Setter(Button.BorderThicknessProperty, new Thickness(1)));
            toolBtn.Setters.Add(new Setter(Button.PaddingProperty, new Thickness(13, 8, 13, 8)));
            toolBtn.Setters.Add(new Setter(Button.FontSizeProperty, 13.0));
            toolBtn.Setters.Add(new Setter(Button.CursorProperty, Cursors.Hand));
            toolBtn.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 0, 8, 0)));
            toolBtn.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.Normal));
            var tt = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            tt.Setters.Add(new Setter(Button.BackgroundProperty, new SolidColorBrush(Color.FromRgb(238, 242, 247))));
            toolBtn.Triggers.Add(tt);
            var colHeader = new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader));
            colHeader.Setters.Add(new Setter(System.Windows.Controls.Primitives.DataGridColumnHeader.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
            colHeader.Setters.Add(new Setter(System.Windows.Controls.Primitives.DataGridColumnHeader.VerticalContentAlignmentProperty, VerticalAlignment.Center));
            colHeader.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
            colHeader.Setters.Add(new Setter(Control.FontSizeProperty, 13.0));
            // 表头：浅灰底、深色字、内边距，与正文区形成层次；底部 1px 分隔线 + 列间细线
            colHeader.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(241, 245, 249))));
            colHeader.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(51, 65, 85))));
            // 表头：左右不留 Padding（标题精确居中于列中心，不被 Padding 推偏），上下留 10px 不截字
            colHeader.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0, 10, 0, 10)));
            colHeader.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(226, 232, 240))));
            colHeader.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0, 0, 1, 1)));
            res["ColHeaderCenter"] = colHeader;
            // 表头行高自适应（不设 ColumnHeaderHeight），改字号也不会截字
            res["ToolButton"] = toolBtn;
            app.Resources = res;
            app.Run(new MainWindow());
        }
    }
}
