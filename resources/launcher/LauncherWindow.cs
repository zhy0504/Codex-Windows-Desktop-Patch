using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CodexPatch.NativeLauncher
{
    internal sealed class LauncherWindow : Window
    {
        private readonly string _root;
        private CurrentInstall _current;
        private LauncherSettings _settings;
        private readonly UpdateService _updates;
        private readonly Palette _palette;
        private readonly Dictionary<string, Button> _navigation = new Dictionary<string, Button>();
        private Border _pageHost;
        private TextBlock _footerStatus;
        private Button _updateButton;
        private Button _shortcutButton;
        private TextBlock _homeStatus;
        private TextBlock _lastCheck;
        private string _activePage = "overview";
        private bool _busy;
        private bool _closeWhenIdle;
        private UpdateCheckResult _availableUpdate;

        internal LauncherWindow(string root, CurrentInstall current)
        {
            if (current == null) throw new ArgumentNullException("current");
            _root = PathSafety.NormalizeRoot(root);
            _current = current;
            _settings = LauncherCore.LoadSettings(_root);
            _updates = new UpdateService(_root);
            _palette = Palette.Create(LauncherCore.IsLightTheme());
            Title = "Codex Desktop Patch";
            Width = 980;
            Height = 590;
            MinWidth = 800;
            MinHeight = 500;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = _palette.Content;
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 14;
            TrySetIcon();
            Content = BuildShell();
            ShowPage("overview");
            Closing += delegate(object sender, System.ComponentModel.CancelEventArgs eventArgs) { if (_busy) eventArgs.Cancel = true; };
            Loaded += delegate
            {
                try { _updates.CleanupPending(_current.InstallPath); }
                catch (Exception error) { LauncherCore.WriteLog(_root, "Cleanup failed: " + error.Message); }
                try
                {
                    foreach (string removed in VersionManager.EnforceRetention(_root, _current.ArtifactBase, _settings.MaxRetainedVersions))
                        LauncherCore.WriteLog(_root, "Retention policy removed installed version: " + removed);
                }
                catch (Exception error) { LauncherCore.WriteLog(_root, "Retention cleanup failed: " + error.Message); }
                if (_settings.AutoUpdateEnabled) BeginCheck(false, false);
            };
        }

        private UIElement BuildShell()
        {
            Grid root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(42) });

            Grid shell = new Grid();
            shell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(168) });
            shell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            shell.Children.Add(BuildNavigation());

            _pageHost = new Border
            {
                Background = _palette.Content,
                Padding = new Thickness(32, 28, 32, 26)
            };
            Grid.SetColumn(_pageHost, 1);
            shell.Children.Add(_pageHost);
            root.Children.Add(shell);

            Border footer = new Border
            {
                Background = _palette.Footer,
                BorderBrush = _palette.Border,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(16, 0, 16, 0)
            };
            Grid footerGrid = new Grid();
            footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _footerStatus = Text("就绪", _palette.Muted, 13, FontWeights.Normal);
            _footerStatus.VerticalAlignment = VerticalAlignment.Center;
            TextBlock runtime = Text("原生启动器 " + LauncherConstants.Version + " · x64", _palette.Muted, 13, FontWeights.Normal);
            runtime.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(runtime, 1);
            footerGrid.Children.Add(_footerStatus);
            footerGrid.Children.Add(runtime);
            footer.Child = footerGrid;
            Grid.SetRow(footer, 1);
            root.Children.Add(footer);
            return root;
        }

        private UIElement BuildNavigation()
        {
            Border panel = new Border
            {
                Background = _palette.Navigation,
                BorderBrush = _palette.Border,
                BorderThickness = new Thickness(0, 0, 1, 0),
                Padding = new Thickness(10, 18, 10, 10)
            };
            StackPanel stack = new StackPanel();
            TextBlock brand = Text("Codex Desktop Patch", _palette.Foreground, 18, FontWeights.SemiBold);
            brand.Margin = new Thickness(12, 0, 8, 20);
            stack.Children.Add(brand);
            stack.Children.Add(NavigationButton("overview", "\uE80F", "概览"));
            stack.Children.Add(NavigationButton("versions", "\uE81C", "版本"));
            stack.Children.Add(NavigationButton("settings", "\uE713", "设置"));
            stack.Children.Add(NavigationButton("logs", "\uE8A5", "日志"));
            panel.Child = stack;
            return panel;
        }

        private Button NavigationButton(string page, string glyph, string label)
        {
            Button button = CreateButton(glyph, label, ButtonKind.Navigation);
            button.Margin = new Thickness(0, 0, 0, 4);
            button.HorizontalContentAlignment = HorizontalAlignment.Left;
            button.Click += delegate { if (!_busy) ShowPage(page); };
            _navigation[page] = button;
            return button;
        }

        private void ShowPage(string page)
        {
            _activePage = page;
            foreach (KeyValuePair<string, Button> item in _navigation)
                ApplyButtonAppearance(item.Value, item.Key == page ? ButtonKind.NavigationSelected : ButtonKind.Navigation);
            if (page == "overview") _pageHost.Child = BuildOverview();
            else if (page == "versions") _pageHost.Child = BuildVersions();
            else if (page == "settings") _pageHost.Child = BuildSettings();
            else _pageHost.Child = BuildLogs();
        }

        private UIElement BuildOverview()
        {
            StackPanel page = new StackPanel();
            Grid heading = new Grid();
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            StackPanel headingText = new StackPanel();
            headingText.Children.Add(Text("概览", _palette.Foreground, 24, FontWeights.SemiBold));
            headingText.Children.Add(Muted("当前安装状态"));
            _homeStatus = Text("●  运行正常", _palette.Success, 14, FontWeights.Normal);
            _homeStatus.Margin = new Thickness(0, 6, 0, 0);
            Grid.SetColumn(_homeStatus, 1);
            heading.Children.Add(headingText);
            heading.Children.Add(_homeStatus);
            page.Children.Add(heading);

            Border versionBand = new Border
            {
                BorderBrush = _palette.Border,
                BorderThickness = new Thickness(0, 1, 0, 1),
                Margin = new Thickness(0, 24, 0, 18),
                Padding = new Thickness(0, 22, 0, 22)
            };
            Grid versionGrid = new Grid();
            versionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            versionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            StackPanel versionText = new StackPanel();
            versionText.Children.Add(Muted("当前版本"));
            TextBlock artifact = Text(_current.ArtifactBase, _palette.Foreground, 18, FontWeights.SemiBold);
            artifact.Margin = new Thickness(0, 5, 0, 3);
            versionText.Children.Add(artifact);
            versionText.Children.Add(Muted("官方 " + _current.MsixVersion + " · 补丁 " + _current.PatchVersion));
            Button launch = CreateButton("\uE768", "启动 Codex", ButtonKind.Primary);
            launch.MinWidth = 138;
            launch.Click += delegate
            {
                try
                {
                    LauncherCore.LaunchCurrent(_root, new string[0]);
                    _footerStatus.Text = "Codex 已启动";
                    if (_busy)
                    {
                        _closeWhenIdle = true;
                        Hide();
                    }
                    else Close();
                }
                catch (Exception error) { ShowError("启动失败", error); }
            };
            launch.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(launch, 1);
            versionGrid.Children.Add(versionText);
            versionGrid.Children.Add(launch);
            versionBand.Child = versionGrid;
            page.Children.Add(versionBand);

            Grid details = new Grid();
            details.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(118) });
            details.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            AddDetail(details, 0, "安装目录", _root);
            AddDetail(details, 1, "自动检查", _settings.AutoUpdateEnabled ? "已开启 · 每 24 小时" : "已关闭");
            _lastCheck = AddDetail(details, 2, "最近检查", ReadLastCheckText());
            page.Children.Add(details);

            WrapPanel actions = new WrapPanel { Margin = new Thickness(0, 22, 0, 0) };
            _updateButton = CreateButton("\uE72C", _availableUpdate == null ? "检查更新" : "安装更新", ButtonKind.Secondary);
            _updateButton.Margin = new Thickness(0, 0, 8, 8);
            _updateButton.Click += delegate
            {
                if (_availableUpdate != null) BeginInstall(_availableUpdate);
                else BeginCheck(true, true);
            };
            Button versions = CreateButton("\uE777", "管理旧版本", ButtonKind.Quiet);
            versions.Margin = new Thickness(0, 0, 8, 8);
            versions.Click += delegate { ShowPage("versions"); };
            Button folder = CreateButton("\uE838", "打开目录", ButtonKind.Quiet);
            folder.Margin = new Thickness(0, 0, 8, 8);
            folder.Click += delegate { OpenPath(_root); };
            _shortcutButton = CreateButton("\uE90F", "检查并修复快捷方式", ButtonKind.Quiet);
            _shortcutButton.Margin = new Thickness(0, 0, 8, 8);
            _shortcutButton.Click += delegate { BeginRepairShortcuts(); };
            actions.Children.Add(_updateButton);
            actions.Children.Add(versions);
            actions.Children.Add(folder);
            actions.Children.Add(_shortcutButton);
            page.Children.Add(actions);
            return page;
        }

        private UIElement BuildVersions()
        {
            DockPanel page = new DockPanel();
            StackPanel heading = new StackPanel { Margin = new Thickness(0, 0, 0, 22) };
            heading.Children.Add(Text("版本", _palette.Foreground, 24, FontWeights.SemiBold));
            heading.Children.Add(Muted("已安装的独立版本"));
            DockPanel.SetDock(heading, Dock.Top);
            page.Children.Add(heading);

            ScrollViewer scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            StackPanel rows = new StackPanel();
            IList<InstalledVersion> versions = LauncherCore.ListInstalledVersions(_root, _current.ArtifactBase);
            foreach (InstalledVersion item in versions)
            {
                InstalledVersion version = item;
                Border row = new Border
                {
                    BorderBrush = _palette.Border,
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Padding = new Thickness(4, 14, 4, 14)
                };
                Grid grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(105) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
                StackPanel identity = new StackPanel();
                TextBlock versionName = Text(version.MsixVersion, _palette.Foreground, 15, FontWeights.SemiBold);
                identity.Children.Add(versionName);
                string detail = "补丁 " + version.PatchVersion;
                if (version.IsPinned) detail += "  ·  已固定";
                if (!version.HasIntegrityEvidence) detail += "  ·  需修复";
                identity.Children.Add(Muted(detail));
                if (!String.IsNullOrWhiteSpace(version.Note))
                {
                    TextBlock note = Text(version.Note, _palette.Muted, 12, FontWeights.Normal);
                    note.Margin = new Thickness(0, 5, 12, 0);
                    note.TextTrimming = TextTrimming.CharacterEllipsis;
                    note.ToolTip = version.Note;
                    identity.Children.Add(note);
                }
                TextBlock size = Text("计算中...", _palette.Muted, 12, FontWeights.Normal);
                size.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(size, 1);
                TextBlock installed = Text(FormatInstalledAt(version.InstalledAt), _palette.Muted, 13, FontWeights.Normal);
                installed.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(installed, 2);
                UIElement action;
                if (version.IsCurrent)
                {
                    action = Text("当前", _palette.Success, 14, FontWeights.SemiBold);
                    ((TextBlock)action).VerticalAlignment = VerticalAlignment.Center;
                    ((TextBlock)action).HorizontalAlignment = HorizontalAlignment.Right;
                }
                else
                {
                    Button launch = CreateButton("\uE768", "启动", ButtonKind.Secondary);
                    launch.HorizontalAlignment = HorizontalAlignment.Right;
                    launch.IsEnabled = version.HasIntegrityEvidence;
                    if (!version.HasIntegrityEvidence) launch.ToolTip = version.IntegrityIssue;
                    launch.Click += delegate { LaunchInstalledVersion(version); };
                    action = launch;
                }
                Grid.SetColumn(action, 3);
                Button more = CreateIconButton("\uE712", "版本操作");
                more.HorizontalAlignment = HorizontalAlignment.Right;
                more.ContextMenu = BuildVersionMenu(version);
                more.Click += delegate { if (!_busy) more.ContextMenu.IsOpen = true; };
                Grid.SetColumn(more, 4);
                grid.Children.Add(identity);
                grid.Children.Add(size);
                grid.Children.Add(installed);
                grid.Children.Add(action);
                grid.Children.Add(more);
                row.Child = grid;
                rows.Children.Add(row);
                Task.Factory.StartNew(delegate { return VersionManager.CalculateSize(version); })
                    .ContinueWith(delegate(Task<long> task)
                    {
                        Dispatcher.BeginInvoke(new Action(delegate
                        {
                            size.Text = task.IsFaulted ? "无法统计" : FormatBytes(task.Result);
                            if (task.IsFaulted) size.ToolTip = Unwrap(task.Exception).Message;
                        }));
                    });
            }
            if (versions.Count == 0) rows.Children.Add(Muted("没有找到可用的安装版本。"));
            scroll.Content = rows;
            page.Children.Add(scroll);
            return page;
        }

        private ContextMenu BuildVersionMenu(InstalledVersion version)
        {
            ContextMenu menu = new ContextMenu();
            MenuItem rollback = new MenuItem
            {
                Header = "设为当前版本（回退）",
                IsEnabled = !version.IsCurrent && version.HasIntegrityEvidence,
                ToolTip = version.HasIntegrityEvidence ? null : version.IntegrityIssue
            };
            rollback.Click += delegate { ConfirmRollback(version); };
            MenuItem note = new MenuItem { Header = "编辑备注" };
            note.Click += delegate { EditVersionNote(version); };
            MenuItem pin = new MenuItem { Header = version.IsPinned ? "取消固定" : "固定保护" };
            pin.Click += delegate { ToggleVersionPin(version); };
            MenuItem validate = new MenuItem { Header = "重新校验关键文件" };
            validate.Click += delegate { BeginValidateVersion(version); };
            MenuItem repair = new MenuItem { Header = "从 Release 修复", IsEnabled = !version.IsCurrent };
            repair.Click += delegate { BeginRepairVersion(version); };
            MenuItem delete = new MenuItem
            {
                Header = version.IsPinned ? "删除（已固定）" : "删除版本",
                IsEnabled = !version.IsCurrent && !version.IsPinned,
                Foreground = _palette.Warning
            };
            delete.Click += delegate { DeleteInstalledVersion(version); };
            menu.Items.Add(rollback);
            menu.Items.Add(new Separator());
            menu.Items.Add(note);
            menu.Items.Add(pin);
            menu.Items.Add(new Separator());
            menu.Items.Add(validate);
            menu.Items.Add(repair);
            menu.Items.Add(new Separator());
            menu.Items.Add(delete);
            return menu;
        }

        private void LaunchInstalledVersion(InstalledVersion version)
        {
            if (_busy) return;
            try
            {
                VersionManager.LaunchInstalled(_root, version, new string[0]);
                _footerStatus.Text = "已直接启动 " + version.MsixVersion + "，当前版本未改变";
            }
            catch (Exception error) { ShowError("启动旧版本失败", error); }
        }

        private void EditVersionNote(InstalledVersion version)
        {
            if (_busy) return;
            string note = PromptVersionNote(version);
            if (note == null) return;
            try
            {
                VersionCatalog.SetNote(_root, version.ArtifactBase, note);
                _footerStatus.Text = "版本备注已保存";
                ShowPage("versions");
            }
            catch (Exception error) { ShowError("无法保存版本备注", error); }
        }

        private string PromptVersionNote(InstalledVersion version)
        {
            Window dialog = new Window
            {
                Title = "版本备注",
                Owner = this,
                Width = 460,
                Height = 220,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = _palette.Content,
                Foreground = _palette.Foreground,
                FontFamily = FontFamily
            };
            Grid root = new Grid { Margin = new Thickness(24) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.Children.Add(Text(version.MsixVersion + " · 补丁 " + version.PatchVersion, _palette.Foreground, 15, FontWeights.SemiBold));
            TextBox input = new TextBox
            {
                Text = version.Note ?? String.Empty,
                MaxLength = 160,
                Height = 38,
                Margin = new Thickness(0, 14, 0, 18),
                Padding = new Thickness(9, 6, 9, 6),
                VerticalContentAlignment = VerticalAlignment.Center,
                Foreground = _palette.Foreground,
                Background = _palette.Button,
                BorderBrush = _palette.BorderStrong
            };
            Grid.SetRow(input, 1);
            root.Children.Add(input);
            StackPanel actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            Button cancel = CreateButton("\uE711", "取消", ButtonKind.Quiet);
            Button save = CreateButton("\uE74E", "保存", ButtonKind.Primary);
            save.Margin = new Thickness(8, 0, 0, 0);
            cancel.Click += delegate { dialog.DialogResult = false; };
            save.Click += delegate { dialog.DialogResult = true; };
            actions.Children.Add(cancel);
            actions.Children.Add(save);
            Grid.SetRow(actions, 2);
            root.Children.Add(actions);
            dialog.Content = root;
            input.SelectAll();
            input.Focus();
            return dialog.ShowDialog() == true ? input.Text : null;
        }

        private void ToggleVersionPin(InstalledVersion version)
        {
            if (_busy) return;
            try
            {
                VersionCatalog.SetPinned(_root, version.ArtifactBase, !version.IsPinned);
                _footerStatus.Text = version.IsPinned ? "已取消固定" : "版本已固定";
                ShowPage("versions");
            }
            catch (Exception error) { ShowError("无法更新固定状态", error); }
        }

        private void BeginValidateVersion(InstalledVersion version)
        {
            if (_busy) return;
            if (MessageBox.Show(this,
                "将下载该版本对应的 GitHub Release bundle，并重新校验安装标记和关键文件。继续吗？",
                "重新校验", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) != MessageBoxResult.Yes) return;
            SetBusy(true, "正在下载并校验 " + version.MsixVersion + "...");
            Task.Factory.StartNew(delegate { return _updates.ValidateInstalled(version); })
                .ContinueWith(delegate(Task<VersionValidationResult> task)
                {
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        SetBusy(false, "就绪");
                        if (task.IsFaulted) { ShowError("版本校验失败", Unwrap(task.Exception)); return; }
                        VersionValidationResult result = task.Result;
                        string message = result.IsValid
                            ? "校验通过，共核对 " + result.CheckedFiles + " 个关键文件。"
                            : "发现 " + result.Issues.Count + " 个问题：\n\n" + String.Join("\n", result.Issues.ToArray());
                        MessageBox.Show(this, message, result.IsValid ? "校验通过" : "校验发现问题",
                            MessageBoxButton.OK, result.IsValid ? MessageBoxImage.Information : MessageBoxImage.Warning);
                    }));
                });
        }

        private void BeginRepairVersion(InstalledVersion version)
        {
            if (_busy) return;
            if (version.IsCurrent) { MessageBox.Show(this, "当前版本正在运行，请先回退到其他版本后再修复。", "无法修复当前版本", MessageBoxButton.OK, MessageBoxImage.Information); return; }
            if (MessageBox.Show(this,
                "将下载并验证对应 Release，然后完整替换此版本目录。备注和固定状态会保留。继续吗？",
                "修复版本", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            SetBusy(true, "正在下载并修复 " + version.MsixVersion + "...");
            Task.Factory.StartNew(delegate { return _updates.RepairInstalled(version, _current.ArtifactBase); })
                .ContinueWith(delegate(Task<VersionRepairResult> task)
                {
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        SetBusy(false, "就绪");
                        if (task.IsFaulted) { ShowError("版本修复失败", Unwrap(task.Exception)); return; }
                        MessageBox.Show(this, "版本已修复，并通过 " + task.Result.Validation.CheckedFiles + " 个关键文件校验。",
                            "修复完成", MessageBoxButton.OK, MessageBoxImage.Information);
                        ShowPage("versions");
                    }));
                });
        }

        private void BeginRepairShortcuts()
        {
            if (_busy) return;
            if (MessageBox.Show(this,
                "将检查桌面和开始菜单中的 4 个 Codex Desktop Patch 快捷方式。缺失或配置不正确的入口会被修复或重新创建，不会修改其他快捷方式。继续吗？",
                "检查并修复快捷方式", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) != MessageBoxResult.Yes) return;

            SetBusy(true, "正在检查并修复快捷方式...");
            Task.Factory.StartNew(delegate { return BundleInstaller.CheckAndRepairShortcuts(_root); })
                .ContinueWith(delegate(Task<ShortcutRepairResult> task)
                {
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        SetBusy(false, "就绪");
                        if (task.IsFaulted) { ShowError("快捷方式修复失败", Unwrap(task.Exception)); return; }
                        ShortcutRepairResult result = task.Result;
                        List<string> lines = new List<string>
                        {
                            "已检查 " + result.Checked + " 个快捷方式。",
                            "正常：" + result.Healthy + "    已修复：" + result.Repaired + "    已创建：" + result.Created
                        };
                        if (result.Details.Count > 0)
                        {
                            lines.Add(String.Empty);
                            lines.Add(String.Join("\n", result.Details.ToArray()));
                        }
                        if (result.Failures.Count > 0)
                        {
                            lines.Add(String.Empty);
                            lines.Add("失败：");
                            lines.Add(String.Join("\n", result.Failures.ToArray()));
                        }
                        MessageBox.Show(this, String.Join("\n", lines.ToArray()),
                            result.IsSuccessful ? "快捷方式检查完成" : "快捷方式部分失败",
                            MessageBoxButton.OK, result.IsSuccessful ? MessageBoxImage.Information : MessageBoxImage.Warning);
                    }));
                });
        }

        private void DeleteInstalledVersion(InstalledVersion version)
        {
            if (_busy) return;
            if (MessageBox.Show(this,
                "永久删除 Codex " + version.MsixVersion + "（补丁 " + version.PatchVersion + "）？\n\n此操作无法撤销。",
                "删除旧版本", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            SetBusy(true, "正在删除 " + version.MsixVersion + "...");
            Task.Factory.StartNew(delegate { VersionManager.Delete(_root, _current, version); })
                .ContinueWith(delegate(Task task)
                {
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        SetBusy(false, "就绪");
                        if (task.IsFaulted) { ShowError("删除版本失败", Unwrap(task.Exception)); return; }
                        _footerStatus.Text = "旧版本已删除";
                        ShowPage("versions");
                    }));
                });
        }

        private UIElement BuildSettings()
        {
            StackPanel page = new StackPanel();
            page.Children.Add(Text("设置", _palette.Foreground, 24, FontWeights.SemiBold));
            TextBlock context = Muted("启动与更新偏好");
            context.Margin = new Thickness(0, 4, 0, 22);
            page.Children.Add(context);

            CheckBox autoUpdate = SettingCheckBox("自动检查更新", "每 24 小时检查稳定版 Release", _settings.AutoUpdateEnabled);
            autoUpdate.Checked += delegate { SaveSettingsFromUi(delegate(LauncherSettings settings) { settings.AutoUpdateEnabled = true; }); };
            autoUpdate.Unchecked += delegate { SaveSettingsFromUi(delegate(LauncherSettings settings) { settings.AutoUpdateEnabled = false; }); };
            page.Children.Add(autoUpdate);

            CheckBox keep = SettingCheckBox("更新前保留当前版本", "作为回退版本保留", _settings.KeepCurrentVersion);
            keep.Checked += delegate { SaveSettingsFromUi(delegate(LauncherSettings settings) { settings.KeepCurrentVersion = true; }); };
            keep.Unchecked += delegate { SaveSettingsFromUi(delegate(LauncherSettings settings) { settings.KeepCurrentVersion = false; }); };
            page.Children.Add(keep);

            Grid retention = new Grid { Margin = new Thickness(0, 12, 0, 2) };
            retention.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            retention.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
            StackPanel retentionText = new StackPanel { Margin = new Thickness(0, 8, 16, 8) };
            retentionText.Children.Add(Text("最多保留旧版本", _palette.Foreground, 15, FontWeights.SemiBold));
            retentionText.Children.Add(Muted("当前版本不计入；固定版本不会自动删除"));
            ComboBox retentionChoice = new ComboBox
            {
                Height = 36,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = _palette.Foreground,
                Background = _palette.Button,
                BorderBrush = _palette.BorderStrong
            };
            List<int> retentionValues = new List<int> { 0, 1, 2, 3, 5, 10 };
            if (!retentionValues.Contains(_settings.MaxRetainedVersions)) retentionValues.Add(_settings.MaxRetainedVersions);
            retentionValues.Sort();
            foreach (int value in retentionValues)
            {
                ComboBoxItem item = new ComboBoxItem { Content = value == 0 ? "不限" : value.ToString(), Tag = value };
                retentionChoice.Items.Add(item);
                if (value == _settings.MaxRetainedVersions) retentionChoice.SelectedItem = item;
            }
            retentionChoice.SelectionChanged += delegate
            {
                ComboBoxItem selected = retentionChoice.SelectedItem as ComboBoxItem;
                if (selected == null) return;
                int maximum = (int)selected.Tag;
                SaveSettingsFromUi(delegate(LauncherSettings settings) { settings.MaxRetainedVersions = maximum; });
            };
            Grid.SetColumn(retentionChoice, 1);
            retention.Children.Add(retentionText);
            retention.Children.Add(retentionChoice);
            page.Children.Add(retention);

            WrapPanel actions = new WrapPanel { Margin = new Thickness(0, 24, 0, 0) };
            Button folder = CreateButton("\uE838", "打开安装目录", ButtonKind.Secondary);
            folder.Margin = new Thickness(0, 0, 8, 8);
            folder.Click += delegate { OpenPath(_root); };
            Button log = CreateButton("\uE8A5", "查看更新日志", ButtonKind.Quiet);
            log.Click += delegate { ShowPage("logs"); };
            actions.Children.Add(folder);
            actions.Children.Add(log);
            page.Children.Add(actions);
            return page;
        }

        private UIElement BuildLogs()
        {
            DockPanel page = new DockPanel();
            Grid heading = new Grid { Margin = new Thickness(0, 0, 0, 20) };
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            StackPanel title = new StackPanel();
            title.Children.Add(Text("日志", _palette.Foreground, 24, FontWeights.SemiBold));
            title.Children.Add(Muted("updater.log"));
            Button open = CreateButton("\uE8A7", "打开文件", ButtonKind.Quiet);
            open.Click += delegate { OpenLogFile(); };
            Grid.SetColumn(open, 1);
            heading.Children.Add(title);
            heading.Children.Add(open);
            DockPanel.SetDock(heading, Dock.Top);
            page.Children.Add(heading);
            TextBox log = new TextBox
            {
                Text = ReadLog(),
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                FontSize = 13,
                Foreground = _palette.Foreground,
                Background = _palette.LogBackground,
                BorderBrush = _palette.Border,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(14)
            };
            page.Children.Add(log);
            return page;
        }

        private void BeginCheck(bool force, bool installWhenAvailable)
        {
            if (_busy) return;
            SetBusy(true, "正在检查更新…");
            Task.Factory.StartNew(delegate { return _updates.Check(_current, force); })
                .ContinueWith(delegate(Task<UpdateCheckResult> task)
                {
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        SetBusy(false, "就绪");
                        if (task.IsFaulted)
                        {
                            Exception error = Unwrap(task.Exception);
                            if (_closeWhenIdle)
                            {
                                LauncherCore.WriteLog(_root, "Background update check failed: " + error.Message);
                                FinishHiddenLaunch(false);
                            }
                            else ShowError("更新检查失败", error);
                            return;
                        }
                        UpdateCheckResult result = task.Result;
                        if (result.Status == "UpdateAvailable")
                        {
                            _availableUpdate = result;
                            _footerStatus.Text = "发现新版本 " + result.Candidate.MsixVersion;
                            if (_homeStatus != null) { _homeStatus.Text = "●  有可用更新"; _homeStatus.Foreground = _palette.Warning; }
                            if (_updateButton != null) _updateButton.Content = ButtonContent("\uE896", "安装更新");
                            FinishHiddenLaunch(true);
                            if (installWhenAvailable) BeginInstall(result);
                        }
                        else
                        {
                            _availableUpdate = null;
                            _footerStatus.Text = result.Status == "CheckDeferred" ? "稍后自动检查" : "已是最新版本";
                            if (_lastCheck != null) _lastCheck.Text = result.Status == "CheckDeferred" ? ReadLastCheckText() : "刚刚 · 已是最新版本";
                            FinishHiddenLaunch(false);
                        }
                    }));
                });
        }

        private void BeginInstall(UpdateCheckResult update)
        {
            if (_busy) return;
            MessageBoxResult upgrade = MessageBox.Show(this,
                "发现 Codex " + update.Candidate.MsixVersion + "（补丁 " + update.Candidate.PatchVersion + "）。\n\n现在下载并安装吗？",
                "Codex Desktop Patch 更新", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
            if (upgrade != MessageBoxResult.Yes) return;
            MessageBoxResult backup = MessageBox.Show(this,
                "保留当前版本作为回退备份吗？", "保留当前版本",
                MessageBoxButton.YesNo, MessageBoxImage.Question,
                _settings.KeepCurrentVersion ? MessageBoxResult.Yes : MessageBoxResult.No);
            bool keep = backup == MessageBoxResult.Yes;
            SetBusy(true, "正在下载并安装更新…");
            Task.Factory.StartNew(delegate { return _updates.Install(_current, update, keep); })
                .ContinueWith(delegate(Task<UpdateInstallResult> task)
                {
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        SetBusy(false, "就绪");
                        if (task.IsFaulted) { ShowError("更新失败", Unwrap(task.Exception)); return; }
                        _current = LauncherCore.LoadCurrent(_root);
                        _availableUpdate = null;
                        LauncherCore.WriteLog(_root, "GUI update completed: " + task.Result.ReleaseTag);
                        MessageBox.Show(this, "新版本已经安装，下次启动将使用新版本。", "更新完成", MessageBoxButton.OK, MessageBoxImage.Information);
                        ShowPage("overview");
                    }));
                });
        }

        private void ConfirmRollback(InstalledVersion version)
        {
            if (MessageBox.Show(this,
                "回退到 Codex " + version.MsixVersion + "（补丁 " + version.PatchVersion + "）？\n\n回退后将关闭自动更新。",
                "确认回退", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            try
            {
                _current = LauncherCore.Rollback(_root, version.ArtifactBase);
                _settings = LauncherCore.LoadSettings(_root);
                _availableUpdate = null;
                _footerStatus.Text = "已回退到 " + version.MsixVersion;
                ShowPage("versions");
            }
            catch (Exception error) { ShowError("回退失败", error); }
        }

        private void SaveSettingsFromUi(Action<LauncherSettings> update)
        {
            try { _settings = LauncherCore.UpdateSettings(_root, update); _footerStatus.Text = "设置已保存"; }
            catch (Exception error) { ShowError("无法保存设置", error); }
        }

        private void SetBusy(bool busy, string message)
        {
            _busy = busy;
            _footerStatus.Text = message;
            if (_updateButton != null) _updateButton.IsEnabled = !busy;
            if (_shortcutButton != null) _shortcutButton.IsEnabled = !busy;
            Mouse.OverrideCursor = busy ? Cursors.Wait : null;
        }

        private void FinishHiddenLaunch(bool needsAttention)
        {
            if (!_closeWhenIdle) return;
            _closeWhenIdle = false;
            if (!needsAttention)
            {
                Close();
                return;
            }
            Show();
            WindowState = WindowState.Normal;
            Activate();
        }

        private static Exception Unwrap(AggregateException error)
        {
            return error == null ? new InvalidOperationException("Unknown asynchronous failure.") : error.Flatten().InnerExceptions[0];
        }

        private void ShowError(string title, Exception error)
        {
            LauncherCore.WriteLog(_root, title + ": " + error.Message);
            _footerStatus.Text = title;
            MessageBox.Show(this, error.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        }

        private TextBlock AddDetail(Grid grid, int row, string label, string value)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            TextBlock labelText = Text(label, _palette.Muted, 14, FontWeights.Normal);
            labelText.Margin = new Thickness(0, 9, 16, 9);
            TextBlock valueText = Text(value, _palette.Foreground, 14, FontWeights.Normal);
            valueText.Margin = new Thickness(0, 9, 0, 9);
            valueText.TextWrapping = TextWrapping.Wrap;
            Grid.SetRow(labelText, row);
            Grid.SetRow(valueText, row);
            Grid.SetColumn(valueText, 1);
            grid.Children.Add(labelText);
            grid.Children.Add(valueText);
            return valueText;
        }

        private CheckBox SettingCheckBox(string title, string detail, bool value)
        {
            CheckBox check = new CheckBox
            {
                IsChecked = value,
                Foreground = _palette.Foreground,
                BorderBrush = _palette.Border,
                Padding = new Thickness(0),
                Margin = new Thickness(0, 0, 0, 2),
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            StackPanel content = new StackPanel { Margin = new Thickness(8, 14, 0, 14) };
            content.Children.Add(Text(title, _palette.Foreground, 15, FontWeights.SemiBold));
            content.Children.Add(Muted(detail));
            check.Content = content;
            return check;
        }

        private Button CreateButton(string glyph, string label, ButtonKind kind)
        {
            Button button = new Button
            {
                Content = ButtonContent(glyph, label),
                MinHeight = kind == ButtonKind.Navigation || kind == ButtonKind.NavigationSelected ? 40 : 38,
                Padding = new Thickness(13, 0, 13, 0),
                Cursor = Cursors.Hand,
                FocusVisualStyle = null
            };
            ApplyButtonAppearance(button, kind);
            return button;
        }

        private Button CreateIconButton(string glyph, string tooltip)
        {
            Button button = new Button
            {
                Content = Text(glyph, _palette.Foreground, 16, FontWeights.Normal),
                Width = 38,
                Height = 38,
                Padding = new Thickness(0),
                ToolTip = tooltip,
                Cursor = Cursors.Hand,
                FocusVisualStyle = null
            };
            ((TextBlock)button.Content).FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
            ApplyButtonAppearance(button, ButtonKind.Quiet);
            return button;
        }

        private UIElement ButtonContent(string glyph, string label)
        {
            StackPanel content = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            TextBlock icon = Text(glyph, _palette.Foreground, 16, FontWeights.Normal);
            icon.FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
            icon.Margin = new Thickness(0, 0, 9, 0);
            TextBlock text = Text(label, _palette.Foreground, 14, FontWeights.SemiBold);
            content.Children.Add(icon);
            content.Children.Add(text);
            return content;
        }

        private void ApplyButtonAppearance(Button button, ButtonKind kind)
        {
            Brush background = Brushes.Transparent;
            Brush foreground = _palette.Foreground;
            Brush border = Brushes.Transparent;
            if (kind == ButtonKind.Primary) { background = _palette.Primary; foreground = _palette.PrimaryForeground; border = _palette.Primary; }
            else if (kind == ButtonKind.Secondary) { background = _palette.Button; border = _palette.BorderStrong; }
            else if (kind == ButtonKind.NavigationSelected) { background = _palette.Selected; foreground = _palette.SelectedForeground; }
            button.Background = background;
            button.Foreground = foreground;
            button.BorderBrush = border;
            button.BorderThickness = new Thickness(1);
            button.Template = ButtonTemplate(background, border);
            StackPanel content = button.Content as StackPanel;
            if (content != null)
                foreach (TextBlock text in content.Children)
                    text.Foreground = foreground;
        }

        private ControlTemplate ButtonTemplate(Brush background, Brush borderBrush)
        {
            FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Button.BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Button.BorderThicknessProperty));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            FrameworkElementFactory presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, new TemplateBindingExtension(Button.HorizontalContentAlignmentProperty));
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            presenter.SetValue(ContentPresenter.MarginProperty, new TemplateBindingExtension(Button.PaddingProperty));
            border.AppendChild(presenter);
            ControlTemplate template = new ControlTemplate(typeof(Button));
            template.VisualTree = border;
            Trigger disabled = new Trigger { Property = Button.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(Button.OpacityProperty, 0.55));
            template.Triggers.Add(disabled);
            return template;
        }

        private static TextBlock Text(string value, Brush foreground, double size, FontWeight weight)
        {
            return new TextBlock { Text = value, Foreground = foreground, FontSize = size, FontWeight = weight };
        }

        private static string FormatBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double value = Math.Max(0, bytes);
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
            return (unit == 0 ? value.ToString("0") : value.ToString("0.0")) + " " + units[unit];
        }

        private TextBlock Muted(string value)
        {
            TextBlock text = Text(value, _palette.Muted, 13, FontWeights.Normal);
            text.Margin = new Thickness(0, 4, 0, 0);
            return text;
        }

        private string ReadLastCheckText()
        {
            try
            {
                Dictionary<string, object> state = JsonStore.ReadObject(Path.Combine(_root, "updater-state.json"));
                string value = JsonStore.OptionalString(state, "lastCheckedAt");
                if (!String.IsNullOrWhiteSpace(value)) return DateTimeOffset.Parse(value).ToLocalTime().ToString("yyyy-MM-dd HH:mm") + " · 已检查";
            }
            catch { }
            return "尚未检查";
        }

        private static string FormatInstalledAt(string value)
        {
            DateTimeOffset parsed;
            return DateTimeOffset.TryParse(value, out parsed) ? parsed.ToLocalTime().ToString("yyyy-MM-dd") : "未知";
        }

        private string ReadLog()
        {
            string path = Path.Combine(_root, "logs", "updater.log");
            try { return File.Exists(path) ? File.ReadAllText(path, System.Text.Encoding.UTF8) : "尚无更新日志。"; }
            catch (Exception error) { return "无法读取日志：" + error.Message; }
        }

        private void OpenLogFile()
        {
            string path = Path.Combine(_root, "logs", "updater.log");
            if (!File.Exists(path)) { MessageBox.Show(this, "尚无更新日志。", "日志", MessageBoxButton.OK, MessageBoxImage.Information); return; }
            OpenPath(path);
        }

        private void OpenPath(string path)
        {
            try { Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true }); }
            catch (Exception error) { ShowError("无法打开", error); }
        }

        private void TrySetIcon()
        {
            try
            {
                string executable = Process.GetCurrentProcess().MainModule.FileName;
                using (System.Drawing.Icon icon = System.Drawing.Icon.ExtractAssociatedIcon(executable))
                {
                    if (icon == null) return;
                    BitmapSource source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    source.Freeze();
                    Icon = source;
                }
            }
            catch { }
        }

        private enum ButtonKind { Primary, Secondary, Quiet, Navigation, NavigationSelected }

        private sealed class Palette
        {
            internal Brush Content;
            internal Brush Navigation;
            internal Brush Footer;
            internal Brush Foreground;
            internal Brush Muted;
            internal Brush Border;
            internal Brush BorderStrong;
            internal Brush Button;
            internal Brush Primary;
            internal Brush PrimaryForeground;
            internal Brush Selected;
            internal Brush SelectedForeground;
            internal Brush Success;
            internal Brush Warning;
            internal Brush LogBackground;

            internal static Palette Create(bool light)
            {
                return new Palette
                {
                    Content = Brush(light ? "#FFFFFFFF" : "#FF202327"),
                    Navigation = Brush(light ? "#FFF1F3F4" : "#FF272A2F"),
                    Footer = Brush(light ? "#FFF7F8FA" : "#FF272A2F"),
                    Foreground = Brush(light ? "#FF202124" : "#FFE8EAED"),
                    Muted = Brush(light ? "#FF687078" : "#FFAEB4BB"),
                    Border = Brush(light ? "#FFE0E3E6" : "#FF383D43"),
                    BorderStrong = Brush(light ? "#FFC7CCD1" : "#FF515860"),
                    Button = Brush(light ? "#FFFFFFFF" : "#FF2B2F34"),
                    Primary = Brush(light ? "#FF202124" : "#FFE8EAED"),
                    PrimaryForeground = Brush(light ? "#FFFFFFFF" : "#FF202124"),
                    Selected = Brush(light ? "#FFDFE7F3" : "#FF344253"),
                    SelectedForeground = Brush(light ? "#FF174A7A" : "#FFD5E7FA"),
                    Success = Brush(light ? "#FF198754" : "#FF4CC38A"),
                    Warning = Brush(light ? "#FFA45A00" : "#FFFFB86B"),
                    LogBackground = Brush(light ? "#FFF6F7F8" : "#FF181A1D")
                };
            }

            private static Brush Brush(string value)
            {
                SolidColorBrush brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
                brush.Freeze();
                return brush;
            }
        }
    }
}
