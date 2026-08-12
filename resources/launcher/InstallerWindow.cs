using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CodexPatch.NativeLauncher
{
    internal sealed class InstallerWindow : Window
    {
        private readonly BundlePackage _package;
        private readonly TextBox _installPath;
        private readonly CheckBox _autoUpdate;
        private readonly CheckBox _desktopShortcut;
        private readonly CheckBox _startMenuShortcut;
        private readonly CheckBox _launchAfter;
        private readonly Button _installButton;
        private readonly Button _cancelButton;
        private readonly ProgressBar _progress;
        private readonly TextBlock _status;
        private bool _busy;

        internal InstallResult Result { get; private set; }

        internal InstallerWindow(string bundleDirectory)
        {
            _package = BundleInstaller.Inspect(bundleDirectory);
            Title = "安装 Codex Desktop Patch";
            Width = 680;
            Height = 590;
            MinWidth = 620;
            MinHeight = 560;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = new FontFamily("Segoe UI");
            Background = Brush("#FFFFFFFF");
            Foreground = Brush("#FF202124");
            TrySetIcon();

            Grid root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Border header = new Border
            {
                Background = Brush("#FFF3F5F7"),
                BorderBrush = Brush("#FFE0E3E6"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(34, 28, 34, 24)
            };
            StackPanel headerContent = new StackPanel();
            headerContent.Children.Add(Label("安装 Codex Desktop Patch", 25, FontWeights.SemiBold, Brush("#FF202124")));
            TextBlock package = Label("官方 " + _package.MsixVersion + "  ·  补丁 " + _package.PatchVersion, 13, FontWeights.Normal, Brush("#FF687078"));
            package.Margin = new Thickness(0, 7, 0, 0);
            headerContent.Children.Add(package);
            header.Child = headerContent;
            root.Children.Add(header);

            ScrollViewer scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Grid.SetRow(scroll, 1);
            StackPanel form = new StackPanel { Margin = new Thickness(34, 26, 34, 22) };
            form.Children.Add(Label("安装位置", 13, FontWeights.SemiBold, Brush("#FF202124")));
            Grid pathRow = new Grid { Margin = new Thickness(0, 8, 0, 22) };
            pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _installPath = new TextBox
            {
                Text = DefaultInstallRoot(),
                Height = 38,
                Padding = new Thickness(10, 7, 10, 7),
                VerticalContentAlignment = VerticalAlignment.Center,
                BorderBrush = Brush("#FFC7CCD1"),
                BorderThickness = new Thickness(1),
                FontSize = 13
            };
            pathRow.Children.Add(_installPath);
            Button browse = Button("\uE838", "选择安装目录");
            browse.Width = 42;
            browse.Margin = new Thickness(8, 0, 0, 0);
            browse.Click += delegate { Browse(); };
            Grid.SetColumn(browse, 1);
            pathRow.Children.Add(browse);
            form.Children.Add(pathRow);

            form.Children.Add(Label("安装选项", 13, FontWeights.SemiBold, Brush("#FF202124")));
            _autoUpdate = Option("自动检查并提示安装更新", true);
            _desktopShortcut = Option("创建桌面快捷方式（Codex + 管理器）", true);
            _startMenuShortcut = Option("创建开始菜单快捷方式（Codex + 管理器）", true);
            _launchAfter = Option("安装完成后启动 Codex", true);
            form.Children.Add(_autoUpdate);
            form.Children.Add(_desktopShortcut);
            form.Children.Add(_startMenuShortcut);
            form.Children.Add(_launchAfter);

            Border verification = new Border
            {
                BorderBrush = Brush("#FFE0E3E6"),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Margin = new Thickness(0, 22, 0, 0),
                Padding = new Thickness(0, 17, 0, 0)
            };
            verification.Child = Label("安装前将验证清单、SHA-256、签名报告和所有关键文件。", 12, FontWeights.Normal, Brush("#FF687078"));
            form.Children.Add(verification);
            scroll.Content = form;
            root.Children.Add(scroll);

            Border footer = new Border
            {
                Background = Brush("#FFF7F8FA"),
                BorderBrush = Brush("#FFE0E3E6"),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(34, 15, 34, 15)
            };
            Grid footerGrid = new Grid();
            footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            StackPanel state = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            _status = Label("准备安装", 12, FontWeights.Normal, Brush("#FF687078"));
            _progress = new ProgressBar { Height = 3, IsIndeterminate = true, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 7, 26, 0) };
            state.Children.Add(_status);
            state.Children.Add(_progress);
            footerGrid.Children.Add(state);
            StackPanel actions = new StackPanel { Orientation = Orientation.Horizontal };
            _cancelButton = TextButton("取消", false);
            _cancelButton.Click += delegate { Close(); };
            _installButton = TextButton("安装", true);
            _installButton.Margin = new Thickness(8, 0, 0, 0);
            _installButton.Click += async delegate { await InstallAsync(); };
            actions.Children.Add(_cancelButton);
            actions.Children.Add(_installButton);
            Grid.SetColumn(actions, 1);
            footerGrid.Children.Add(actions);
            footer.Child = footerGrid;
            Grid.SetRow(footer, 2);
            root.Children.Add(footer);

            Content = root;
            Closing += delegate(object sender, System.ComponentModel.CancelEventArgs eventArgs) { if (_busy) eventArgs.Cancel = true; };
        }

        private async Task InstallAsync()
        {
            string root;
            try { root = PathSafety.NormalizeRoot(_installPath.Text.Trim().Trim('"')); }
            catch (Exception error) { ShowError(error.Message); return; }
            SetBusy(true, "正在验证安装包并解压，请稍候...");
            try
            {
                InstallOptions options = new InstallOptions
                {
                    InstallRoot = root,
                    AutoUpdateEnabled = _autoUpdate.IsChecked == true,
                    CreateDesktopShortcut = _desktopShortcut.IsChecked == true,
                    CreateStartMenuShortcut = _startMenuShortcut.IsChecked == true,
                    LaunchAfterInstall = _launchAfter.IsChecked == true
                };
                Result = await Task.Run(delegate { return BundleInstaller.Install(_package, options); });
                string warning = Result.Warnings.Count == 0 ? String.Empty : "\n\n" + String.Join("\n", Result.Warnings.ToArray());
                MessageBox.Show(this, "Codex Desktop Patch 已安装到：\n" + Result.InstallRoot + warning,
                    "安装完成", MessageBoxButton.OK, Result.Warnings.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
                _busy = false;
                Close();
            }
            catch (Exception error)
            {
                Result = null;
                SetBusy(false, "安装失败");
                ShowError(error.Message);
            }
        }

        private void Browse()
        {
            using (System.Windows.Forms.FolderBrowserDialog dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "选择 Codex Desktop Patch 安装目录";
                dialog.ShowNewFolderButton = true;
                string selected = _installPath.Text.Trim();
                if (Directory.Exists(selected)) dialog.SelectedPath = selected;
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) _installPath.Text = dialog.SelectedPath;
            }
        }

        private void SetBusy(bool busy, string status)
        {
            _busy = busy;
            _installButton.IsEnabled = !busy;
            _cancelButton.IsEnabled = !busy;
            _installPath.IsEnabled = !busy;
            _autoUpdate.IsEnabled = !busy;
            _desktopShortcut.IsEnabled = !busy;
            _startMenuShortcut.IsEnabled = !busy;
            _launchAfter.IsEnabled = !busy;
            _status.Text = status;
            _status.Foreground = Brush(busy ? "#FF174A7A" : "#FF687078");
            _progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ShowError(string message)
        {
            MessageBox.Show(this, message, "安装失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        private static string DefaultInstallRoot()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "CodexDesktopPatch");
        }

        private static CheckBox Option(string text, bool selected)
        {
            return new CheckBox
            {
                Content = text,
                IsChecked = selected,
                FontSize = 13,
                Margin = new Thickness(0, 12, 0, 0),
                VerticalContentAlignment = VerticalAlignment.Center
            };
        }

        private static Button Button(string glyph, string tooltip)
        {
            return new Button
            {
                Content = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 15 },
                ToolTip = tooltip,
                Height = 38,
                Background = Brush("#FFFFFFFF"),
                BorderBrush = Brush("#FFC7CCD1"),
                BorderThickness = new Thickness(1)
            };
        }

        private static Button TextButton(string text, bool primary)
        {
            return new Button
            {
                Content = text,
                MinWidth = 86,
                Height = 36,
                Padding = new Thickness(16, 0, 16, 0),
                FontSize = 13,
                Background = Brush(primary ? "#FF202124" : "#FFFFFFFF"),
                Foreground = Brush(primary ? "#FFFFFFFF" : "#FF202124"),
                BorderBrush = Brush(primary ? "#FF202124" : "#FFC7CCD1"),
                BorderThickness = new Thickness(1)
            };
        }

        private static TextBlock Label(string text, double size, FontWeight weight, Brush foreground)
        {
            return new TextBlock { Text = text, FontSize = size, FontWeight = weight, Foreground = foreground, TextWrapping = TextWrapping.Wrap };
        }

        private static Brush Brush(string value)
        {
            SolidColorBrush brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
            brush.Freeze();
            return brush;
        }

        private void TrySetIcon()
        {
            try
            {
                string executable = Process.GetCurrentProcess().MainModule.FileName;
                using (System.Drawing.Icon icon = System.Drawing.Icon.ExtractAssociatedIcon(executable))
                {
                    if (icon == null) return;
                    BitmapSource source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                        icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    source.Freeze();
                    Icon = source;
                }
            }
            catch { }
        }
    }
}
