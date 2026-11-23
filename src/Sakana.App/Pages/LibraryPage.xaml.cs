using Sakana.Application;
using Sakana.App.ViewModels;
using Sakana.Domain;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.Storage.Pickers;

namespace Sakana.App.Pages;

public sealed partial class LibraryPage : Page
{
    private bool _updatingLibraryFilters;

    public LibraryPage() : this(App.CurrentApp.LauncherService, App.CurrentApp.MinecraftAccountService)
    {
    }

    public LibraryPage(ILauncherService launcherService, IMinecraftAccountService accountService)
    {
        ViewModel = new LibraryViewModel(launcherService, accountService, DispatcherQueue.GetForCurrentThread());
        InitializeComponent();
        Loaded += Page_Loaded;
        Unloaded += Page_Unloaded;
    }

    public LibraryViewModel ViewModel { get; }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.RefreshAsync();
            RefreshGroupFilterOptions();
            ApplyLibraryFilter();
            UpdateLibraryListVisibility();
        }
        catch (Exception exception)
        {
            await App.CurrentApp.Logger.ErrorAsync("library.load_failed", "Could not load instance profiles.", exception);
            ShowError("无法读取实例库", "请检查本地数据目录，并查看 Sakana 日志。", exception.Message);
        }
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.Dispose();
    }

    private void MoreActions_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not Guid profileId)
        {
            return;
        }

        var profile = ViewModel.Profiles.FirstOrDefault(item => item.Id == profileId);
        if (profile is null)
        {
            return;
        }

        var menu = new MenuFlyout();
        MenuFlyoutItem AddItem(string label, RoutedEventHandler handler, bool isEnabled = true)
        {
            var item = new MenuFlyoutItem { Text = label, Tag = profileId, IsEnabled = isEnabled };
            item.Click += handler;
            menu.Items.Add(item);
            return item;
        }

        var busy = profile.IsRunning || profile.IsLaunching || profile.ProfileState == InstanceProfileState.Installing;
        AddItem("分组、标签与图标…", OrganizeProfile_Click, !busy);
        if (profile.ProfileState == InstanceProfileState.Installing)
        {
            AddItem("取消安装", CancelInstall_Click);
        }
        else if (profile.ProfileState is InstanceProfileState.ProfileOnly or InstanceProfileState.Cancelled or InstanceProfileState.Failed)
        {
            AddItem(profile.ProfileState == InstanceProfileState.ProfileOnly ? "安装游戏文件" : "重新安装", InstallProfile_Click);
        }
        else if (profile.ProfileState == InstanceProfileState.Installed)
        {
            AddItem("世界与服务器…", ManageWorldsAndServers_Click, !busy);
            AddItem("校验 / 修复", RepairProfile_Click, !busy);
            AddItem("导出 Modrinth 整合包…", ExportModrinthPack_Click, !busy);
            if (!string.IsNullOrWhiteSpace(profile.ModrinthProjectId))
            {
                AddItem("检查 Modrinth 更新…", UpdateModrinthPack_Click, !busy);
            }

            menu.Items.Add(new MenuFlyoutSeparator());
            if (profile.SupportsModManagement)
            {
                AddItem("模组", ManageMods_Click, !busy);
            }

            AddItem("内容包管理", ManagePacks_Click, !busy);
            menu.Items.Add(new MenuFlyoutSeparator());
            AddItem("启动设置", LaunchSettings_Click, !busy);
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        AddItem("打开实例文件夹", OpenInstanceFolder_Click);
        AddItem("复制实例…", DuplicateProfile_Click, !busy);
        AddItem("重命名实例…", RenameProfile_Click, !busy);
        var deleteItem = AddItem("删除实例和文件…", DeleteProfile_Click, !busy);
        if (busy)
        {
            ToolTipService.SetToolTip(deleteItem, "请先取消安装或停止游戏，再删除实例。");
        }

        menu.ShowAt(button);
    }

    private void LibrarySearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updatingLibraryFilters) return;
        ApplyLibraryFilter();
        UpdateLibraryListVisibility();
    }

    private void LibraryFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingLibraryFilters || !IsLoaded) return;
        ApplyLibraryFilter();
        UpdateLibraryListVisibility();
    }

    private void ClearLibraryFilters_Click(object sender, RoutedEventArgs e)
    {
        _updatingLibraryFilters = true;
        try
        {
            LibrarySearchBox.Text = string.Empty;
            GroupFilterComboBox.SelectedIndex = 0;
            LoaderFilterComboBox.SelectedIndex = 0;
            StateFilterComboBox.SelectedIndex = 0;
            SortFilterComboBox.SelectedIndex = 0;
        }
        finally
        {
            _updatingLibraryFilters = false;
        }

        ApplyLibraryFilter();
        UpdateLibraryListVisibility();
    }

    private void ApplyLibraryFilter()
    {
        var selectedGroup = (GroupFilterComboBox.SelectedItem as ComboBoxItem)?.Tag as string;
        var loader = (LoaderFilterComboBox.SelectedItem as ComboBoxItem)?.Tag as string;
        var state = (StateFilterComboBox.SelectedItem as ComboBoxItem)?.Tag as string;
        var sort = (SortFilterComboBox.SelectedItem as ComboBoxItem)?.Tag as string;
        ViewModel.ApplyLibraryFilter(LibrarySearchBox.Text, selectedGroup, loader, state, sort);
    }

    private void RefreshGroupFilterOptions(string? preferredGroup = null)
    {
        var currentGroup = preferredGroup ?? (GroupFilterComboBox.SelectedItem as ComboBoxItem)?.Tag as string;
        _updatingLibraryFilters = true;
        try
        {
            GroupFilterComboBox.Items.Clear();
            GroupFilterComboBox.Items.Add(new ComboBoxItem { Content = "全部实例", Tag = null });
            foreach (var group in ViewModel.GetGroupNames())
            {
                GroupFilterComboBox.Items.Add(new ComboBoxItem { Content = group, Tag = group });
            }

            var match = GroupFilterComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag as string, currentGroup, StringComparison.OrdinalIgnoreCase));
            GroupFilterComboBox.SelectedItem = match ?? GroupFilterComboBox.Items[0];
        }
        finally
        {
            _updatingLibraryFilters = false;
        }
    }

    private void UpdateLibraryListVisibility()
    {
        EmptyState.Visibility = ViewModel.HasProfiles ? Visibility.Collapsed : Visibility.Visible;
        OrganizationToolbar.Visibility = ViewModel.HasProfiles ? Visibility.Visible : Visibility.Collapsed;
        ProfileItems.Visibility = ViewModel.VisibleProfiles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NoResultsState.Visibility = ViewModel.HasProfiles && ViewModel.VisibleProfiles.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        LibraryCountText.Text = ViewModel.VisibleProfiles.Count == ViewModel.Profiles.Count
            ? $"{ViewModel.Profiles.Count} 个实例"
            : $"显示 {ViewModel.VisibleProfiles.Count} / {ViewModel.Profiles.Count} 个实例";
    }

    private async void OrganizeProfile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Guid profileId }) return;
        var profile = ViewModel.Profiles.FirstOrDefault(item => item.Id == profileId);
        if (profile is null) return;

        var groupInput = new TextBox
        {
            Header = "分组",
            Text = profile.GroupName ?? string.Empty,
            PlaceholderText = "例如：生存、整合包、测试",
            MaxLength = 40
        };
        var tagsInput = new TextBox
        {
            Header = "标签",
            Text = string.Join("，", profile.Tags),
            PlaceholderText = "用逗号分隔，例如：轻量，联机，科技",
            MaxLength = 640
        };
        var iconLabel = new TextBlock
        {
            Text = profile.IconFileName is null ? "使用默认图标" : "已设置自定义图标",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["SakanaMutedBrush"]
        };
        string? selectedIconPath = null;
        var clearIcon = false;
        var chooseIconButton = new Button { Content = "选择图片…" };
        chooseIconButton.Click += async (_, _) =>
        {
            try
            {
                var picker = new FileOpenPicker(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(App.CurrentApp.WindowHandle));
                picker.FileTypeFilter.Add(".png");
                picker.FileTypeFilter.Add(".jpg");
                picker.FileTypeFilter.Add(".jpeg");
                picker.FileTypeFilter.Add(".bmp");
                var selected = await picker.PickSingleFileAsync();
                if (selected is null) return;
                selectedIconPath = selected.Path;
                clearIcon = false;
                iconLabel.Text = Path.GetFileName(selected.Path);
            }
            catch (Exception exception)
            {
                iconLabel.Text = $"选择图片失败：{exception.Message}";
            }
        };
        var resetIconButton = new Button { Content = "恢复默认图标" };
        resetIconButton.Click += (_, _) =>
        {
            selectedIconPath = null;
            clearIcon = true;
            iconLabel.Text = "保存后恢复默认图标";
        };
        var iconActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        iconActions.Children.Add(chooseIconButton);
        iconActions.Children.Add(resetIconButton);
        var iconPanel = new StackPanel { Spacing = 8 };
        iconPanel.Children.Add(new TextBlock { Text = "实例图标", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        iconPanel.Children.Add(iconLabel);
        iconPanel.Children.Add(iconActions);

        var content = new StackPanel { Spacing = 14, Width = 520 };
        content.Children.Add(groupInput);
        content.Children.Add(tagsInput);
        content.Children.Add(new TextBlock
        {
            Text = "标签最多 20 个，每个不超过 32 个字符。自定义图标会复制到此实例文件夹中。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["SakanaMutedBrush"]
        });
        content.Children.Add(iconPanel);

        var dialog = new ContentDialog
        {
            Title = $"整理实例 · {profile.Name}",
            Content = content,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            var tags = tagsInput.Text.Split([',', '，', ';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            await ViewModel.SetInstanceOrganizationAsync(profileId, groupInput.Text, tags);
            if (clearIcon || selectedIconPath is not null)
            {
                await ViewModel.SetProfileIconAsync(profileId, clearIcon ? null : selectedIconPath);
            }

            var selectedGroup = (GroupFilterComboBox.SelectedItem as ComboBoxItem)?.Tag as string;
            RefreshGroupFilterOptions(selectedGroup);
            ApplyLibraryFilter();
            UpdateLibraryListVisibility();
            RepairInfoBar.Severity = InfoBarSeverity.Success;
            RepairInfoBar.Title = "实例信息已保存";
            RepairInfoBar.Message = "分组、标签和图标已更新。";
            RepairInfoBar.IsOpen = true;
            ErrorInfoBar.IsOpen = false;
        }
        catch (Exception exception)
        {
            await App.CurrentApp.Logger.ErrorAsync("profile.organization_update_failed", "Could not update instance organization or icon.", exception);
            ShowError("无法保存实例信息", exception.Message, string.Empty);
        }
    }

    private async void ManageWorldsAndServers_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Guid profileId })
        {
            return;
        }

        var profile = ViewModel.Profiles.FirstOrDefault(item => item.Id == profileId);
        if (profile is null)
        {
            return;
        }

        try
        {
            var worlds = await ViewModel.ListMinecraftWorldsAsync(profileId);
            var servers = await ViewModel.ListServerBookmarksAsync(profileId);
            var worldList = new StackPanel { Spacing = 8 };
            var serverList = new StackPanel { Spacing = 8 };
            var serverNameInput = new TextBox { Header = "收藏名称", PlaceholderText = "例如：生存服", MaxLength = 80 };
            var serverAddressInput = new TextBox { Header = "服务器地址", PlaceholderText = "play.example.net:25565", MaxLength = 255 };
            var serverError = new TextBlock { Foreground = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["SystemFillColorCriticalBrush"], TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };

            var dialog = new ContentDialog
            {
                Title = $"{profile.Name} · 世界与服务器",
                PrimaryButtonText = "添加收藏",
                CloseButtonText = "完成",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            };

            void AddWorldRows()
            {
                worldList.Children.Clear();
                if (worlds.Count == 0)
                {
                    worldList.Children.Add(new TextBlock
                    {
                        Text = "此实例还没有世界。先启动 Minecraft 创建世界，之后可在这里打开存档目录或直接快速启动。",
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["SakanaMutedBrush"]
                    });
                    return;
                }

                foreach (var worldName in worlds)
                {
                    var row = new Grid { ColumnSpacing = 12 };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var worldLabel = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
                    worldLabel.Children.Add(new TextBlock { Text = worldName, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
                    worldLabel.Children.Add(new TextBlock { Text = "Minecraft 存档", FontSize = 12, Foreground = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["SakanaMutedBrush"] });
                    row.Children.Add(worldLabel);

                    var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
                    var openButton = new Button { Content = "打开文件夹", Tag = worldName };
                    openButton.Click += (_, _) => OpenWorldFolder(profileId, worldName);
                    var launchButton = new Button { Content = "快速启动", Tag = worldName, Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["AccentButtonStyle"] };
                    launchButton.Click += async (_, _) =>
                    {
                        dialog.Hide();
                        await LaunchProfileWithQuickPlayAsync(profileId, new MinecraftQuickPlayTarget(MinecraftQuickPlayKind.SingleplayerWorld, worldName));
                    };
                    actions.Children.Add(openButton);
                    actions.Children.Add(launchButton);
                    Grid.SetColumn(actions, 1);
                    row.Children.Add(actions);

                    var card = new Border { Padding = new Thickness(12), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["SakanaStrokeBrush"], Background = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["SakanaSurfaceBrush"], Child = row };
                    worldList.Children.Add(card);
                }
            }

            void AddServerRows()
            {
                serverList.Children.Clear();
                if (servers.Count == 0)
                {
                    serverList.Children.Add(new TextBlock
                    {
                        Text = "还没有收藏服务器。添加后可从这里直接快速启动。收藏列表属于此实例，不会修改 Minecraft 的 servers.dat。",
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["SakanaMutedBrush"]
                    });
                    return;
                }

                foreach (var server in servers)
                {
                    var row = new Grid { ColumnSpacing = 12 };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var serverLabel = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
                    serverLabel.Children.Add(new TextBlock { Text = server.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
                    serverLabel.Children.Add(new TextBlock { Text = server.Address, FontSize = 12, Foreground = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["SakanaMutedBrush"] });
                    row.Children.Add(serverLabel);

                    var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
                    var launchButton = new Button { Content = "快速启动", Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["AccentButtonStyle"] };
                    launchButton.Click += async (_, _) =>
                    {
                        dialog.Hide();
                        await LaunchProfileWithQuickPlayAsync(profileId, new MinecraftQuickPlayTarget(MinecraftQuickPlayKind.MultiplayerServer, server.Address));
                    };
                    var removeButton = new Button { Content = "移除" };
                    removeButton.Click += async (_, _) =>
                    {
                        try
                        {
                            await ViewModel.RemoveServerBookmarkAsync(profileId, server.Id);
                            servers = await ViewModel.ListServerBookmarksAsync(profileId);
                            AddServerRows();
                        }
                        catch (Exception exception)
                        {
                            serverError.Text = exception.Message;
                            serverError.Visibility = Visibility.Visible;
                        }
                    };
                    actions.Children.Add(launchButton);
                    actions.Children.Add(removeButton);
                    Grid.SetColumn(actions, 1);
                    row.Children.Add(actions);

                    var card = new Border { Padding = new Thickness(12), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["SakanaStrokeBrush"], Background = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["SakanaSurfaceBrush"], Child = row };
                    serverList.Children.Add(card);
                }
            }

            AddWorldRows();
            AddServerRows();

            var worldTab = new TabViewItem
            {
                Header = "世界",
                IsClosable = false,
                Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = worldList, Padding = new Thickness(0, 12, 0, 0) }
            };
            var serverForm = new StackPanel { Spacing = 8 };
            serverForm.Children.Add(serverNameInput);
            serverForm.Children.Add(serverAddressInput);

            var serverContent = new Grid { RowSpacing = 12, Margin = new Thickness(0, 12, 0, 0) };
            serverContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            serverContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            serverContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            serverContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var serverDescription = new TextBlock { Text = "保存常用地址，直接进入支持 Quick Play 的 Minecraft 版本。", TextWrapping = TextWrapping.Wrap, Foreground = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["SakanaMutedBrush"] };
            var serverListScrollViewer = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = serverList };
            serverContent.Children.Add(serverDescription);
            serverContent.Children.Add(serverForm);
            serverContent.Children.Add(serverError);
            serverContent.Children.Add(serverListScrollViewer);
            Grid.SetRow(serverForm, 1);
            Grid.SetRow(serverError, 2);
            Grid.SetRow(serverListScrollViewer, 3);
            var serverTab = new TabViewItem { Header = "服务器", IsClosable = false, Content = serverContent };
            var tabView = new TabView { IsAddTabButtonVisible = false, Width = 650, Height = 460 };
            tabView.TabItems.Add(worldTab);
            tabView.TabItems.Add(serverTab);
            void UpdatePrimaryAction()
            {
                var onServerTab = ReferenceEquals(tabView.SelectedItem, serverTab);
                dialog.PrimaryButtonText = onServerTab ? "添加收藏" : string.Empty;
                dialog.DefaultButton = onServerTab ? ContentDialogButton.Primary : ContentDialogButton.Close;
            }
            tabView.SelectionChanged += (_, _) => UpdatePrimaryAction();
            UpdatePrimaryAction();
            dialog.PrimaryButtonClick += async (_, args) =>
            {
                args.Cancel = true;
                var deferral = args.GetDeferral();
                try
                {
                    await ViewModel.AddServerBookmarkAsync(profileId, serverNameInput.Text, serverAddressInput.Text);
                    serverNameInput.Text = string.Empty;
                    serverAddressInput.Text = string.Empty;
                    serverError.Visibility = Visibility.Collapsed;
                    servers = await ViewModel.ListServerBookmarksAsync(profileId);
                    AddServerRows();
                }
                catch (Exception exception)
                {
                    serverError.Text = exception.Message;
                    serverError.Visibility = Visibility.Visible;
                }
                finally
                {
                    deferral.Complete();
                }
            };
            dialog.Content = tabView;
            await dialog.ShowAsync();
        }
        catch (Exception exception)
        {
            await App.CurrentApp.Logger.ErrorAsync("world_server.load_failed", "World and server management could not be opened.", exception);
            ShowError("无法打开世界与服务器", "请确认实例已安装，并查看 Sakana 日志。", exception.Message);
        }
    }

    private void OpenWorldFolder(Guid profileId, string worldName)
    {
        try
        {
            var path = Path.Combine(ViewModel.GetInstanceGameDirectory(profileId), "saves", worldName);
            if (!Directory.Exists(path))
            {
                throw new DirectoryNotFoundException("这个世界目录已不存在，请刷新实例列表后重试。");
            }

            var startInfo = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            startInfo.ArgumentList.Add(path);
            Process.Start(startInfo);
        }
        catch (Exception exception)
        {
            ShowError("无法打开世界文件夹", "请检查实例目录是否仍然存在。", exception.Message);
        }
    }

    private async Task LaunchProfileWithQuickPlayAsync(Guid profileId, MinecraftQuickPlayTarget target)
    {
        ErrorInfoBar.IsOpen = false;
        try
        {
            await ViewModel.LaunchProfileAsync(profileId, quickPlayTarget: target);
        }
        catch (MinecraftAccountRequiredException exception)
        {
            await App.CurrentApp.Logger.InformationAsync("profile.launch_needs_account", exception.Message);
            App.CurrentApp.RequestNavigation("accounts");
        }
        catch (MinecraftClientIdNotAuthorizedException exception)
        {
            await App.CurrentApp.Logger.ErrorAsync("profile.launch_client_id_not_authorized", "Quick Play requires an authorized Minecraft API client ID.", exception);
            App.CurrentApp.RequestNavigation("accounts");
        }
        catch (Exception exception)
        {
            await App.CurrentApp.Logger.ErrorAsync("profile.quick_play_failed", "Minecraft Quick Play could not be started.", exception);
            ShowError("快速启动失败", "请检查 Minecraft 版本、账户和服务器地址。", exception.Message);
        }
    }

    private async void DuplicateProfile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Guid profileId })
        {
            return;
        }

        var profile = ViewModel.Profiles.FirstOrDefault(item => item.Id == profileId);
        if (profile is null)
        {
            return;
        }

        const string suffix = " - 副本";
        var namePrefixLength = Math.Max(1, 64 - suffix.Length);
        var suggestedName = profile.Name.Length > namePrefixLength
            ? profile.Name[..namePrefixLength] + suffix
            : profile.Name + suffix;
        var nameInput = new TextBox
        {
            Header = "新实例名称",
            Text = suggestedName,
            MaxLength = 64
        };
        var details = new StackPanel { Spacing = 8 };
        details.Children.Add(nameInput);
        details.Children.Add(new TextBlock
        {
            Text = "会复制此实例的游戏文件、模组、世界存档、资源包、光影包、截图和启动设置。副本拥有独立文件夹；两个实例之后的改动互不影响。",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        });

        var dialog = new ContentDialog
        {
            Title = $"复制“{profile.Name}”",
            Content = details,
            PrimaryButtonText = "创建副本",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var progressBar = new ProgressBar { Minimum = 0, Maximum = 100, IsIndeterminate = true };
        var progressStatus = new TextBlock { Text = "正在准备复制…", TextWrapping = TextWrapping.Wrap };
        var progressDetails = new TextBlock
        {
            FontSize = 12,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["SakanaMutedBrush"],
            TextWrapping = TextWrapping.Wrap
        };
        var progressContent = new StackPanel { Spacing = 10 };
        progressContent.Children.Add(progressStatus);
        progressContent.Children.Add(progressBar);
        progressContent.Children.Add(progressDetails);
        var progressDialog = new ContentDialog
        {
            Title = "正在复制实例",
            Content = progressContent,
            CloseButtonText = "取消复制",
            XamlRoot = XamlRoot
        };
        using var cancellationSource = new CancellationTokenSource();
        var progressDialogClosed = false;
        var copyFinished = false;
        progressDialog.Closing += (_, _) =>
        {
            progressDialogClosed = true;
            if (!copyFinished)
            {
                cancellationSource.Cancel();
            }
        };
        var uiDispatcher = DispatcherQueue.GetForCurrentThread();
        var progress = new Progress<InstanceCopyProgress>(update => uiDispatcher?.TryEnqueue(() =>
        {
            progressStatus.Text = update.Status;
            if (update.TotalBytes > 0)
            {
                progressBar.IsIndeterminate = false;
                progressBar.Value = Math.Clamp(update.BytesCopied * 100d / update.TotalBytes, 0, 100);
                progressDetails.Text = $"{FormatFileSize(update.BytesCopied)} / {FormatFileSize(update.TotalBytes)} · {update.FilesCopied:N0} / {update.TotalFiles:N0} 个文件";
            }
            else if (update.TotalFiles > 0)
            {
                progressBar.IsIndeterminate = false;
                progressBar.Value = update.FilesCopied * 100d / update.TotalFiles;
                progressDetails.Text = $"{update.FilesCopied:N0} / {update.TotalFiles:N0} 个文件";
            }
            else
            {
                progressBar.IsIndeterminate = true;
                progressDetails.Text = string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(update.CurrentFile))
            {
                progressDetails.Text += $"\n{update.CurrentFile}";
            }
        }));

        try
        {
            var progressDialogOperation = progressDialog.ShowAsync();
            InstanceProfile duplicate;
            try
            {
                duplicate = await ViewModel.DuplicateProfileAsync(profileId, nameInput.Text, progress, cancellationSource.Token);
                copyFinished = true;
            }
            finally
            {
                if (!progressDialogClosed)
                {
                    copyFinished = true;
                    progressDialog.Hide();
                }

                await progressDialogOperation;
            }

            RefreshGroupFilterOptions();
            ApplyLibraryFilter();
            UpdateLibraryListVisibility();
            ErrorInfoBar.IsOpen = false;
            RepairInfoBar.Severity = InfoBarSeverity.Success;
            RepairInfoBar.Title = "实例副本已创建";
            RepairInfoBar.Message = $"“{duplicate.Name}”保存在 {ViewModel.GetInstanceGameDirectory(duplicate.Id)}。";
            RepairInfoBar.IsOpen = true;
            await App.CurrentApp.Logger.InformationAsync("profile.duplicated", $"Duplicated instance {profile.Name} as {duplicate.Name}.");
        }
        catch (OperationCanceledException) when (cancellationSource.IsCancellationRequested)
        {
            RepairInfoBar.Severity = InfoBarSeverity.Informational;
            RepairInfoBar.Title = "复制已取消";
            RepairInfoBar.Message = "未完成的副本已清理，原实例没有改动。";
            RepairInfoBar.IsOpen = true;
        }
        catch (ProfileNameInUseException)
        {
            ShowError("实例名称已存在", "请换一个名称再创建副本。", string.Empty);
            nameInput.Focus(FocusState.Programmatic);
        }
        catch (ArgumentException exception)
        {
            ShowError("实例名称无效", exception.Message, string.Empty);
            nameInput.Focus(FocusState.Programmatic);
        }
        catch (Exception exception)
        {
            await App.CurrentApp.Logger.ErrorAsync("profile.duplicate_failed", "The Minecraft instance could not be duplicated.", exception);
            ShowError("无法复制实例", "请检查磁盘空间后重试。原实例文件不会被修改。", exception.Message);
        }
    }

    private async void ExportModrinthPack_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Guid profileId }) return;
        var profile = ViewModel.Profiles.FirstOrDefault(item => item.Id == profileId);
        if (profile is null) return;

        try
        {
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(App.CurrentApp.WindowHandle);
            var picker = new FileSavePicker(windowId)
            {
                SuggestedFileName = profile.Name,
                DefaultFileExtension = ".mrpack",
                CommitButtonText = "导出整合包"
            };
            picker.FileTypeChoices.Add("Modrinth 整合包", new List<string> { ".mrpack" });
            var destination = await picker.PickSaveFileAsync();
            if (destination is null) return;

            using var cancellationSource = new CancellationTokenSource();
            var exportDialog = new ContentDialog
            {
                Title = "正在导出整合包",
                Content = new StackPanel
                {
                    Spacing = 10,
                    Children =
                    {
                        new TextBlock { Text = "正在生成可分享的 Modrinth .mrpack 文件…", TextWrapping = TextWrapping.Wrap },
                        new ProgressBar { IsIndeterminate = true },
                        new TextBlock
                        {
                            Text = "会包含实例配置、资源包和光影；世界存档、截图及账号设置不会导出。无法确认 Modrinth 来源的本地 JAR 会跳过。",
                            FontSize = 12,
                            TextWrapping = TextWrapping.Wrap,
                            Foreground = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["SakanaMutedBrush"]
                        }
                    }
                },
                CloseButtonText = "取消导出",
                XamlRoot = XamlRoot
            };
            var exportDialogClosed = false;
            var exportFinished = false;
            exportDialog.Closing += (_, _) =>
            {
                exportDialogClosed = true;
                if (!exportFinished) cancellationSource.Cancel();
            };

            try
            {
                var dialogOperation = exportDialog.ShowAsync();
                ModrinthModpackExportResult result;
                try
                {
                    result = await ViewModel.ExportModrinthPackAsync(profileId, destination.Path, cancellationSource.Token);
                    exportFinished = true;
                }
                finally
                {
                    if (!exportDialogClosed)
                    {
                        exportFinished = true;
                        exportDialog.Hide();
                    }

                    await dialogOperation;
                }

                RepairInfoBar.Severity = result.LocalModFilesOmitted > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success;
                RepairInfoBar.Title = result.LocalModFilesOmitted > 0 ? "整合包已导出，部分本地模组已跳过" : "整合包已导出";
                RepairInfoBar.Message = $"{destination.Path} · {result.ModrinthFilesIncluded} 个 Modrinth 文件，{result.OverrideFilesIncluded} 个配置/资源文件。未打包 {result.LocalModFilesOmitted} 个无法确认 Modrinth 来源的本地 JAR。";
                RepairInfoBar.IsOpen = true;
                await App.CurrentApp.Logger.InformationAsync("modpack.exported", $"Exported instance {profile.Name} to a Modrinth modpack with {result.ModrinthFilesIncluded} remote files and {result.OverrideFilesIncluded} overrides.");
            }
            catch (OperationCanceledException) when (cancellationSource.IsCancellationRequested)
            {
                RepairInfoBar.Severity = InfoBarSeverity.Informational;
                RepairInfoBar.Title = "整合包导出已取消";
                RepairInfoBar.Message = "没有覆盖目标文件。";
                RepairInfoBar.IsOpen = true;
            }
        }
        catch (Exception exception)
        {
            await App.CurrentApp.Logger.ErrorAsync("modpack.export_failed", "The instance could not be exported as a Modrinth modpack.", exception);
            ShowError("无法导出整合包", "请检查目标位置的磁盘空间，并查看 Sakana 日志。", exception.Message);
        }
    }

    private async void UpdateModrinthPack_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Guid profileId }) return;
        var profile = ViewModel.Profiles.FirstOrDefault(item => item.Id == profileId);
        if (profile is null || string.IsNullOrWhiteSpace(profile.ModrinthProjectId)) return;

        try
        {
            var versions = await ViewModel.GetSupportedModrinthModpackVersionsAsync(profile.ModrinthProjectId);
            var candidates = versions
                .Where(version => !string.Equals(version.VersionId, profile.ModrinthPackVersionId, StringComparison.Ordinal))
                .ToArray();
            if (candidates.Length == 0)
            {
                RepairInfoBar.Severity = InfoBarSeverity.Informational;
                RepairInfoBar.Title = "没有其他兼容版本";
                RepairInfoBar.Message = "Modrinth 暂未提供这个整合包的其他可安装 .mrpack 版本。";
                RepairInfoBar.IsOpen = true;
                return;
            }

            var versionPicker = new ComboBox
            {
                Header = "选择整合包版本",
                MinWidth = 360,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            foreach (var version in candidates)
            {
                var games = version.GameVersions.Count <= 3
                    ? string.Join(", ", version.GameVersions)
                    : $"{string.Join(", ", version.GameVersions.Take(3))} 等 {version.GameVersions.Count} 个游戏版本";
                var loaders = string.Join(", ", version.Loaders);
                versionPicker.Items.Add(new ComboBoxItem
                {
                    Content = $"{version.VersionNumber} · Minecraft {games} · {loaders} · {FormatFileSize(version.SizeBytes)}",
                    Tag = version
                });
            }
            versionPicker.SelectedIndex = 0;

            var selectionDialog = new ContentDialog
            {
                Title = $"检查“{profile.Name}”的 Modrinth 更新",
                Content = new StackPanel
                {
                    Spacing = 10,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = $"当前来源版本：{profile.ModrinthPackVersionNumber ?? "未知"}。选择一个新版本后，Sakana 会比较整合包文件和本地修改。",
                            TextWrapping = TextWrapping.Wrap
                        },
                        versionPicker
                    }
                },
                PrimaryButtonText = "下载并查看变更",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot
            };
            if (await selectionDialog.ShowAsync() != ContentDialogResult.Primary ||
                versionPicker.SelectedItem is not ComboBoxItem { Tag: ModrinthModRelease selectedVersion })
            {
                return;
            }

            var archivePath = await ViewModel.DownloadModrinthModpackVersionAsync(selectedVersion.VersionId);
            var stagingDirectory = Path.GetDirectoryName(archivePath);
            try
            {
                var preview = await ViewModel.PreviewModrinthPackUpdateAsync(profileId, archivePath, selectedVersion.VersionNumber);
                var suggestedName = CreateUpdatedInstanceName(profile.Name, selectedVersion.VersionNumber);
                var nameInput = new TextBox
                {
                    Header = "更新副本名称（仅创建更新副本时使用）",
                    Text = suggestedName,
                    MaxLength = 40
                };
                var summary = new TextBlock
                {
                    Text = $"{preview.CurrentVersion} → {preview.NewVersion}\nMinecraft {preview.CurrentMinecraftVersion} → {preview.NewMinecraftVersion} · {GetLoaderLabel(preview.CurrentLoader)} → {GetLoaderLabel(preview.NewLoader)}\n新增 {preview.AddedCount} · 更新 {preview.UpdatedCount} · 移除 {preview.RemovedCount} · 旧实例有本地修改 {preview.LocallyChangedCount}",
                    TextWrapping = TextWrapping.Wrap
                };
                var details = new StackPanel { Spacing = 10 };
                details.Children.Add(nameInput);
                details.Children.Add(summary);
                if (preview.SupportsInPlaceUpdate)
                {
                    details.Children.Add(new TextBlock
                    {
                        Text = "就地更新会先暂存和校验文件。默认保留本地修改、世界存档、截图和游戏设置；整合包移除的文件也会留在实例中。",
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["SakanaMutedBrush"]
                    });
                }
                else
                {
                    details.Children.Add(new TextBlock
                    {
                        Text = "此版本更改了 Minecraft 或加载器版本，不能就地更新；创建更新副本可以保留当前实例。",
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["SakanaMutedBrush"]
                    });
                }

                var conflictChoices = new List<CheckBox>();
                if (preview.SupportsInPlaceUpdate && preview.ConflictCount > 0)
                {
                    details.Children.Add(new TextBlock
                    {
                        Text = $"本地冲突文件（{preview.ConflictCount}）· 勾选后才会覆盖",
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                    });
                    var conflictsPanel = new StackPanel { Spacing = 4 };
                    foreach (var change in preview.Changes.Where(change => change.RequiresConflictChoice))
                    {
                        var choice = new CheckBox
                        {
                            Content = new TextBlock
                            {
                                Text = $"{change.Path}{(change.IsPersonalData ? " · 个人数据" : " · 本地修改")}",
                                TextTrimming = TextTrimming.CharacterEllipsis
                            },
                            Tag = change.Path,
                            IsChecked = false
                        };
                        conflictChoices.Add(choice);
                        conflictsPanel.Children.Add(choice);
                    }

                    details.Children.Add(new ScrollViewer
                    {
                        Content = conflictsPanel,
                        MaxHeight = 180,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                    });
                }

                if (preview.Changes.Count > 0)
                {
                    var changesPanel = new StackPanel { Spacing = 4 };
                    foreach (var change in preview.Changes.Take(120))
                    {
                        var label = change.Kind switch
                        {
                            ModrinthPackChangeKind.Added => "新增",
                            ModrinthPackChangeKind.Updated => "更新",
                            ModrinthPackChangeKind.Removed => "移除",
                            _ => "本地改动"
                        };
                        var localLabel = change.IsPersonalData
                            ? " · 个人数据"
                            : change.Kind == ModrinthPackChangeKind.Added && change.HasLocalFile
                                ? " · 旧实例存在同名文件"
                            : change.HasLocalChanges
                                ? " · 旧实例中已修改"
                                : string.Empty;
                        changesPanel.Children.Add(new TextBlock
                        {
                            Text = $"{label} · {change.Path}{localLabel}",
                            TextTrimming = TextTrimming.CharacterEllipsis,
                            FontSize = 12
                        });
                    }

                    if (preview.Changes.Count > 120)
                    {
                        changesPanel.Children.Add(new TextBlock
                        {
                            Text = $"另有 {preview.Changes.Count - 120} 项未展开。",
                            FontSize = 12
                        });
                    }

                    details.Children.Add(new ScrollViewer
                    {
                        Content = changesPanel,
                        MaxHeight = 260,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                    });
                }

                details.Children.Add(new TextBlock
                {
                    Text = "就地更新不会删除旧版文件，也不会迁移世界存档、截图或其他个人数据。未勾选的本地冲突会保留原样。",
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["SakanaMutedBrush"]
                });

                var previewDialog = new ContentDialog
                {
                    Title = "整合包变更预览",
                    Content = new ScrollViewer
                    {
                        Content = details,
                        MaxHeight = 520,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                    },
                    PrimaryButtonText = preview.SupportsInPlaceUpdate ? "就地更新此实例" : "创建更新副本",
                    SecondaryButtonText = preview.SupportsInPlaceUpdate ? "创建更新副本" : null,
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = XamlRoot
                };
                var previewResult = await previewDialog.ShowAsync();
                if (previewResult == ContentDialogResult.Primary && preview.SupportsInPlaceUpdate)
                {
                    var selectedConflicts = conflictChoices
                        .Where(choice => choice.IsChecked == true && choice.Tag is string)
                        .Select(choice => (string)choice.Tag!)
                        .ToArray();
                    if (selectedConflicts.Length > 0)
                    {
                        var confirmation = new ContentDialog
                        {
                            Title = "确认覆盖本地文件",
                            Content = $"你选择覆盖 {selectedConflicts.Length} 个本地修改文件。Sakana 会先备份这些文件；如果更新失败会尝试自动恢复。未勾选的冲突和整合包移除的文件会保留。",
                            PrimaryButtonText = "覆盖所选文件并更新",
                            CloseButtonText = "返回",
                            DefaultButton = ContentDialogButton.Close,
                            XamlRoot = XamlRoot
                        };
                        if (await confirmation.ShowAsync() != ContentDialogResult.Primary) return;
                    }

                    var updateProgressDialog = new ContentDialog
                    {
                        Title = "正在更新整合包",
                        Content = new StackPanel
                        {
                            Spacing = 12,
                            Children =
                            {
                                new ProgressRing { IsActive = true, Width = 32, Height = 32 },
                                new TextBlock
                                {
                                    Text = "正在验证并暂存文件，然后安全应用更新。大型整合包可能需要一些时间。",
                                    TextWrapping = TextWrapping.Wrap
                                }
                            }
                        },
                        XamlRoot = XamlRoot
                    };
                    var progressDialogOperation = updateProgressDialog.ShowAsync();
                    await Task.Yield();
                    ModrinthModpackUpdateResult updateResult;
                    try
                    {
                        updateResult = await ViewModel.ApplyModrinthPackUpdateAsync(
                            profileId,
                            archivePath,
                            selectedVersion.VersionId,
                            selectedVersion.VersionNumber,
                            selectedConflicts);
                    }
                    finally
                    {
                        updateProgressDialog.Hide();
                        await progressDialogOperation;
                    }
                    RepairInfoBar.Severity = InfoBarSeverity.Success;
                    RepairInfoBar.Title = "整合包已就地更新";
                    RepairInfoBar.Message = $"新增 {updateResult.InstalledFiles} 个文件，更新 {updateResult.UpdatedFiles} 个文件；保留本地文件 {updateResult.PreservedLocalFiles} 个，保留整合包移除文件 {updateResult.RetainedRemovedFiles} 个。";
                    RepairInfoBar.IsOpen = true;
                }
                else if (previewResult == ContentDialogResult.Secondary ||
                    (previewResult == ContentDialogResult.Primary && !preview.SupportsInPlaceUpdate))
                {
                    await ImportModrinthPackArchiveAsync(
                        archivePath,
                        profile.ModrinthProjectId,
                        selectedVersion.VersionId,
                        selectedVersion.VersionNumber,
                        nameInput.Text);
                }
            }
            finally
            {
                if (stagingDirectory is not null && Directory.Exists(stagingDirectory))
                {
                    Directory.Delete(stagingDirectory, recursive: true);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The user can cancel the version picker, preview, or download.
        }
        catch (Exception exception)
        {
            await App.CurrentApp.Logger.ErrorAsync("modpack.update_failed", "The Modrinth pack update could not be completed.", exception);
            ShowError("无法完成整合包更新", "更新失败后 Sakana 会尝试恢复原文件。请查看错误详情；若提示恢复目录，请先保留该目录。", exception.Message);
        }
    }

    private static string CreateUpdatedInstanceName(string currentName, string versionNumber)
    {
        var suffix = $" - {versionNumber}";
        if (suffix.Length >= 40) suffix = suffix[..39];
        var prefixLength = Math.Max(1, 40 - suffix.Length);
        var prefix = currentName.Length > prefixLength ? currentName[..prefixLength] : currentName;
        return prefix + suffix;
    }

    private static string GetLoaderLabel(MinecraftLoaderType loaderType) => loaderType switch
    {
        MinecraftLoaderType.Fabric => "Fabric",
        MinecraftLoaderType.Quilt => "Quilt",
        MinecraftLoaderType.Forge => "Forge",
        MinecraftLoaderType.NeoForge => "NeoForge",
        _ => "原版"
    };

    private async void RenameProfile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Guid profileId })
        {
            return;
        }

        var profile = ViewModel.Profiles.FirstOrDefault(item => item.Id == profileId);
        if (profile is null)
        {
            return;
        }

        var nameInput = new TextBox
        {
            Header = "实例名称",
            Text = profile.Name,
            MaxLength = 64
        };
        var dialog = new ContentDialog
        {
            Title = "重命名实例",
            Content = nameInput,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            await ViewModel.RenameProfileAsync(profileId, nameInput.Text);
            ApplyLibraryFilter();
            UpdateLibraryListVisibility();
            RepairInfoBar.Severity = InfoBarSeverity.Success;
            RepairInfoBar.Title = "实例已重命名";
            RepairInfoBar.Message = $"新名称：{nameInput.Text.Trim()}";
            RepairInfoBar.IsOpen = true;
            ErrorInfoBar.IsOpen = false;
            await App.CurrentApp.Logger.InformationAsync("profile.renamed", $"Renamed instance {profile.Name} to {nameInput.Text.Trim()}.");
        }
        catch (Exception exception)
        {
            await App.CurrentApp.Logger.ErrorAsync("profile.rename_failed", "The Minecraft instance could not be renamed.", exception);
            ShowError("无法重命名实例", exception.Message, string.Empty);
        }
    }

    private void OpenInstanceFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Guid profileId })
        {
            return;
        }

        try
        {
            var instanceGameDirectory = ViewModel.GetInstanceGameDirectory(profileId);
            Directory.CreateDirectory(instanceGameDirectory);
            var startInfo = new ProcessStartInfo { FileName = "explorer.exe", UseShellExecute = true };
            startInfo.ArgumentList.Add(instanceGameDirectory);
            Process.Start(startInfo);
        }
        catch (Exception exception)
        {
            ShowError("无法打开实例文件夹", "请检查实例数据目录是否可访问。", exception.Message);
        }
    }

    private async void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Guid profileId })
        {
            return;
        }

        var profile = ViewModel.Profiles.FirstOrDefault(item => item.Id == profileId);
        if (profile is null)
        {
            return;
        }

        var directory = ViewModel.GetInstanceGameDirectory(profileId);
        var details = new StackPanel { Spacing = 8 };
        details.Children.Add(new TextBlock
        {
            Text = $"确定删除“{profile.Name}”及其实例文件吗？这会删除该实例的游戏文件、模组、配置、世界存档、资源包、光影包和存档内的数据包。",
            TextWrapping = TextWrapping.Wrap
        });
        details.Children.Add(new TextBlock
        {
            Text = $"内容目录：{directory}\nSakana 的共享下载缓存会保留；导入时复制到 Sakana 的整合包文件会删除，原始文件不会改动。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12
        });

        var dialog = new ContentDialog
        {
            Title = "删除 Minecraft 实例？",
            Content = details,
            PrimaryButtonText = "删除实例和文件",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            var filesDeleted = await ViewModel.DeleteProfileAsync(profileId);
            RefreshGroupFilterOptions();
            ApplyLibraryFilter();
            UpdateLibraryListVisibility();
            ErrorInfoBar.IsOpen = false;
            RepairInfoBar.Severity = filesDeleted ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
            RepairInfoBar.Title = filesDeleted ? "实例和文件已删除" : "实例已删除，文件清理稍后重试";
            RepairInfoBar.Message = filesDeleted
                ? "实例已从列表移除，实例目录和导入副本已删除；共享下载缓存保留供其他实例复用。"
                : "实例已从列表移除。部分文件正被占用，Sakana 会在下次启动时重试清理。";
            RepairInfoBar.IsOpen = true;
            await App.CurrentApp.Logger.InformationAsync(
                "profile.deleted",
                filesDeleted
                    ? $"Deleted instance {profile.Name} and its instance files."
                    : $"Removed instance {profile.Name}; some files remain queued for cleanup.");
        }
        catch (Exception exception)
        {
            await App.CurrentApp.Logger.ErrorAsync("profile.delete_failed", "The Minecraft instance could not be deleted.", exception);
            ShowError("无法删除实例", "请先取消安装或停止正在运行的 Minecraft，然后重试。", exception.Message);
        }
    }

    private async void ImportModrinthPack_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ImportModrinthPackAsync();
        }
        catch (OperationCanceledException)
        {
            // The native picker may report cancellation when the user dismisses it.
        }
        catch (Exception exception)
        {
            var details = exception is COMException comException
                ? $"HRESULT 0x{unchecked((uint)comException.HResult):X8}"
                : exception.Message;
            await App.CurrentApp.Logger.ErrorAsync(
                "modpack.import_flow_failed",
                $"The modpack import flow could not be opened. {details}",
                exception);
            ShowError("无法打开整合包导入", "文件选择器或导入窗口发生错误；Sakana 仍可继续使用。", details);
        }
    }

    private async void ManageMods_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Guid profileId })
        {
            return;
        }

        var profile = ViewModel.Profiles.FirstOrDefault(item => item.Id == profileId);
        if (profile is null)
        {
            return;
        }

        try
        {
            await ShowLocalModsDialogAsync(profileId, profile.Name);
        }
        catch (Exception exception)
        {
            await App.CurrentApp.Logger.ErrorAsync("mods.manager_failed", "The local mod manager could not be opened.", exception);
            ShowError("无法打开模组管理", "Sakana 无法读取这个实例的模组目录。", exception.Message);
        }
    }

    private async void ManagePacks_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Guid profileId })
        {
            return;
        }

        var profile = ViewModel.Profiles.FirstOrDefault(item => item.Id == profileId);
        if (profile is null)
        {
            return;
        }

        try
        {
            await ShowLocalPacksDialogAsync(profileId, profile.Name, profile.VersionId);
        }
        catch (Exception exception)
        {
            await App.CurrentApp.Logger.ErrorAsync("packs.manager_failed", "The local resource/shader pack manager could not be opened.", exception);
            ShowError("无法打开内容包管理", "Sakana 无法读取这个实例的内容目录。", exception.Message);
        }
    }

    private async Task ShowLocalPacksDialogAsync(Guid profileId, string profileName, string minecraftVersion)
    {
        var kindInput = new ComboBox { Width = 145 };
        kindInput.Items.Add(new ComboBoxItem { Content = "资源包", Tag = MinecraftLocalPackKind.ResourcePack });
        kindInput.Items.Add(new ComboBoxItem { Content = "光影包", Tag = MinecraftLocalPackKind.ShaderPack });
        kindInput.Items.Add(new ComboBoxItem { Content = "数据包", Tag = MinecraftLocalPackKind.DataPack });
        kindInput.SelectedIndex = 0;
        var worldInput = new ComboBox { Width = 170, PlaceholderText = "选择世界", Visibility = Visibility.Collapsed };
        var addButton = new Button { Content = "＋ 添加 ZIP", Width = 112, HorizontalAlignment = HorizontalAlignment.Left };
        var browseButton = new Button { Content = "从 Modrinth 下载", Width = 148, HorizontalAlignment = HorizontalAlignment.Left };
        var queryInput = new TextBox { PlaceholderText = "搜索名称；留空可浏览热门内容", MaxLength = 100, MinWidth = 250 };
        var searchButton = new Button { Content = "搜索", MinWidth = 76 };
        var sortInput = CreateModrinthSortPicker();
        var loadMoreButton = new Button { Content = "加载更多", Visibility = Visibility.Collapsed };
        var searchStatus = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        var searchResults = new StackPanel { Spacing = 8 };
        var searchPanel = new StackPanel { Spacing = 8, Visibility = Visibility.Collapsed };
        var summaryText = new TextBlock { FontSize = 12, Foreground = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["SakanaMutedBrush"] };
        var feedbackText = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        var packsPanel = new StackPanel { Spacing = 8 };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock
        {
            Text = $"内容保存在“{profileName}”自己的游戏目录中（Minecraft {minecraftVersion}）。光影包需要安装 Iris 等兼容渲染器；数据包安装到所选世界并在世界的数据包列表中启用。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12
        });
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        controls.Children.Add(kindInput);
        controls.Children.Add(worldInput);
        controls.Children.Add(addButton);
        content.Children.Add(controls);
        var downloadRow = new StackPanel { Orientation = Orientation.Horizontal };
        downloadRow.Children.Add(browseButton);
        content.Children.Add(downloadRow);
        var searchControls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        searchControls.Children.Add(queryInput);
        searchControls.Children.Add(sortInput);
        searchControls.Children.Add(searchButton);
        searchPanel.Children.Add(new TextBlock
        {
            Text = $"Modrinth · Minecraft {minecraftVersion}",
            FontSize = 12
        });
        searchPanel.Children.Add(searchControls);
        searchPanel.Children.Add(searchStatus);
        searchPanel.Children.Add(loadMoreButton);
        searchPanel.Children.Add(new ScrollViewer
        {
            Content = searchResults,
            MaxHeight = 230,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        });
        content.Children.Add(searchPanel);
        content.Children.Add(summaryText);
        content.Children.Add(feedbackText);
        content.Children.Add(new ScrollViewer
        {
            Content = packsPanel,
            MaxHeight = 380,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        });

        var dialog = new ContentDialog
        {
            Title = $"{profileName} · 内容包管理",
            Content = content,
            CloseButtonText = "关闭",
            XamlRoot = XamlRoot
        };
        var searchOffset = 0;
        var searchTotalHits = 0;
        var searchInProgress = false;
        string? lastSearchQuery = null;
        string? lastSortOrder = null;
        ModrinthContentKind? lastKind = null;
        var suppressWorldSelectionChanged = false;

        MinecraftLocalPackKind GetKind() => (kindInput.SelectedItem as ComboBoxItem)?.Tag is MinecraftLocalPackKind kind
            ? kind
            : MinecraftLocalPackKind.ResourcePack;

        ModrinthContentKind GetModrinthKind() => GetKind() switch
        {
            MinecraftLocalPackKind.ResourcePack => ModrinthContentKind.ResourcePack,
            MinecraftLocalPackKind.ShaderPack => ModrinthContentKind.ShaderPack,
            _ => ModrinthContentKind.DataPack
        };

        string GetContentKindName() => GetKind() switch
        {
            MinecraftLocalPackKind.ResourcePack => "资源包",
            MinecraftLocalPackKind.ShaderPack => "光影包",
            MinecraftLocalPackKind.DataPack => "数据包",
            _ => "内容包"
        };

        string? GetSelectedWorldName() => worldInput.SelectedItem is ComboBoxItem { Tag: string folderName }
            ? folderName
            : null;

        async Task RefreshWorldChoicesAsync()
        {
            suppressWorldSelectionChanged = true;
            worldInput.Items.Clear();
            try
            {
                foreach (var worldName in await ViewModel.ListMinecraftWorldsAsync(profileId))
                {
                    worldInput.Items.Add(new ComboBoxItem { Content = worldName, Tag = worldName });
                }

                worldInput.SelectedIndex = worldInput.Items.Count > 0 ? 0 : -1;
            }
            finally
            {
                suppressWorldSelectionChanged = false;
            }
        }

        async Task RefreshPacksAsync()
        {
            var kind = GetKind();
            packsPanel.Children.Clear();
            var worldName = kind == MinecraftLocalPackKind.DataPack ? GetSelectedWorldName() : null;
            if (kind == MinecraftLocalPackKind.DataPack && worldName is null)
            {
                summaryText.Text = "此实例尚未发现世界";
                return;
            }

            var packs = await ViewModel.ListLocalPacksAsync(profileId, kind, worldName);
            summaryText.Text = packs.Count == 0
                ? $"此{GetContentKindName()}尚未添加"
                : $"{GetContentKindName()} · {packs.Count} 个";
            if (packs.Count == 0)
            {
                var emptyHelpText = kind switch
                {
                    MinecraftLocalPackKind.ResourcePack => string.Empty,
                    MinecraftLocalPackKind.ShaderPack => string.Empty,
                    _ => "数据包保存在所选世界的 datapacks 目录中，可在 Minecraft 世界的数据包列表中启用。"
                };
                if (!string.IsNullOrWhiteSpace(emptyHelpText))
                {
                    packsPanel.Children.Add(new TextBlock
                    {
                        Text = emptyHelpText,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["SakanaMutedBrush"]
                    });
                }

                return;
            }

            foreach (var pack in packs)
            {
                var toggle = new ToggleSwitch
                {
                    Header = pack.DisplayName,
                    IsOn = pack.IsEnabled,
                    OnContent = "启用",
                    OffContent = "已停用",
                    MinWidth = 160
                };
                var details = new List<string>();
                if (!string.IsNullOrWhiteSpace(pack.Details)) details.Add(pack.Details);
                details.Add(FormatFileSize(pack.SizeBytes));
                details.Add(pack.FileName);
                var detailsText = new TextBlock
                {
                    Text = string.Join(" · ", details),
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["SakanaMutedBrush"]
                };
                var removeButton = new Button { Content = "移除", VerticalAlignment = VerticalAlignment.Center };
                var row = new Grid { ColumnSpacing = 12, Margin = new Thickness(0, 0, 0, 8) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var info = new StackPanel { Spacing = 2 };
                info.Children.Add(toggle);
                info.Children.Add(detailsText);
                row.Children.Add(info);
                Grid.SetColumn(removeButton, 1);
                row.Children.Add(removeButton);
                packsPanel.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(10),
                    BorderThickness = new Thickness(1),
                    BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["SakanaStrokeBrush"],
                    Padding = new Thickness(10),
                    Child = row
                });

                toggle.Toggled += async (_, _) =>
                {
                    toggle.IsEnabled = false;
                    try
                    {
                        await ViewModel.SetLocalPackEnabledAsync(profileId, kind, pack.FileName, toggle.IsOn, worldName);
                        feedbackText.Text = $"已{(toggle.IsOn ? "启用" : "停用")} {pack.DisplayName}。";
                        await RefreshPacksAsync();
                    }
                    catch (Exception exception)
                    {
                        await App.CurrentApp.Logger.ErrorAsync("packs.toggle_failed", "A resource or shader pack could not be enabled or disabled.", exception);
                        feedbackText.Text = exception.Message;
                        await RefreshPacksAsync();
                    }
                };

                removeButton.Click += async (_, _) =>
                {
                    removeButton.IsEnabled = false;
                    try
                    {
                        await ViewModel.RemoveLocalPackAsync(profileId, kind, pack.FileName, worldName);
                        feedbackText.Text = $"已移除 {pack.DisplayName}。";
                        await RefreshPacksAsync();
                    }
                    catch (Exception exception)
                    {
                        await App.CurrentApp.Logger.ErrorAsync("packs.remove_failed", "A resource or shader pack could not be removed.", exception);
                        feedbackText.Text = exception.Message;
                        removeButton.IsEnabled = true;
                    }
                };
            }
        }

        kindInput.SelectionChanged += async (_, _) =>
        {
            try
            {
                feedbackText.Text = string.Empty;
                searchResults.Children.Clear();
                searchOffset = 0;
                searchTotalHits = 0;
                loadMoreButton.Visibility = Visibility.Collapsed;
                searchStatus.Text = string.Empty;
                var isDataPack = GetKind() == MinecraftLocalPackKind.DataPack;
                worldInput.Visibility = isDataPack ? Visibility.Visible : Visibility.Collapsed;
                if (isDataPack)
                {
                    await RefreshWorldChoicesAsync();
                }
                await RefreshPacksAsync();
            }
            catch (Exception exception)
            {
                feedbackText.Text = exception.Message;
            }
        };

        worldInput.SelectionChanged += async (_, _) =>
        {
            if (suppressWorldSelectionChanged || GetKind() != MinecraftLocalPackKind.DataPack)
            {
                return;
            }

            try
            {
                await RefreshPacksAsync();
            }
            catch (Exception exception)
            {
                feedbackText.Text = exception.Message;
            }
        };

        browseButton.Click += (_, _) =>
        {
            searchPanel.Visibility = searchPanel.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
            if (searchPanel.Visibility == Visibility.Visible)
            {
                queryInput.Focus(FocusState.Programmatic);
            }
        };

        async Task SearchContentAsync(bool loadMore)
        {
            if (searchInProgress)
            {
                return;
            }

            var kind = GetModrinthKind();
            var sortOrder = GetModrinthSortOrder(sortInput);
            if (loadMore && (!string.Equals(lastSearchQuery, queryInput.Text, StringComparison.Ordinal) ||
                             !string.Equals(lastSortOrder, sortOrder, StringComparison.Ordinal) ||
                             lastKind != kind))
            {
                loadMore = false;
            }

            searchInProgress = true;
            searchButton.IsEnabled = false;
            loadMoreButton.IsEnabled = false;
            if (!loadMore)
            {
                searchOffset = 0;
                searchTotalHits = 0;
                lastSearchQuery = queryInput.Text;
                lastSortOrder = sortOrder;
                lastKind = kind;
                searchResults.Children.Clear();
                loadMoreButton.Visibility = Visibility.Collapsed;
            }

            searchStatus.Text = loadMore
                ? $"正在加载更多 Modrinth {GetContentKindName()}…"
                : $"正在搜索兼容的 Modrinth {GetContentKindName()}…";
            try
            {
                var page = await ViewModel.SearchModrinthContentAsync(profileId, kind, queryInput.Text, sortOrder, searchOffset);
                var projects = page.Projects;
                searchOffset = Math.Min(page.Offset + page.Limit, page.TotalHits);
                searchTotalHits = page.TotalHits;
                searchStatus.Text = searchTotalHits == 0
                    ? $"没有找到适配 Minecraft {minecraftVersion} 的{GetContentKindName()}。试试其他关键词。"
                    : $"找到 {searchTotalHits:N0} 个项目，当前显示 {Math.Min(searchOffset, searchTotalHits):N0} 个。选择版本后即可安装到此实例。";
                if (searchTotalHits > 10000 && searchOffset >= 10000)
                {
                    searchStatus.Text += " Modrinth 的单次搜索最多可浏览 10,000 项。";
                }
                loadMoreButton.Visibility = searchOffset < searchTotalHits && searchOffset < 10000 ? Visibility.Visible : Visibility.Collapsed;

                foreach (var project in projects)
                {
                    var projectPanel = new StackPanel { Spacing = 4 };
                    projectPanel.Children.Add(CreateModrinthProjectSummary(project, minecraftVersion));

                    var versionInput = new ComboBox
                    {
                        PlaceholderText = "选择版本",
                        MinWidth = 220,
                        Visibility = Visibility.Collapsed
                    };
                    var chooseVersionButton = new Button { Content = "选择版本" };
                    var installButton = new Button { Content = "安装", Visibility = Visibility.Collapsed };
                    var projectActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                    projectActions.Children.Add(chooseVersionButton);
                    projectActions.Children.Add(versionInput);
                    projectActions.Children.Add(installButton);
                    projectPanel.Children.Add(projectActions);
                    searchResults.Children.Add(new Border
                    {
                        CornerRadius = new CornerRadius(8),
                        BorderThickness = new Thickness(1),
                        BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["SakanaStrokeBrush"],
                        Padding = new Thickness(10),
                        Child = projectPanel
                    });

                    chooseVersionButton.Click += async (_, _) =>
                    {
                        chooseVersionButton.IsEnabled = false;
                        searchStatus.Text = $"正在读取 {project.Title} 的 Minecraft {minecraftVersion} 兼容版本…";
                        try
                        {
                            var releases = await ViewModel.GetCompatibleModrinthContentVersionsAsync(profileId, kind, project.ProjectId);
                            versionInput.Items.Clear();
                            foreach (var release in releases)
                            {
                                var loaders = release.Loaders.Count == 0 ? "未标注加载器" : string.Join(", ", release.Loaders);
                                versionInput.Items.Add(new ComboBoxItem
                                {
                                    Content = $"{release.VersionNumber} · {release.VersionType} · {loaders} · {FormatFileSize(release.SizeBytes)}",
                                    Tag = release
                                });
                            }

                            if (versionInput.Items.Count == 0)
                            {
                                searchStatus.Text = $"此项目没有适配 Minecraft {minecraftVersion} 且包含 ZIP 文件的{GetContentKindName()}版本。";
                                return;
                            }

                            versionInput.SelectedIndex = 0;
                            versionInput.Visibility = Visibility.Visible;
                            installButton.Visibility = Visibility.Visible;
                            searchStatus.Text = GetKind() switch
                            {
                                MinecraftLocalPackKind.ShaderPack => $"{project.Title}：选择一个兼容版本。",
                                MinecraftLocalPackKind.DataPack => $"{project.Title}：选择一个适配 Minecraft {minecraftVersion} 的版本。",
                                _ => $"{project.Title}：选择一个适配 Minecraft {minecraftVersion} 的版本。"
                            };
                        }
                        catch (Exception exception)
                        {
                            await App.CurrentApp.Logger.ErrorAsync("modrinth.pack_versions_failed", "Compatible Modrinth pack versions could not be loaded.", exception);
                            searchStatus.Text = exception.Message;
                        }
                        finally
                        {
                            chooseVersionButton.IsEnabled = true;
                        }
                    };

                    installButton.Click += async (_, _) =>
                    {
                        if (versionInput.SelectedItem is not ComboBoxItem { Tag: ModrinthModRelease release })
                        {
                            return;
                        }

                        installButton.IsEnabled = false;
                        chooseVersionButton.IsEnabled = false;
                        var selectedWorldName = kind == ModrinthContentKind.DataPack ? GetSelectedWorldName() : null;
                        if (kind == ModrinthContentKind.DataPack && selectedWorldName is null)
                        {
                            searchStatus.Text = "先选择一个世界；如果列表为空，请在此实例中创建或打开一个世界。";
                            installButton.IsEnabled = true;
                            chooseVersionButton.IsEnabled = true;
                            return;
                        }

                        searchStatus.Text = $"正在下载并验证 {project.Title}…";
                        try
                        {
                            var result = await ViewModel.InstallModrinthContentVersionAsync(profileId, kind, release.VersionId, selectedWorldName);
                            feedbackText.Text = result.InstalledCount == 0
                                ? $"{GetContentKindName()}已存在；没有重复安装。"
                                : kind == ModrinthContentKind.DataPack
                                    ? $"已将数据包 {result.FileName} 安装到世界“{selectedWorldName}”。进入该世界的数据包列表启用它。"
                                    : $"已安装{GetContentKindName()} {result.FileName}。";
                            searchStatus.Text = feedbackText.Text;
                            await RefreshPacksAsync();
                        }
                        catch (Exception exception)
                        {
                            await App.CurrentApp.Logger.ErrorAsync("modrinth.pack_install_failed", "The selected Modrinth resource or shader pack could not be installed.", exception);
                            searchStatus.Text = exception.Message;
                        }
                        finally
                        {
                            installButton.IsEnabled = true;
                            chooseVersionButton.IsEnabled = true;
                        }
                    };
                }
            }
            catch (Exception exception)
            {
                await App.CurrentApp.Logger.ErrorAsync("modrinth.pack_search_failed", "Modrinth resource or shader pack search failed.", exception);
                searchStatus.Text = $"搜索失败：{exception.Message}";
            }
            finally
            {
                searchInProgress = false;
                searchButton.IsEnabled = true;
                loadMoreButton.IsEnabled = true;
            }
        }

        searchButton.Click += async (_, _) => await SearchContentAsync(loadMore: false);
        loadMoreButton.Click += async (_, _) => await SearchContentAsync(loadMore: true);
        queryInput.KeyDown += async (_, args) =>
        {
            if (args.Key == Windows.System.VirtualKey.Enter)
            {
                args.Handled = true;
                await SearchContentAsync(loadMore: false);
            }
        };
        sortInput.SelectionChanged += async (_, _) =>
        {
            if (lastSearchQuery is not null)
            {
                await SearchContentAsync(loadMore: false);
            }
        };

        addButton.Click += async (_, _) =>
        {
            addButton.IsEnabled = false;
            var kind = GetKind();
            try
            {
                var worldName = kind == MinecraftLocalPackKind.DataPack ? GetSelectedWorldName() : null;
                if (kind == MinecraftLocalPackKind.DataPack && worldName is null)
                {
                    feedbackText.Text = "先选择一个世界；如果列表为空，请在此实例中创建或打开一个世界。";
                    return;
                }

                var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(App.CurrentApp.WindowHandle);
                var picker = new FileOpenPicker(windowId);
                picker.FileTypeFilter.Add(".zip");
                picker.CommitButtonText = "添加 ZIP";
                var selectedFiles = await picker.PickMultipleFilesAsync();
                if (selectedFiles.Count == 0)
                {
                    return;
                }

                var result = await ViewModel.ImportLocalPacksAsync(profileId, kind, selectedFiles.Select(file => file.Path).ToArray(), worldName);
                feedbackText.Text = kind == MinecraftLocalPackKind.DataPack
                    ? $"已向世界“{worldName}”添加 {result.ImportedCount} 个数据包；跳过同名文件 {result.SkippedDuplicateCount} 个。"
                    : $"已添加 {result.ImportedCount} 个包；跳过同名文件 {result.SkippedDuplicateCount} 个。";
                await RefreshPacksAsync();
            }
            catch (Exception exception)
            {
                await App.CurrentApp.Logger.ErrorAsync("packs.import_failed", "Local resource or shader packs could not be imported.", exception);
                feedbackText.Text = exception.Message;
            }
            finally
            {
                addButton.IsEnabled = true;
            }
        };

        await RefreshPacksAsync();
        await dialog.ShowAsync();
    }

    private async Task ShowLocalModsDialogAsync(Guid profileId, string profileName)
    {
        var summaryText = new TextBlock { FontSize = 12, Foreground = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["SakanaMutedBrush"] };
        var feedbackText = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        var modsPanel = new StackPanel { Spacing = 8 };
        var addButton = new Button { Content = "＋ 添加本地模组", HorizontalAlignment = HorizontalAlignment.Left };
        var downloadButton = new Button { Content = "从 Modrinth 下载", HorizontalAlignment = HorizontalAlignment.Left };
        var checkUpdatesButton = new Button { Content = "检查更新", HorizontalAlignment = HorizontalAlignment.Left };
        var availableUpdates = new Dictionary<string, ModrinthModUpdate>(StringComparer.OrdinalIgnoreCase);
        var queryInput = new TextBox { PlaceholderText = "搜索模组；留空可浏览热门项目", MaxLength = 100, MinWidth = 250 };
        var searchButton = new Button { Content = "搜索", MinWidth = 76 };
        var sortInput = CreateModrinthSortPicker();
        var loadMoreButton = new Button { Content = "加载更多", Visibility = Visibility.Collapsed };
        var searchStatus = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        var searchResults = new StackPanel { Spacing = 8 };
        var searchPanel = new StackPanel { Spacing = 8, Visibility = Visibility.Collapsed };
        var searchOffset = 0;
        var searchTotalHits = 0;
        var searchInProgress = false;
        string? lastSearchQuery = null;
        string? lastSortOrder = null;
        searchPanel.Children.Add(new TextBlock
        {
            Text = $"Modrinth · Minecraft {ViewModel.Profiles.First(item => item.Id == profileId).VersionId} · {ViewModel.Profiles.First(item => item.Id == profileId).VersionLabel.Split('·').Last().Trim()}",
            FontSize = 12
        });
        var searchControls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        searchControls.Children.Add(queryInput);
        searchControls.Children.Add(sortInput);
        searchControls.Children.Add(searchButton);
        searchPanel.Children.Add(searchControls);
        searchPanel.Children.Add(searchStatus);
        searchPanel.Children.Add(loadMoreButton);
        searchPanel.Children.Add(new ScrollViewer
        {
            Content = searchResults,
            MaxHeight = 210,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        });
        var actionButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actionButtons.Children.Add(addButton);
        actionButtons.Children.Add(downloadButton);
        actionButtons.Children.Add(checkUpdatesButton);
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock
        {
            Text = "模组只放在这个实例中。可安装适配当前 Minecraft/加载器版本的 Modrinth 模组及必需依赖，也可从本地添加；支持启用、停用、移除，并检查 Sakana 通过 Modrinth 安装且未被修改的模组更新。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12
        });
        content.Children.Add(actionButtons);
        content.Children.Add(searchPanel);
        content.Children.Add(summaryText);
        content.Children.Add(feedbackText);
        content.Children.Add(new ScrollViewer
        {
            Content = modsPanel,
            MaxHeight = 390,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        });

        var dialog = new ContentDialog
        {
            Title = $"{profileName} · 模组",
            Content = content,
            CloseButtonText = "关闭",
            XamlRoot = XamlRoot
        };

        async Task RefreshModsAsync()
        {
            var mods = await ViewModel.ListLocalModsAsync(profileId);
            modsPanel.Children.Clear();
            summaryText.Text = mods.Count == 0 ? "尚未添加模组" : $"此实例有 {mods.Count} 个模组";
            if (mods.Count == 0)
            {
                modsPanel.Children.Add(new TextBlock
                {
                    Text = "从本地选择一个或多个适用于此加载器的模组 .jar 文件。",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["SakanaMutedBrush"]
                });
                return;
            }

            foreach (var mod in mods)
            {
                var modToggle = new ToggleSwitch
                {
                    Header = mod.DisplayName,
                    IsOn = mod.IsEnabled,
                    OnContent = "启用",
                    OffContent = "已停用",
                    MinWidth = 170
                };
                var detailParts = new List<string>();
                if (!string.IsNullOrWhiteSpace(mod.ModId)) detailParts.Add(mod.ModId);
                if (!string.IsNullOrWhiteSpace(mod.Version)) detailParts.Add(mod.Version);
                detailParts.Add(FormatFileSize(mod.SizeBytes));
                detailParts.Add(mod.FileName);
                var detailText = new TextBlock
                {
                    Text = string.Join(" · ", detailParts),
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["SakanaMutedBrush"]
                };
                var removeButton = new Button { Content = "移除", VerticalAlignment = VerticalAlignment.Center };
                var rowActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
                if (availableUpdates.TryGetValue(mod.FileName, out var availableUpdate))
                {
                    var updateButton = new Button
                    {
                        Content = $"更新到 {availableUpdate.AvailableRelease.VersionNumber}",
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    updateButton.Click += async (_, _) =>
                    {
                        updateButton.IsEnabled = false;
                        feedbackText.Text = $"正在更新 {mod.DisplayName}，并检查必需依赖…";
                        try
                        {
                            var result = await ViewModel.UpdateInstalledModAsync(profileId, mod.FileName, availableUpdate.AvailableRelease.VersionId);
                            availableUpdates.Clear();
                            feedbackText.Text = result.InstalledCount == 0
                                ? $"{mod.DisplayName} 已是最新版本。"
                                : $"已更新 {mod.DisplayName}，安装或替换 {result.InstalledCount} 个文件；复用了 {result.SkippedDuplicateCount} 个相同文件。";
                            await RefreshModsAsync();
                        }
                        catch (Exception exception)
                        {
                            await App.CurrentApp.Logger.ErrorAsync("modrinth.update_failed", "A Modrinth mod update could not be applied.", exception);
                            feedbackText.Text = $"更新 {mod.DisplayName} 失败：{exception.Message}";
                            updateButton.IsEnabled = true;
                        }
                    };
                    rowActions.Children.Add(updateButton);
                }

                rowActions.Children.Add(removeButton);
                var row = new Grid { ColumnSpacing = 12, Margin = new Thickness(0, 0, 0, 8) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var detailsPanel = new StackPanel { Spacing = 2 };
                detailsPanel.Children.Add(modToggle);
                detailsPanel.Children.Add(detailText);
                row.Children.Add(detailsPanel);
                Grid.SetColumn(rowActions, 1);
                row.Children.Add(rowActions);
                modsPanel.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(10),
                    BorderThickness = new Thickness(1),
                    BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["SakanaStrokeBrush"],
                    Padding = new Thickness(10),
                    Child = row
                });

                modToggle.Toggled += async (_, _) =>
                {
                    modToggle.IsEnabled = false;
                    try
                    {
                        await ViewModel.SetLocalModEnabledAsync(profileId, mod.FileName, modToggle.IsOn);
                        feedbackText.Text = modToggle.IsOn ? $"已启用 {mod.DisplayName}。" : $"已停用 {mod.DisplayName}。";
                        await RefreshModsAsync();
                    }
                    catch (Exception exception)
                    {
                        await App.CurrentApp.Logger.ErrorAsync("mods.toggle_failed", "A local mod could not be enabled or disabled.", exception);
                        feedbackText.Text = exception.Message;
                        await RefreshModsAsync();
                    }
                };

                removeButton.Click += async (_, _) =>
                {
                    removeButton.IsEnabled = false;
                    try
                    {
                        await ViewModel.RemoveLocalModAsync(profileId, mod.FileName);
                        availableUpdates.Remove(mod.FileName);
                        feedbackText.Text = $"已从此实例移除 {mod.DisplayName}。";
                        await RefreshModsAsync();
                    }
                    catch (Exception exception)
                    {
                        await App.CurrentApp.Logger.ErrorAsync("mods.remove_failed", "A local mod could not be removed.", exception);
                        feedbackText.Text = exception.Message;
                        removeButton.IsEnabled = true;
                    }
                };
            }
        }

        addButton.Click += async (_, _) =>
        {
            addButton.IsEnabled = false;
            try
            {
                var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(App.CurrentApp.WindowHandle);
                var picker = new FileOpenPicker(windowId);
                picker.FileTypeFilter.Add(".jar");
                picker.CommitButtonText = "添加模组";
                var selectedFiles = await picker.PickMultipleFilesAsync();
                if (selectedFiles.Count == 0)
                {
                    return;
                }

                var result = await ViewModel.ImportLocalModsAsync(profileId, selectedFiles.Select(file => file.Path).ToArray());
                availableUpdates.Clear();
                feedbackText.Text = $"已添加 {result.ImportedCount} 个模组；跳过同名文件 {result.SkippedDuplicateCount} 个。";
                await RefreshModsAsync();
            }
            catch (Exception exception)
            {
                await App.CurrentApp.Logger.ErrorAsync("mods.import_failed", "Local mod files could not be added to the instance.", exception);
                feedbackText.Text = exception.Message;
            }
            finally
            {
                addButton.IsEnabled = true;
            }
        };

        downloadButton.Click += (_, _) =>
        {
            searchPanel.Visibility = searchPanel.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
            if (searchPanel.Visibility == Visibility.Visible)
            {
                queryInput.Focus(FocusState.Programmatic);
            }
        };

        checkUpdatesButton.Click += async (_, _) =>
        {
            checkUpdatesButton.IsEnabled = false;
            feedbackText.Text = "正在检查此实例中可更新的 Modrinth 模组…";
            try
            {
                var updates = await ViewModel.CheckInstalledModUpdatesAsync(profileId);
                availableUpdates.Clear();
                foreach (var update in updates)
                {
                    availableUpdates[update.FileName] = update;
                }

                feedbackText.Text = updates.Count == 0
                    ? "没有发现可更新的 Modrinth 模组。其他来源或已修改的模组不会自动更新。"
                    : $"发现 {updates.Count} 个 Modrinth 模组更新；逐项检查后可更新，并保留当前启用/停用状态。";
                await RefreshModsAsync();
            }
            catch (Exception exception)
            {
                await App.CurrentApp.Logger.ErrorAsync("modrinth.update_check_failed", "Installed Modrinth mod updates could not be checked.", exception);
                feedbackText.Text = $"检查更新失败：{exception.Message}";
            }
            finally
            {
                checkUpdatesButton.IsEnabled = true;
            }
        };

        async Task SearchModsAsync(bool loadMore)
        {
            if (searchInProgress)
            {
                return;
            }

            var sortOrder = GetModrinthSortOrder(sortInput);
            if (loadMore && (!string.Equals(lastSearchQuery, queryInput.Text, StringComparison.Ordinal) ||
                             !string.Equals(lastSortOrder, sortOrder, StringComparison.Ordinal)))
            {
                loadMore = false;
            }

            searchInProgress = true;
            searchButton.IsEnabled = false;
            loadMoreButton.IsEnabled = false;
            if (!loadMore)
            {
                searchOffset = 0;
                searchTotalHits = 0;
                lastSearchQuery = queryInput.Text;
                lastSortOrder = sortOrder;
                searchResults.Children.Clear();
                loadMoreButton.Visibility = Visibility.Collapsed;
            }

            searchStatus.Text = loadMore ? "正在加载更多 Modrinth 模组…" : "正在搜索兼容的 Modrinth 模组…";
            try
            {
                var page = await ViewModel.SearchModrinthModsAsync(profileId, queryInput.Text, sortOrder, searchOffset);
                var projects = page.Projects;
                searchOffset = Math.Min(page.Offset + page.Limit, page.TotalHits);
                searchTotalHits = page.TotalHits;
                searchStatus.Text = searchTotalHits == 0
                    ? "没有找到兼容的模组。试试其他关键词。"
                    : $"找到 {searchTotalHits:N0} 个兼容项目，当前显示 {Math.Min(searchOffset, searchTotalHits):N0} 个。选择项目后再选择要安装的版本。";
                if (searchTotalHits > 10000 && searchOffset >= 10000)
                {
                    searchStatus.Text += " Modrinth 的单次搜索最多可浏览 10,000 项。";
                }
                loadMoreButton.Visibility = searchOffset < searchTotalHits && searchOffset < 10000 ? Visibility.Visible : Visibility.Collapsed;

                foreach (var project in projects)
                {
                    var projectPanel = new StackPanel { Spacing = 4 };
                    projectPanel.Children.Add(CreateModrinthProjectSummary(project, ViewModel.Profiles.First(item => item.Id == profileId).VersionId));

                    var versionInput = new ComboBox
                    {
                        PlaceholderText = "选择版本",
                        MinWidth = 220,
                        Visibility = Visibility.Collapsed
                    };
                    var installButton = new Button
                    {
                        Content = "安装（含必需依赖）",
                        Visibility = Visibility.Collapsed
                    };
                    var projectActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                    var chooseVersionButton = new Button { Content = "选择版本" };
                    projectActions.Children.Add(chooseVersionButton);
                    projectActions.Children.Add(versionInput);
                    projectActions.Children.Add(installButton);
                    projectPanel.Children.Add(projectActions);
                    searchResults.Children.Add(new Border
                    {
                        CornerRadius = new CornerRadius(8),
                        BorderThickness = new Thickness(1),
                        BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["SakanaStrokeBrush"],
                        Padding = new Thickness(10),
                        Child = projectPanel
                    });

                    chooseVersionButton.Click += async (_, _) =>
                    {
                        chooseVersionButton.IsEnabled = false;
                        searchStatus.Text = $"正在读取 {project.Title} 的兼容版本…";
                        try
                        {
                            var releases = await ViewModel.GetCompatibleModrinthVersionsAsync(profileId, project.ProjectId);
                            versionInput.Items.Clear();
                            foreach (var release in releases)
                            {
                                versionInput.Items.Add(new ComboBoxItem
                                {
                                    Content = $"{release.VersionNumber} · {release.VersionType} · {FormatFileSize(release.SizeBytes)}",
                                    Tag = release
                                });
                            }

                            if (versionInput.Items.Count == 0)
                            {
                                searchStatus.Text = "此项目没有适配当前 Minecraft/加载器版本且可下载的 JAR。";
                                return;
                            }

                            versionInput.SelectedIndex = 0;
                            versionInput.Visibility = Visibility.Visible;
                            installButton.Visibility = Visibility.Visible;
                            searchStatus.Text = $"{project.Title}：选择一个兼容版本。必需依赖会一起安装。";
                        }
                        catch (Exception exception)
                        {
                            await App.CurrentApp.Logger.ErrorAsync("modrinth.versions_failed", "Compatible Modrinth mod versions could not be loaded.", exception);
                            searchStatus.Text = exception.Message;
                        }
                        finally
                        {
                            chooseVersionButton.IsEnabled = true;
                        }
                    };

                    installButton.Click += async (_, _) =>
                    {
                        if (versionInput.SelectedItem is not ComboBoxItem { Tag: ModrinthModRelease release })
                        {
                            return;
                        }

                        installButton.IsEnabled = false;
                        chooseVersionButton.IsEnabled = false;
                        searchStatus.Text = $"正在安装 {project.Title} 及必需依赖，请稍候…";
                        try
                        {
                            var result = await ViewModel.InstallModrinthVersionAsync(profileId, release.VersionId);
                            availableUpdates.Clear();
                            feedbackText.Text = result.InstalledCount == 0
                                ? "模组文件已经存在；没有重复安装。"
                                : $"已安装 {result.InstalledCount} 个文件（包含必需依赖）；跳过同名文件 {result.SkippedDuplicateCount} 个。";
                            searchStatus.Text = feedbackText.Text;
                            await RefreshModsAsync();
                        }
                        catch (Exception exception)
                        {
                            await App.CurrentApp.Logger.ErrorAsync("modrinth.install_failed", "The selected Modrinth mod could not be installed.", exception);
                            searchStatus.Text = exception.Message;
                        }
                        finally
                        {
                            installButton.IsEnabled = true;
                            chooseVersionButton.IsEnabled = true;
                        }
                    };
                }
            }
            catch (Exception exception)
            {
                await App.CurrentApp.Logger.ErrorAsync("modrinth.search_failed", "Modrinth mod search failed.", exception);
                searchStatus.Text = $"搜索失败：{exception.Message}";
            }
            finally
            {
                searchInProgress = false;
                searchButton.IsEnabled = true;
                loadMoreButton.IsEnabled = true;
            }
        }

        searchButton.Click += async (_, _) => await SearchModsAsync(loadMore: false);
        loadMoreButton.Click += async (_, _) => await SearchModsAsync(loadMore: true);
        queryInput.KeyDown += async (_, args) =>
        {
            if (args.Key == Windows.System.VirtualKey.Enter)
            {
                args.Handled = true;
                await SearchModsAsync(loadMore: false);
            }
        };
        sortInput.SelectionChanged += async (_, _) =>
        {
            if (lastSearchQuery is not null)
            {
                await SearchModsAsync(loadMore: false);
            }
        };

        await RefreshModsAsync();
        await dialog.ShowAsync();
    }

    private async void BrowseModrinthPacks_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ShowModrinthPackBrowserAsync();
        }
        catch (OperationCanceledException)
        {
            // Browsing and download can be dismissed by the user.
        }
        catch (Exception exception)
        {
            await App.CurrentApp.Logger.ErrorAsync("modpack.browser_failed", "The Modrinth modpack browser failed.", exception);
            ShowError("无法浏览整合包", "请检查网络连接后重试。", exception.Message);
        }
    }

    private async Task ShowModrinthPackBrowserAsync()
    {
        var queryInput = new TextBox { PlaceholderText = "搜索整合包；留空可浏览热门整合包", MaxLength = 100, MinWidth = 0 };
        var searchButton = new Button { Content = "搜索", MinWidth = 76 };
        var sortInput = CreateModrinthSortPicker();
        sortInput.SelectedIndex = 1;
        sortInput.Width = 142;
        sortInput.MinWidth = 0;
        var loadMoreButton = new Button { Content = "加载更多", Visibility = Visibility.Collapsed };
        var searchStatus = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        var resultsPanel = new StackPanel { Spacing = 8 };
        var searchOffset = 0;
        var searchTotalHits = 0;
        var searchInProgress = false;
        string? lastSearchQuery = null;
        string? lastSortOrder = null;
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock
        {
            Text = "从 Modrinth 在线发现并安装原版、Fabric、Quilt、Forge 和 NeoForge 整合包。Forge 与 NeoForge 的安装和启动仍需在实际环境中持续验证。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12
        });
        var searchControls = new Grid { ColumnSpacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch };
        searchControls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        searchControls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        searchControls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(sortInput, 1);
        Grid.SetColumn(searchButton, 2);
        searchControls.Children.Add(queryInput);
        searchControls.Children.Add(sortInput);
        searchControls.Children.Add(searchButton);
        content.Children.Add(searchControls);
        content.Children.Add(searchStatus);
        content.Children.Add(loadMoreButton);
        content.Children.Add(new ScrollViewer
        {
            Content = resultsPanel,
            MaxHeight = 430,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        });

        var dialog = new ContentDialog
        {
            Title = "在线浏览 Modrinth 整合包",
            Content = content,
            CloseButtonText = "关闭",
            XamlRoot = XamlRoot
        };
        string? selectedVersionId = null;
        string? selectedProjectId = null;
        string? selectedVersionNumber = null;

        async Task SearchModpacksAsync(bool loadMore)
        {
            if (searchInProgress)
            {
                return;
            }

            var sortOrder = GetModrinthSortOrder(sortInput);
            if (loadMore && (!string.Equals(lastSearchQuery, queryInput.Text, StringComparison.Ordinal) || !string.Equals(lastSortOrder, sortOrder, StringComparison.Ordinal)))
            {
                loadMore = false;
            }

            searchInProgress = true;
            searchButton.IsEnabled = false;
            loadMoreButton.IsEnabled = false;
            if (!loadMore)
            {
                searchOffset = 0;
                searchTotalHits = 0;
                lastSearchQuery = queryInput.Text;
                lastSortOrder = sortOrder;
                resultsPanel.Children.Clear();
                loadMoreButton.Visibility = Visibility.Collapsed;
            }

            searchStatus.Text = loadMore ? "正在加载更多 Modrinth 整合包…" : "正在搜索 Modrinth 整合包…";
            try
            {
                var page = await ViewModel.SearchModrinthModpacksAsync(queryInput.Text, sortOrder, searchOffset);
                var projects = page.Projects;
                searchOffset = Math.Min(page.Offset + page.Limit, page.TotalHits);
                searchTotalHits = page.TotalHits;
                searchStatus.Text = searchTotalHits == 0
                    ? "没有找到匹配的整合包。试试其他关键词。"
                    : $"找到 {searchTotalHits:N0} 个整合包，当前显示 {Math.Min(searchOffset, searchTotalHits):N0} 个。打开版本列表后，可查看游戏版本和加载器。";
                if (searchTotalHits > 10000 && searchOffset >= 10000)
                {
                    searchStatus.Text += " Modrinth 的单次搜索最多可浏览 10,000 项。";
                }
                loadMoreButton.Visibility = searchOffset < searchTotalHits && searchOffset < 10000 ? Visibility.Visible : Visibility.Collapsed;
                foreach (var project in projects)
                {
                    var projectPanel = new StackPanel { Spacing = 5 };
                    projectPanel.Children.Add(CreateModrinthProjectSummary(project, minecraftVersion: null));
                    var versionInput = new ComboBox
                    {
                        PlaceholderText = "选择版本",
                        MinWidth = 260,
                        Visibility = Visibility.Collapsed
                    };
                    var chooseButton = new Button { Content = "查看可安装版本" };
                    var continueButton = new Button { Content = "下载并导入", Visibility = Visibility.Collapsed };
                    var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                    actions.Children.Add(chooseButton);
                    actions.Children.Add(versionInput);
                    actions.Children.Add(continueButton);
                    projectPanel.Children.Add(actions);
                    resultsPanel.Children.Add(new Border
                    {
                        CornerRadius = new CornerRadius(8),
                        BorderThickness = new Thickness(1),
                        BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["SakanaStrokeBrush"],
                        Padding = new Thickness(10),
                        Child = projectPanel
                    });

                    chooseButton.Click += async (_, _) =>
                    {
                        chooseButton.IsEnabled = false;
                        searchStatus.Text = $"正在读取 {project.Title} 的版本…";
                        try
                        {
                            var versions = await ViewModel.GetSupportedModrinthModpackVersionsAsync(project.ProjectId);
                            versionInput.Items.Clear();
                            foreach (var version in versions)
                            {
                                var gameVersions = version.GameVersions.Count <= 3
                                    ? string.Join(", ", version.GameVersions)
                                    : $"{string.Join(", ", version.GameVersions.Take(3))} 等 {version.GameVersions.Count} 个版本";
                                var loaders = string.Join(", ", version.Loaders);
                                versionInput.Items.Add(new ComboBoxItem
                                {
                                    Content = $"{version.VersionNumber} · Minecraft {gameVersions} · {loaders} · {FormatFileSize(version.SizeBytes)}",
                                    Tag = version
                                });
                            }

                            if (versionInput.Items.Count == 0)
                            {
                                searchStatus.Text = "这个整合包没有可安装的 Vanilla、Fabric、Quilt、Forge 或 NeoForge .mrpack 版本。";
                                return;
                            }

                            versionInput.SelectedIndex = 0;
                            versionInput.Visibility = Visibility.Visible;
                            continueButton.Visibility = Visibility.Visible;
                            searchStatus.Text = $"{project.Title}：选择一个版本后继续。";
                        }
                        catch (Exception exception)
                        {
                            await App.CurrentApp.Logger.ErrorAsync("modpack.versions_failed", "Supported Modrinth modpack versions could not be loaded.", exception);
                            searchStatus.Text = exception.Message;
                        }
                        finally
                        {
                            chooseButton.IsEnabled = true;
                        }
                    };

                    continueButton.Click += (_, _) =>
                    {
                        if (versionInput.SelectedItem is not ComboBoxItem { Tag: ModrinthModRelease version }) return;
                        selectedVersionId = version.VersionId;
                        selectedProjectId = project.ProjectId;
                        selectedVersionNumber = version.VersionNumber;
                        dialog.Hide();
                    };
                }
            }
            catch (Exception exception)
            {
                await App.CurrentApp.Logger.ErrorAsync("modpack.search_failed", "Modrinth modpack search failed.", exception);
                searchStatus.Text = $"搜索失败：{exception.Message}";
            }
            finally
            {
                searchInProgress = false;
                searchButton.IsEnabled = true;
                loadMoreButton.IsEnabled = true;
            }
        }

        searchButton.Click += async (_, _) => await SearchModpacksAsync(loadMore: false);
        loadMoreButton.Click += async (_, _) => await SearchModpacksAsync(loadMore: true);
        queryInput.KeyDown += async (_, args) =>
        {
            if (args.Key == Windows.System.VirtualKey.Enter)
            {
                args.Handled = true;
                await SearchModpacksAsync(loadMore: false);
            }
        };
        sortInput.SelectionChanged += async (_, _) =>
        {
            if (lastSearchQuery is not null)
            {
                await SearchModpacksAsync(loadMore: false);
            }
        };

        _ = SearchModpacksAsync(loadMore: false);
        await dialog.ShowAsync();
        if (selectedVersionId is null)
        {
            return;
        }

        var archivePath = await ViewModel.DownloadModrinthModpackVersionAsync(selectedVersionId);
        var stagingDirectory = Path.GetDirectoryName(archivePath);
        try
        {
            await ImportModrinthPackArchiveAsync(archivePath, selectedProjectId, selectedVersionId, selectedVersionNumber);
        }
        finally
        {
            if (stagingDirectory is not null && Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }
        }
    }

    private async Task ImportModrinthPackAsync()
    {
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(App.CurrentApp.WindowHandle);
        var picker = new FileOpenPicker(windowId);
        picker.FileTypeFilter.Add(".mrpack");

        var selectedFile = await picker.PickSingleFileAsync();
        if (selectedFile is null)
        {
            return;
        }

        await ImportModrinthPackArchiveAsync(selectedFile.Path);
    }

    private async Task ImportModrinthPackArchiveAsync(
        string archivePath,
        string? modrinthProjectId = null,
        string? modrinthVersionId = null,
        string? modrinthVersionNumber = null,
        string? suggestedInstanceName = null)
    {

        ModrinthModpackDescription description;
        try
        {
            description = await ViewModel.InspectModrinthPackAsync(archivePath);
        }
        catch (Exception exception)
        {
            await App.CurrentApp.Logger.ErrorAsync("modpack.inspect_failed", "The selected Modrinth modpack could not be read.", exception);
            ShowError("无法读取整合包", "请选择有效的 Modrinth .mrpack 文件。", exception.Message);
            return;
        }

        var nameInput = new TextBox
        {
            Header = "实例名称",
            Text = !string.IsNullOrWhiteSpace(suggestedInstanceName)
                ? suggestedInstanceName.Length <= 40 ? suggestedInstanceName : suggestedInstanceName[..40]
                : description.Name.Length <= 40 ? description.Name : description.Name[..40],
            MaxLength = 40
        };
        var loaderName = description.LoaderType switch
        {
            MinecraftLoaderType.Fabric => $"Fabric Loader {description.LoaderVersion}",
            MinecraftLoaderType.Quilt => $"Quilt Loader {description.LoaderVersion}",
            MinecraftLoaderType.Forge => $"Forge {description.LoaderVersion}",
            MinecraftLoaderType.NeoForge => $"NeoForge {description.LoaderVersion}",
            _ => "原版（Vanilla）"
        };
        var information = new TextBlock
        {
            Text = $"Minecraft {description.MinecraftVersion} · {loaderName}\n必需文件：{description.RequiredClientFiles} 个。整合包的配置文件和资源会安装到这个实例中。",
            TextWrapping = TextWrapping.Wrap
        };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(nameInput);
        content.Children.Add(information);
        if (!string.IsNullOrWhiteSpace(description.Summary))
        {
            content.Children.Add(new TextBlock { Text = description.Summary, TextWrapping = TextWrapping.Wrap });
        }

        var optionalChoices = new List<CheckBox>();
        if (description.OptionalFiles.Count > 0)
        {
            content.Children.Add(new TextBlock { Text = "可选文件（默认勾选，可按需取消）", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            var optionalFilesPanel = new StackPanel { Spacing = 6 };
            foreach (var optionalFile in description.OptionalFiles)
            {
                var checkBox = new CheckBox
                {
                    Content = $"{optionalFile.Path} · {FormatFileSize(optionalFile.Size)}",
                    Tag = optionalFile.Path,
                    IsChecked = true
                };
                optionalChoices.Add(checkBox);
                optionalFilesPanel.Children.Add(checkBox);
            }

            content.Children.Add(new ScrollViewer
            {
                Content = optionalFilesPanel,
                MaxHeight = 220,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            });
        }
        else
        {
            content.Children.Add(new TextBlock
            {
                Text = "此整合包没有可选客户端文件。",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12
            });
        }

        var dialog = new ContentDialog
        {
            Title = "导入 Modrinth 整合包",
            Content = content,
            PrimaryButtonText = "导入并安装",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var selectedOptionalFiles = optionalChoices
            .Where(checkBox => checkBox.IsChecked == true)
            .Select(checkBox => (string)checkBox.Tag)
            .ToArray();
        InstanceProfile profile;
        try
        {
            profile = await ViewModel.ImportModrinthPackAsync(
                archivePath,
                nameInput.Text,
                selectedOptionalFiles,
                modrinthProjectId: modrinthProjectId,
                modrinthVersionId: modrinthVersionId,
                modrinthVersionNumber: modrinthVersionNumber);
            RefreshGroupFilterOptions();
            ApplyLibraryFilter();
            UpdateLibraryListVisibility();
            ErrorInfoBar.IsOpen = false;
        }
        catch (ProfileNameInUseException)
        {
            ShowError("配置名称已存在", "请为这个整合包输入一个不同的实例名称。", string.Empty);
            return;
        }
        catch (Exception exception)
        {
            await App.CurrentApp.Logger.ErrorAsync("modpack.import_failed", "The Modrinth modpack could not be imported.", exception);
            ShowError("无法导入整合包", "Sakana 无法创建这个整合包实例。", exception.Message);
            return;
        }

        try
        {
            await ViewModel.InstallProfileAsync(profile.Id);
        }
        catch (OperationCanceledException)
        {
            // Installation can be cancelled from the instance card or downloads page.
        }
        catch (Exception exception)
        {
            await App.CurrentApp.Logger.ErrorAsync("modpack.install_failed", "The imported Modrinth modpack could not be installed.", exception);
            ShowError("整合包安装失败", "实例已保存在实例库中，可稍后重试。", exception.Message);
        }
    }

    private static UIElement CreateModrinthProjectSummary(ModrinthModProject project, string? minecraftVersion)
    {
        var mutedBrush = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["SakanaMutedBrush"];
        var fallbackIcon = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(project.Title) ? "M" : project.Title[..1],
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var iconBorder = new Border
        {
            Width = 48,
            Height = 48,
            CornerRadius = new CornerRadius(8),
            Background = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["SakanaAccentSoftBrush"],
            Child = fallbackIcon
        };
        if (Uri.TryCreate(project.IconUrl, UriKind.Absolute, out var iconUri) &&
            iconUri.Scheme == Uri.UriSchemeHttps &&
            iconUri.Host.Equals("cdn.modrinth.com", StringComparison.OrdinalIgnoreCase))
        {
            var image = new Image
            {
                Width = 48,
                Height = 48,
                Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill,
                Source = new BitmapImage(iconUri)
            };
            image.ImageFailed += (_, _) => iconBorder.Child = fallbackIcon;
            iconBorder.Child = image;
        }

        var metadataLines = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        metadataLines.Children.Add(new TextBlock
        {
            Text = $"{project.Title} · {project.Author} · {project.Downloads:N0} 次下载",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        if (project.Categories is { Count: > 0 })
        {
            metadataLines.Children.Add(new TextBlock
            {
                Text = string.Join(" · ", project.Categories.Take(4)),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = mutedBrush
            });
        }

        if (!string.IsNullOrWhiteSpace(minecraftVersion))
        {
            metadataLines.Children.Add(new TextBlock
            {
                Text = $"Minecraft {minecraftVersion} · Modrinth 兼容搜索",
                FontSize = 11,
                Foreground = mutedBrush
            });
        }

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        header.Children.Add(iconBorder);
        header.Children.Add(metadataLines);
        var summary = new StackPanel { Spacing = 4 };
        summary.Children.Add(header);
        summary.Children.Add(new TextBlock
        {
            Text = project.Description,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 3,
            Foreground = mutedBrush
        });
        return summary;
    }

    private static ComboBox CreateModrinthSortPicker()
    {
        var picker = new ComboBox { MinWidth = 136, PlaceholderText = "排序方式" };
        picker.Items.Add(new ComboBoxItem { Content = "相关度", Tag = "relevance" });
        picker.Items.Add(new ComboBoxItem { Content = "下载最多", Tag = "downloads" });
        picker.Items.Add(new ComboBoxItem { Content = "关注最多", Tag = "follows" });
        picker.Items.Add(new ComboBoxItem { Content = "最新发布", Tag = "newest" });
        picker.Items.Add(new ComboBoxItem { Content = "最近更新", Tag = "updated" });
        picker.SelectedIndex = 0;
        return picker;
    }

    private static string GetModrinthSortOrder(ComboBox picker) =>
        picker.SelectedItem is ComboBoxItem { Tag: string sortOrder } ? sortOrder : "relevance";

    private static string FormatFileSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var size = (double)bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size:0.#} {units[unit]}";
    }

    private static string FormatEnvironmentVariables(string json)
    {
        try
        {
            var variables = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                ?? new Dictionary<string, string>();
            return string.Join(Environment.NewLine, variables.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => $"{pair.Key}={pair.Value}"));
        }
        catch (System.Text.Json.JsonException)
        {
            return string.Empty;
        }
    }

    private static IReadOnlyDictionary<string, string> ParseEnvironmentVariables(string text)
    {
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in text.Split(["\r\n", "\n", "\r"], StringSplitOptions.None))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                throw new FormatException($"环境变量格式无效：{line}。请按 KEY=VALUE 每行填写一项。");
            }

            var name = line[..separator].Trim();
            var value = line[(separator + 1)..];
            if (!variables.TryAdd(name, value))
            {
                throw new FormatException($"环境变量名称重复：{name}。");
            }
        }

        return variables;
    }

    private async void CreateProfile_Click(object sender, RoutedEventArgs e)
    {
        var nameInput = new TextBox { Text = "我的世界", PlaceholderText = "例如：生存世界", MaxLength = 40 };
        var filteredVersions = new ObservableCollection<MinecraftGameVersion>();
        var versionSearchInput = new TextBox
        {
            PlaceholderText = "搜索版本号，例如：1.20.1",
            MaxLength = 40
        };
        var versionInput = new ComboBox
        {
            ItemsSource = filteredVersions,
            DisplayMemberPath = nameof(MinecraftGameVersion.Id),
            IsTextSearchEnabled = true,
            MaxDropDownHeight = 360,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var versionStatus = new TextBlock
        {
            Text = "正在读取 Minecraft 版本列表…",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        };
        var retryVersionLoadButton = new Button
        {
            Content = "重试加载版本",
            HorizontalAlignment = HorizontalAlignment.Left,
            Visibility = Visibility.Collapsed
        };
        ContentDialog dialog = null!;

        var vanillaLoaderItem = new ComboBoxItem { Content = "原版（Vanilla）", Tag = MinecraftLoaderType.Vanilla };
        var fabricLoaderItem = new ComboBoxItem { Content = "Fabric", Tag = MinecraftLoaderType.Fabric };
        var quiltLoaderItem = new ComboBoxItem { Content = "Quilt", Tag = MinecraftLoaderType.Quilt };
        var forgeLoaderItem = new ComboBoxItem { Content = "Forge", Tag = MinecraftLoaderType.Forge };
        var neoForgeLoaderItem = new ComboBoxItem { Content = "NeoForge", Tag = MinecraftLoaderType.NeoForge };
        var loaderInput = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            SelectedItem = vanillaLoaderItem
        };
        loaderInput.Items.Add(vanillaLoaderItem);
        loaderInput.Items.Add(fabricLoaderItem);
        loaderInput.Items.Add(quiltLoaderItem);
        loaderInput.Items.Add(forgeLoaderItem);
        loaderInput.Items.Add(neoForgeLoaderItem);
        loaderInput.SelectedItem = vanillaLoaderItem;

        var loaderVersionInput = new ComboBox
        {
            DisplayMemberPath = nameof(MinecraftLoaderVersion.Version),
            IsTextSearchEnabled = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Visibility = Visibility.Collapsed
        };
        var loaderVersionStatus = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
        };

        async Task RefreshLoaderVersionsAsync()
        {
            var loaderType = (loaderInput.SelectedItem as ComboBoxItem)?.Tag;
            var selectedVersion = versionInput.SelectedItem as MinecraftGameVersion;
            var showLoader = loaderType is MinecraftLoaderType.Fabric or MinecraftLoaderType.Quilt or MinecraftLoaderType.Forge or MinecraftLoaderType.NeoForge;
            var loaderName = loaderType?.ToString() ?? "加载器";
            loaderVersionInput.Visibility = showLoader ? Visibility.Visible : Visibility.Collapsed;
            loaderVersionStatus.Visibility = showLoader ? Visibility.Visible : Visibility.Collapsed;
            if (!showLoader || selectedVersion is null || loaderType is not MinecraftLoaderType selectedLoaderType)
            {
                return;
            }

            loaderVersionInput.IsEnabled = false;
            loaderVersionInput.ItemsSource = null;
            loaderVersionStatus.Text = $"正在读取兼容的 {loaderName} Loader 版本…";
            try
            {
                await ViewModel.LoadLoaderVersionsAsync(selectedLoaderType, selectedVersion.Id);
                loaderVersionInput.ItemsSource = ViewModel.AvailableLoaderVersions;
                loaderVersionInput.SelectedItem = ViewModel.AvailableLoaderVersions.FirstOrDefault(item => item.IsStable)
                    ?? ViewModel.AvailableLoaderVersions.FirstOrDefault();
                loaderVersionStatus.Text = loaderVersionInput.SelectedItem is null
                    ? $"Minecraft {selectedVersion.Id} 暂无可用的 {loaderName} Loader 版本。"
                    : "默认选择稳定版本；安装时会下载对应的启动库。";
            }
            catch (Exception exception)
            {
                loaderVersionInput.ItemsSource = null;
                loaderVersionStatus.Text = $"无法读取 {loaderName} 版本：{exception.Message}";
            }
            finally
            {
                loaderVersionInput.IsEnabled = true;
            }
        }

        loaderInput.SelectionChanged += async (_, _) => await RefreshLoaderVersionsAsync();
        var filteringVersions = false;
        versionSearchInput.TextChanged += async (_, _) =>
        {
            var previousSelection = (versionInput.SelectedItem as MinecraftGameVersion)?.Id;
            var query = versionSearchInput.Text.Trim();
            var matchingVersions = ViewModel.AvailableVersions
                .Where(item => item.Id.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            filteringVersions = true;
            filteredVersions.Clear();
            foreach (var item in matchingVersions)
            {
                filteredVersions.Add(item);
            }

            versionInput.SelectedItem = matchingVersions.FirstOrDefault(item => item.Id == previousSelection)
                ?? matchingVersions.FirstOrDefault();
            filteringVersions = false;

            var currentSelection = (versionInput.SelectedItem as MinecraftGameVersion)?.Id;
            if (!string.Equals(previousSelection, currentSelection, StringComparison.Ordinal))
            {
                await RefreshLoaderVersionsAsync();
            }

            dialog.IsPrimaryButtonEnabled = versionInput.SelectedItem is not null;
        };
        versionInput.SelectionChanged += async (_, _) =>
        {
            if (!filteringVersions)
            {
                await RefreshLoaderVersionsAsync();
            }
        };
        var loadingCancellation = new CancellationTokenSource();
        async Task LoadVersionOptionsAsync()
        {
            dialog.IsPrimaryButtonEnabled = false;
            retryVersionLoadButton.IsEnabled = false;
            retryVersionLoadButton.Visibility = Visibility.Collapsed;
            versionStatus.Text = "正在读取 Minecraft 版本列表…";
            try
            {
                using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(loadingCancellation.Token);
                attemptCancellation.CancelAfter(TimeSpan.FromSeconds(20));
                await ViewModel.LoadAvailableVersionsAsync(attemptCancellation.Token);
                var query = versionSearchInput.Text.Trim();
                var matchingVersions = ViewModel.AvailableVersions
                    .Where(item => item.Id.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                filteringVersions = true;
                filteredVersions.Clear();
                foreach (var item in matchingVersions)
                {
                    filteredVersions.Add(item);
                }

                versionInput.SelectedItem = matchingVersions.FirstOrDefault(version => version.IsLatestRelease)
                    ?? matchingVersions.FirstOrDefault();
                filteringVersions = false;
                dialog.IsPrimaryButtonEnabled = versionInput.SelectedItem is not null;
                versionStatus.Text = matchingVersions.Length == 0
                    ? "没有匹配的正式版，请修改搜索内容。"
                    : $"已加载 {ViewModel.AvailableVersions.Count} 个正式版。";
            }
            catch (OperationCanceledException) when (loadingCancellation.IsCancellationRequested)
            {
                // The dialog was closed while the request was in flight.
            }
            catch (Exception exception)
            {
                filteringVersions = false;
                versionStatus.Text = exception is OperationCanceledException
                    ? "读取版本列表超时，请检查网络后重试。"
                    : $"无法读取版本列表：{exception.Message}";
                retryVersionLoadButton.Visibility = Visibility.Visible;
                await App.CurrentApp.Logger.ErrorAsync("versions.load_failed", "Could not load the official Minecraft release manifest.", exception);
            }
            finally
            {
                retryVersionLoadButton.IsEnabled = true;
            }
        }

        Task currentVersionLoad = Task.CompletedTask;
        retryVersionLoadButton.Click += async (_, _) =>
        {
            currentVersionLoad = LoadVersionOptionsAsync();
            await currentVersionLoad;
        };

        var fields = new StackPanel { Spacing = 12 };
        fields.Children.Add(new TextBlock { Text = "配置名称" });
        fields.Children.Add(nameInput);
        fields.Children.Add(new TextBlock { Text = "Minecraft 发布版本" });
        fields.Children.Add(versionSearchInput);
        fields.Children.Add(versionStatus);
        fields.Children.Add(retryVersionLoadButton);
        fields.Children.Add(versionInput);
        fields.Children.Add(new TextBlock { Text = "游戏加载器" });
        fields.Children.Add(loaderInput);
        fields.Children.Add(loaderVersionInput);
        fields.Children.Add(loaderVersionStatus);
        fields.Children.Add(new TextBlock
        {
            Text = "安装会下载游戏文件与匹配的 Java 运行时。Fabric、Quilt、Forge 和 NeoForge 实例还会安装对应加载器；启动游戏仍需连接 Minecraft Java 账户。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12
        });

        dialog = new ContentDialog
        {
            Title = "新建 Minecraft 实例",
            Content = fields,
            PrimaryButtonText = "创建配置",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
            XamlRoot = XamlRoot
        };

        var dialogOperation = dialog.ShowAsync();
        currentVersionLoad = LoadVersionOptionsAsync();
        ContentDialogResult dialogResult;
        try
        {
            dialogResult = await dialogOperation;
        }
        finally
        {
            loadingCancellation.Cancel();
            await currentVersionLoad;
            loadingCancellation.Dispose();
        }

        if (dialogResult != ContentDialogResult.Primary)
        {
            return;
        }

        if (versionInput.SelectedItem is not MinecraftGameVersion version)
        {
            ShowError("请选择 Minecraft 版本", "版本列表中没有可用项目。", string.Empty);
            return;
        }

        if ((loaderInput.SelectedItem as ComboBoxItem)?.Tag is not MinecraftLoaderType loaderType)
        {
            ShowError("请选择游戏加载器", "加载器选项无效。", string.Empty);
            return;
        }

        var requiresLoaderVersion = loaderType is MinecraftLoaderType.Fabric or MinecraftLoaderType.Quilt or MinecraftLoaderType.Forge or MinecraftLoaderType.NeoForge;
        var loaderVersion = requiresLoaderVersion
            ? (loaderVersionInput.SelectedItem as MinecraftLoaderVersion)?.Version
            : null;
        if (requiresLoaderVersion && string.IsNullOrWhiteSpace(loaderVersion))
        {
            ShowError($"没有可用的 {loaderType} Loader", "请检查网络连接和 Minecraft 版本后重试。", string.Empty);
            return;
        }

        try
        {
            await ViewModel.CreateProfileAsync(nameInput.Text, version.Id, loaderType, loaderVersion);
            ErrorInfoBar.IsOpen = false;
            RefreshGroupFilterOptions();
            ApplyLibraryFilter();
            UpdateLibraryListVisibility();
        }
        catch (ProfileNameInUseException)
        {
            ShowError("配置名称已存在", "请换一个名称再试。", string.Empty);
            nameInput.Focus(FocusState.Programmatic);
        }
        catch (ArgumentException exception)
        {
            ShowError("配置资料有误", exception.Message, string.Empty);
            nameInput.Focus(FocusState.Programmatic);
        }
        catch (Exception exception)
        {
            await App.CurrentApp.Logger.ErrorAsync("profile.create_failed", "Could not save an instance profile.", exception);
            ShowError("无法创建配置", "保存失败。请查看 Sakana 日志后重试。", exception.Message);
        }
    }

    private async void InstallProfile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Guid profileId })
        {
            return;
        }

        ErrorInfoBar.IsOpen = false;
        try
        {
            await ViewModel.InstallProfileAsync(profileId);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is initiated by the user from the install card or downloads page.
        }
        catch (Exception exception)
        {
            await App.CurrentApp.Logger.ErrorAsync("profile.install_failed", "The Minecraft files could not be installed.", exception);
            ShowError("安装失败", "请查看下载中心或 Sakana 日志，然后重试。", exception.Message);
        }
    }

    private void CancelInstall_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Guid profileId })
        {
            ViewModel.CancelInstall(profileId);
        }
    }

    private async void RepairProfile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Guid profileId })
        {
            return;
        }

        RepairInfoBar.IsOpen = false;
        ErrorInfoBar.IsOpen = false;
        var profileName = ViewModel.Profiles.FirstOrDefault(profile => profile.Id == profileId)?.Name ?? "实例";
        try
        {
            await ViewModel.RepairProfileAsync(profileId);
            RepairInfoBar.Severity = InfoBarSeverity.Success;
            RepairInfoBar.Title = "校验完成";
            RepairInfoBar.Message = $"{profileName} 的官方游戏文件和 Java 运行时已校验；缺失或损坏的文件已自动下载。世界存档和实例配置未被改动。";
            RepairInfoBar.IsOpen = true;
        }
        catch (OperationCanceledException)
        {
            // Repair cancellation is initiated by the user from this instance card.
        }
        catch (Exception exception)
        {
            await App.CurrentApp.Logger.ErrorAsync("profile.repair_failed", "The Minecraft instance could not be checked or repaired.", exception);
            ShowError("校验或修复失败", "请检查下载状态和 Sakana 日志，然后重试。", exception.Message);
        }
    }

    private async void LaunchSettings_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Guid profileId })
        {
            return;
        }

        var profile = ViewModel.Profiles.FirstOrDefault(item => item.Id == profileId);
        if (profile is null)
        {
            return;
        }

        var globalSettings = (await ViewModel.GetSettingsAsync()).GlobalLaunchSettings;
        var currentSettings = profile.UseGlobalLaunchSettings
            ? new InstanceLaunchSettings(
                globalSettings.UseAutomaticMemory,
                globalSettings.MinMemoryMb,
                globalSettings.MaxMemoryMb,
                globalSettings.JavaExecutablePath,
                globalSettings.CustomJavaArguments,
                ParseEnvironmentVariables(FormatEnvironmentVariables(globalSettings.CustomEnvironmentVariablesJson)),
                globalSettings.Fullscreen,
                globalSettings.WindowWidth,
                globalSettings.WindowHeight,
                UseGlobalSettings: true)
            : new InstanceLaunchSettings(
                profile.UseAutomaticMemory,
                profile.MinMemoryMb,
                profile.MaxMemoryMb,
                profile.JavaExecutablePath,
                profile.CustomJavaArguments,
                ParseEnvironmentVariables(FormatEnvironmentVariables(profile.CustomEnvironmentVariablesJson)),
                profile.Fullscreen,
                profile.WindowWidth,
                profile.WindowHeight,
                UseGlobalSettings: false);

        var automaticMemoryToggle = new ToggleSwitch
        {
            Header = "自动分配内存",
            IsOn = currentSettings.UseAutomaticMemory,
            OnContent = "自动",
            OffContent = "手动"
        };
        var minMemoryInput = new NumberBox
        {
            Header = "最小内存（MB）",
            Minimum = 256,
            Maximum = 65536,
            SmallChange = 256,
            LargeChange = 1024,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            Value = currentSettings.MinMemoryMb
        };
        var maxMemoryInput = new NumberBox
        {
            Header = "最大内存（MB）",
            Minimum = 256,
            Maximum = 65536,
            SmallChange = 256,
            LargeChange = 1024,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            Value = currentSettings.MaxMemoryMb
        };
        var manualMemoryPanel = new StackPanel { Spacing = 8 };
        manualMemoryPanel.Children.Add(minMemoryInput);
        manualMemoryPanel.Children.Add(maxMemoryInput);
        manualMemoryPanel.Children.Add(new TextBlock
        {
            Text = "最大内存作为 Minecraft 的 Java 堆上限。大型整合包通常需要更多内存，请给 Windows 和其他程序留出空间。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12
        });
        automaticMemoryToggle.Toggled += (_, _) =>
        {
            minMemoryInput.IsEnabled = !automaticMemoryToggle.IsOn;
            maxMemoryInput.IsEnabled = !automaticMemoryToggle.IsOn;
        };
        minMemoryInput.IsEnabled = !automaticMemoryToggle.IsOn;
        maxMemoryInput.IsEnabled = !automaticMemoryToggle.IsOn;

        var customJavaToggle = new ToggleSwitch
        {
            Header = "使用自定义 Java",
            IsOn = !string.IsNullOrWhiteSpace(currentSettings.JavaExecutablePath),
            OnContent = "自定义",
            OffContent = "自动匹配"
        };
        var javaPathLabel = new TextBlock { Text = "Java 可执行文件", Foreground = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["SakanaMutedBrush"] };
        var javaPathInput = new TextBox
        {
            PlaceholderText = "留空时自动选择适配 Minecraft 的 Java",
            Text = currentSettings.JavaExecutablePath ?? string.Empty,
            IsEnabled = customJavaToggle.IsOn,
            Height = 40,
            VerticalContentAlignment = VerticalAlignment.Center,
            MinWidth = 0
        };
        var javaBrowseButton = new Button { Content = "浏览 java.exe", IsEnabled = customJavaToggle.IsOn, Height = 40, VerticalAlignment = VerticalAlignment.Center };
        var javaResetButton = new Button { Content = "自动匹配", IsEnabled = customJavaToggle.IsOn, Height = 40, VerticalAlignment = VerticalAlignment.Center };
        customJavaToggle.Toggled += (_, _) =>
        {
            javaPathInput.IsEnabled = customJavaToggle.IsOn;
            javaBrowseButton.IsEnabled = customJavaToggle.IsOn;
            javaResetButton.IsEnabled = customJavaToggle.IsOn;
        };
        javaBrowseButton.Click += async (_, _) =>
        {
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(App.CurrentApp.WindowHandle);
            var picker = new FileOpenPicker(windowId);
            picker.FileTypeFilter.Add(".exe");
            picker.CommitButtonText = "选择 Java";
            var selectedFile = await picker.PickSingleFileAsync();
            if (selectedFile is not null)
            {
                javaPathInput.Text = selectedFile.Path;
            }
        };
        javaResetButton.Click += (_, _) =>
        {
            customJavaToggle.IsOn = false;
            javaPathInput.Text = string.Empty;
        };
        var javaPathControls = new Grid { ColumnSpacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch };
        javaPathControls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        javaPathControls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        javaPathControls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(javaBrowseButton, 1);
        Grid.SetColumn(javaResetButton, 2);
        javaPathControls.Children.Add(javaPathInput);
        javaPathControls.Children.Add(javaBrowseButton);
        javaPathControls.Children.Add(javaResetButton);

        var javaArgumentsInput = new TextBox
        {
            Header = "自定义 Java 参数",
            PlaceholderText = "例如：-XX:+UseG1GC -XX:+UnlockExperimentalVMOptions",
            Text = currentSettings.CustomJavaArguments,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 60
        };
        var environmentVariablesInput = new TextBox
        {
            Header = "自定义环境变量",
            PlaceholderText = "每行一项，例如：\nMALLOC_ARENA_MAX=2\nJAVA_TOOL_OPTIONS=-Dfile.encoding=UTF-8",
            Text = string.Join(Environment.NewLine, currentSettings.CustomEnvironmentVariables.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => $"{pair.Key}={pair.Value}")),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 80
        };

        var javaMemoryFields = new StackPanel { Spacing = 12, Padding = new Thickness(4) };
        javaMemoryFields.Children.Add(new TextBlock
        {
            Text = "此实例使用独立的 Java 和内存配置。自动 Java 会选择适配此 Minecraft 版本的 Sakana 运行时。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12
        });
        javaMemoryFields.Children.Add(automaticMemoryToggle);
        javaMemoryFields.Children.Add(new TextBlock
        {
            Text = "自动模式会在每次启动时按设备可供 Java 使用的内存计算堆上限（1–16 GB）；游戏运行后不会热调整。手动模式使用下方的最小和最大值。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12
        });
        javaMemoryFields.Children.Add(manualMemoryPanel);
        javaMemoryFields.Children.Add(customJavaToggle);
        javaMemoryFields.Children.Add(javaPathLabel);
        javaMemoryFields.Children.Add(javaPathControls);
        javaMemoryFields.Children.Add(javaArgumentsInput);
        javaMemoryFields.Children.Add(environmentVariablesInput);

        var fullscreenToggle = new ToggleSwitch
        {
            Header = "全屏启动",
            IsOn = currentSettings.Fullscreen,
            OnContent = "全屏",
            OffContent = "窗口"
        };
        var widthInput = new NumberBox
        {
            Header = "窗口宽度",
            Minimum = 640,
            Maximum = 16384,
            SmallChange = 16,
            LargeChange = 100,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            Value = currentSettings.WindowWidth,
            IsEnabled = !currentSettings.Fullscreen
        };
        var heightInput = new NumberBox
        {
            Header = "窗口高度",
            Minimum = 480,
            Maximum = 16384,
            SmallChange = 16,
            LargeChange = 100,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            Value = currentSettings.WindowHeight,
            IsEnabled = !currentSettings.Fullscreen
        };
        fullscreenToggle.Toggled += (_, _) =>
        {
            widthInput.IsEnabled = !fullscreenToggle.IsOn;
            heightInput.IsEnabled = !fullscreenToggle.IsOn;
        };
        var windowFields = new StackPanel { Spacing = 12, Padding = new Thickness(4) };
        windowFields.Children.Add(new TextBlock
        {
            Text = "窗口尺寸会在启动时传给 Minecraft。全屏模式会同步更新此实例的 options.txt。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12
        });
        windowFields.Children.Add(fullscreenToggle);
        windowFields.Children.Add(widthInput);
        windowFields.Children.Add(heightInput);

        var tabs = new Pivot();
        tabs.Items.Add(new PivotItem
        {
            Header = "Java 和内存",
            Content = new ScrollViewer { Content = javaMemoryFields, MaxHeight = 520, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }
        });
        tabs.Items.Add(new PivotItem
        {
            Header = "游戏窗口",
            Content = new ScrollViewer { Content = windowFields, MaxHeight = 520, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }
        });
        var useGlobalSettingsToggle = new ToggleSwitch
        {
            Header = "使用全局启动设置",
            IsOn = profile.UseGlobalLaunchSettings,
            OnContent = "跟随全局",
            OffContent = "使用此实例设置"
        };
        useGlobalSettingsToggle.Toggled += (_, _) => tabs.IsEnabled = !useGlobalSettingsToggle.IsOn;
        tabs.IsEnabled = !useGlobalSettingsToggle.IsOn;
        var dialogContent = new StackPanel { Spacing = 8 };
        dialogContent.Children.Add(useGlobalSettingsToggle);
        dialogContent.Children.Add(tabs);

        var dialog = new ContentDialog
        {
            Title = $"{profile.Name} · 启动设置",
            Content = dialogContent,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        if (!useGlobalSettingsToggle.IsOn && customJavaToggle.IsOn && string.IsNullOrWhiteSpace(javaPathInput.Text))
        {
            ShowError("Java 路径为空", "请选择 java.exe，或将 Java 切换为自动匹配。", string.Empty);
            return;
        }

        if (!double.IsFinite(minMemoryInput.Value) || !double.IsFinite(maxMemoryInput.Value) ||
            minMemoryInput.Value != Math.Truncate(minMemoryInput.Value) || maxMemoryInput.Value != Math.Truncate(maxMemoryInput.Value) ||
            !double.IsFinite(widthInput.Value) || !double.IsFinite(heightInput.Value) ||
            widthInput.Value != Math.Truncate(widthInput.Value) || heightInput.Value != Math.Truncate(heightInput.Value))
        {
            ShowError("设置数值无效", "内存和窗口尺寸都必须是整数。", string.Empty);
            return;
        }

        try
        {
            var environmentVariables = ParseEnvironmentVariables(environmentVariablesInput.Text);
            var settings = new InstanceLaunchSettings(
                automaticMemoryToggle.IsOn,
                (int)minMemoryInput.Value,
                (int)maxMemoryInput.Value,
                customJavaToggle.IsOn ? javaPathInput.Text.Trim() : null,
                javaArgumentsInput.Text,
                environmentVariables,
                fullscreenToggle.IsOn,
                (int)widthInput.Value,
                (int)heightInput.Value,
                useGlobalSettingsToggle.IsOn);
            await ViewModel.SaveInstanceLaunchSettingsAsync(profileId, settings);
            RepairInfoBar.IsOpen = false;
            ErrorInfoBar.IsOpen = false;
        }
        catch (Exception exception)
        {
            await App.CurrentApp.Logger.ErrorAsync("profile.memory_settings_failed", "Instance launch memory settings could not be saved.", exception);
            ShowError("无法保存启动设置", exception.Message, string.Empty);
        }
    }

    private async void LaunchProfile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Guid profileId })
        {
            return;
        }

        ErrorInfoBar.IsOpen = false;
        try
        {
            await ViewModel.LaunchProfileAsync(profileId);
        }
        catch (MinecraftAccountRequiredException exception)
        {
            await App.CurrentApp.Logger.InformationAsync("profile.launch_needs_account", exception.Message);
            App.CurrentApp.RequestNavigation("accounts");
        }
        catch (Exception exception)
        {
            await App.CurrentApp.Logger.ErrorAsync("profile.launch_failed", "Minecraft could not be started.", exception);
            ShowError("启动失败", "请检查账户、游戏文件和 Java 运行时状态。", exception.Message);
        }
    }

    private async void StopProfile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Guid profileId })
        {
            return;
        }

        try
        {
            await ViewModel.StopProfileAsync(profileId);
        }
        catch (Exception exception)
        {
            await App.CurrentApp.Logger.ErrorAsync("profile.stop_failed", "Minecraft could not be stopped.", exception);
            ShowError("无法停止游戏", "请稍后重试。", exception.Message);
        }
    }

    private void ShowError(string title, string message, string details)
    {
        ErrorInfoBar.Title = title;
        ErrorInfoBar.Message = string.IsNullOrWhiteSpace(details) ? message : $"{message} {details}";
        ErrorInfoBar.IsOpen = true;
    }
}
