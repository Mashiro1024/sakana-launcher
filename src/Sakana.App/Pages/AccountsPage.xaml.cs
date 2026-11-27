using Sakana.Application;
using Sakana.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;

namespace Sakana.App.Pages;

public sealed partial class AccountsPage : Page
{
    private const double SkinPreviewPixelSize = 5;
    private const double SkinTexturePreviewWidth = 64 * SkinPreviewPixelSize;
    private readonly IMinecraftAccountService _accountService;
    private CancellationTokenSource? _signInCancellation;
    private BitmapImage? _skinTexture;
    private int _skinPreviewGeneration;
    private bool _showSkinBack;
    private bool _usingDefaultSkinFallback;
    private double? _skinDragStartX;
    private uint? _skinDragPointerId;
    private bool _skinIsSlim;
    private string _clientId = string.Empty;
    private string _accountStatus = "尚未连接账户";

    public AccountsPage() : this(App.CurrentApp.MinecraftAccountService)
    {
    }

    public AccountsPage(IMinecraftAccountService accountService)
    {
        _accountService = accountService;
        InitializeComponent();
        Loaded += Page_Loaded;
        Unloaded += Page_Unloaded;
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshAccountStateAsync();
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        _signInCancellation?.Cancel();
    }

    private async Task RefreshAccountStateAsync()
    {
        try
        {
            _clientId = LauncherSettings.MicrosoftOAuthClientId;
            await RefreshSavedAccountsAsync();
            var session = await _accountService.GetCurrentSessionAsync(_clientId);
            if (session is null)
            {
                await RefreshSavedAccountsAsync();
                SetDisconnected("尚未连接账户");
                return;
            }

            SetConnected(session.PlayerName);
            await SetSkinPreviewAsync(session);
            await RefreshSavedAccountsAsync();
        }
        catch (Exception exception)
        {
            await App.CurrentApp.Logger.ErrorAsync("account.refresh_failed", "Could not refresh the connected Minecraft account.", exception);
            SetDisconnected("账户状态无法验证", canSignOut: true);
            AccountInfoBar.Severity = InfoBarSeverity.Error;
            AccountInfoBar.Title = "账户状态无法验证";
            AccountInfoBar.Message = exception.Message;
            AccountInfoBar.IsOpen = true;
            if (exception is MinecraftClientIdNotAuthorizedException)
            {
                ShowClientIdAuthorizationRequired();
            }
        }
    }

    private async void SignIn_Click(object sender, RoutedEventArgs e)
    {
        if (SignInProgress.IsActive)
        {
            return;
        }

        try
        {
            AccountInfoBar.IsOpen = false;
            ClientIdApprovalLink.Visibility = Visibility.Collapsed;
            _clientId = LauncherSettings.MicrosoftOAuthClientId;

            _signInCancellation = new CancellationTokenSource();
            SetBusy(true, "正在向 Microsoft 请求登录代码…");
            var prompt = await _accountService.BeginSignInAsync(_clientId, _signInCancellation.Token);

            var codeBox = new TextBox
            {
                Text = prompt.UserCode,
                IsReadOnly = true,
                FontSize = 24,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            var copyButton = new Button { Content = "复制代码", HorizontalAlignment = HorizontalAlignment.Left };
            copyButton.Click += (_, _) =>
            {
                var package = new DataPackage();
                package.SetText(prompt.UserCode);
                Clipboard.SetContent(package);
                AccountInfoBar.Severity = InfoBarSeverity.Success;
                AccountInfoBar.Message = "登录代码已复制。";
                AccountInfoBar.IsOpen = true;
            };
            var dialogContent = new StackPanel { Spacing = 12, Width = 420 };
            dialogContent.Children.Add(new TextBlock
            {
                Text = "在浏览器中打开 Microsoft 登录页面，输入以下一次性代码并完成账户授权。",
                TextWrapping = TextWrapping.Wrap
            });
            dialogContent.Children.Add(new HyperlinkButton
            {
                Content = prompt.VerificationUri,
                NavigateUri = new Uri(prompt.VerificationUri),
                Padding = new Thickness(0)
            });
            dialogContent.Children.Add(codeBox);
            dialogContent.Children.Add(copyButton);
            dialogContent.Children.Add(new TextBlock
            {
                Text = $"代码约 {Math.Max(1, prompt.ExpiresInSeconds / 60)} 分钟后过期。",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap
            });

            SetBusy(false, string.Empty);
            var dialog = new ContentDialog
            {
                Title = "连接 Microsoft 账户",
                Content = dialogContent,
                PrimaryButtonText = "我已完成授权",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            AccountInfoBar.IsOpen = false;
            SetBusy(true, "正在验证 Microsoft 与 Minecraft 账户…");
            var session = await _accountService.CompleteSignInAsync(_clientId, prompt, _signInCancellation.Token);
            SetConnected(session.PlayerName);
            await SetSkinPreviewAsync(session);
            await RefreshSavedAccountsAsync();
            AccountInfoBar.Severity = InfoBarSeverity.Success;
            AccountInfoBar.Title = "账户已连接";
            AccountInfoBar.Message = $"已验证 Minecraft Java Edition 资料：{session.PlayerName}。";
            AccountInfoBar.IsOpen = true;
            await App.CurrentApp.Logger.InformationAsync("account.sign_in_succeeded", "A Minecraft Java Edition account was connected.");
        }
        catch (OperationCanceledException)
        {
            AccountInfoBar.Severity = InfoBarSeverity.Informational;
            AccountInfoBar.Title = "登录已取消";
            AccountInfoBar.Message = "没有更改已保存的账户。";
            AccountInfoBar.IsOpen = true;
        }
        catch (MinecraftClientIdNotAuthorizedException exception)
        {
            await App.CurrentApp.Logger.ErrorAsync("account.client_id_not_authorized", "Minecraft Services rejected the configured Microsoft client ID.", exception);
            ShowClientIdAuthorizationRequired();
        }
        catch (Exception exception)
        {
            await App.CurrentApp.Logger.ErrorAsync("account.sign_in_failed", "Microsoft/Minecraft account sign-in failed.", exception);
            SetDisconnected("登录未完成", canSignOut: true);
            AccountInfoBar.Severity = InfoBarSeverity.Error;
            AccountInfoBar.Title = "账户登录失败";
            AccountInfoBar.Message = exception.Message;
            AccountInfoBar.IsOpen = true;
        }
        finally
        {
            SetBusy(false, string.Empty);
            _signInCancellation?.Dispose();
            _signInCancellation = null;
        }
    }

    private async void SignOut_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _accountService.SignOutAsync();
            await RefreshAccountStateAsync();
            AccountInfoBar.Severity = InfoBarSeverity.Success;
            AccountInfoBar.Title = "已退出当前账户";
            AccountInfoBar.Message = "当前账户凭据已从此设备移除；如果还有其他已保存账户，Sakana 已自动切换。";
            AccountInfoBar.IsOpen = true;
            await App.CurrentApp.Logger.InformationAsync("account.sign_out_succeeded", "The active Microsoft account credential was removed.");
        }
        catch (Exception exception)
        {
            await App.CurrentApp.Logger.ErrorAsync("account.sign_out_failed", "Could not clear Microsoft account credentials.", exception);
            AccountInfoBar.Severity = InfoBarSeverity.Error;
            AccountInfoBar.Title = "无法退出登录";
            AccountInfoBar.Message = "请查看 Sakana 日志。";
            AccountInfoBar.IsOpen = true;
        }
    }

    private void CancelSignIn_Click(object sender, RoutedEventArgs e) => _signInCancellation?.Cancel();

    private async void ManageMinecraftSkin_Click(object sender, RoutedEventArgs e)
    {
        const string skinProfileUrl = "https://www.minecraft.net/en-us/msaprofile/mygames/editskin";
        try
        {
            var launched = await Windows.System.Launcher.LaunchUriAsync(new Uri(skinProfileUrl));
            if (!launched)
            {
                AccountInfoBar.Severity = InfoBarSeverity.Warning;
                AccountInfoBar.Title = "无法打开 Minecraft 账户页面";
                AccountInfoBar.Message = "请检查 Windows 的默认浏览器设置，然后重试。";
                AccountInfoBar.IsOpen = true;
            }
        }
        catch (Exception exception)
        {
            await App.CurrentApp.Logger.ErrorAsync("account.open_skin_profile_failed", "Could not open the official Minecraft Java skin profile page.", exception);
            AccountInfoBar.Severity = InfoBarSeverity.Error;
            AccountInfoBar.Title = "无法打开 Minecraft 账户页面";
            AccountInfoBar.Message = exception.Message;
            AccountInfoBar.IsOpen = true;
        }
    }

    private Task SetSkinPreviewAsync(MinecraftAccountSession session)
    {
        var generation = ++_skinPreviewGeneration;
        CurrentSkinPreviewCard.Visibility = Visibility.Visible;
        SkinModelCanvas.Children.Clear();
        SkinPreviewEmptyState.Visibility = Visibility.Visible;
        SkinPreviewEmptyStateText.Text = "正在加载皮肤…";
        _skinTexture = null;
        _showSkinBack = false;
        _usingDefaultSkinFallback = false;
        _skinDragStartX = null;
        _skinDragPointerId = null;
        SkinOuterLayerToggle.IsOn = true;
        SkinOuterLayerToggle.IsEnabled = true;
        SkinViewSideButton.IsEnabled = false;
        SkinViewSideButton.Content = "显示背面";

        var isSlim = string.Equals(session.SkinVariant, "SLIM", StringComparison.OrdinalIgnoreCase);
        _skinIsSlim = isSlim;
        if (!Uri.TryCreate(session.SkinTextureUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, "textures.minecraft.net", StringComparison.OrdinalIgnoreCase))
        {
            ShowDefaultSkinFallback(isSlim, "Minecraft 未提供可预览的皮肤纹理。");
            return Task.CompletedTask;
        }

        SkinVariantText.Text = isSlim
            ? "Alex 模型 · 窄臂"
            : "Classic 模型 · 宽臂";
        SkinPreviewStatusText.Text = "正在加载完整皮肤预览…";

        var bitmap = new BitmapImage();
        bitmap.ImageOpened += (_, _) =>
        {
            if (generation == _skinPreviewGeneration)
            {
                ConfigureSkinModel(bitmap, isSlim);
            }
        };
        bitmap.ImageFailed += (_, _) =>
        {
            if (generation == _skinPreviewGeneration)
            {
                _skinTexture = null;
                ShowDefaultSkinFallback(isSlim, "真实皮肤纹理暂时无法加载。");
            }
        };
        _skinTexture = bitmap;
        bitmap.UriSource = uri;
        return Task.CompletedTask;
    }

    private void ConfigureSkinModel(BitmapImage bitmap, bool isSlim)
    {
        var width = bitmap.PixelWidth;
        var height = bitmap.PixelHeight;
        if (width < 64 || width > 1024 || width % 64 != 0)
        {
            ShowDefaultSkinFallback(isSlim, "当前皮肤纹理格式暂不支持预览。");
            return;
        }

        var textureScale = width / 64;
        var hasModernLayout = height == 64 * textureScale;
        var hasLegacyLayout = height == 32 * textureScale;
        if (!hasModernLayout && !hasLegacyLayout)
        {
            ShowDefaultSkinFallback(isSlim, "当前皮肤纹理格式暂不支持预览。");
            return;
        }

        _skinTexture = bitmap;
        _usingDefaultSkinFallback = false;
        SkinModelCanvas.Children.Clear();
        SkinPreviewEmptyState.Visibility = Visibility.Collapsed;
        SkinViewSideButton.IsEnabled = true;
        SkinOuterLayerToggle.IsEnabled = true;
        if (hasLegacyLayout)
        {
            SkinOuterLayerToggle.Header = "显示帽子外层";
        }
        else
        {
            SkinOuterLayerToggle.Header = "显示皮肤外层";
        }

        var isBack = _showSkinBack;
        var leftArmWidth = isSlim ? 3 : 4;
        var rightArmWidth = isSlim ? 3 : 4;
        const double center = 112;
        const double headTop = 12;
        const double bodyTop = 52;
        const double legsTop = 112;

        // Render the six visible front-facing surfaces. Keeping every part pixel aligned
        // preserves the crisp texture layout while fitting in the compact WinUI preview card.
        AddSkinPart(isBack ? 24 : 8, 8, 8, 8, center - 20, headTop);
        AddSkinPart(isBack ? 32 : 20, 20, 8, 12, center - 20, bodyTop);
        AddSkinPart(isBack ? 52 : 44, 20, rightArmWidth, 12, center - 40, bodyTop);
        AddSkinPart(isBack ? 44 : 36, 52, leftArmWidth, 12, center + 20, bodyTop,
            useLegacyRightLimb: hasLegacyLayout, legacyU: 44, legacyV: 20);
        AddSkinPart(isBack ? 12 : 4, 20, 4, 12, center - 20, legsTop);
        AddSkinPart(isBack ? 28 : 20, 52, 4, 12, center, legsTop,
            useLegacyRightLimb: hasLegacyLayout, legacyU: 4, legacyV: 20);

        if (SkinOuterLayerToggle.IsOn)
        {
            AddSkinPart(isBack ? 56 : 40, 8, 8, 8, center - 20, headTop);
            if (hasModernLayout)
            {
                AddSkinPart(isBack ? 32 : 20, 36, 8, 12, center - 20, bodyTop);
                AddSkinPart(isBack ? 52 : 44, 36, rightArmWidth, 12, center - 40, bodyTop);
                AddSkinPart(isBack ? 60 : 52, 52, leftArmWidth, 12, center + 20, bodyTop);
                AddSkinPart(isBack ? 12 : 4, 36, 4, 12, center - 20, legsTop);
                AddSkinPart(isBack ? 12 : 4, 52, 4, 12, center, legsTop);
            }
        }

        var side = isBack ? "背面" : "正面";
        SkinViewSideButton.Content = isBack ? "显示正面" : "显示背面";
        SkinPreviewStatusText.Text = hasLegacyLayout
            ? $"当前视角：{side}。旧版 64×32 皮肤仅支持帽子外层，左右肢体使用相同纹理。"
            : $"当前视角：{side}。可切换皮肤外层，或拖动预览查看另一面。";
    }

    private void AddSkinPart(
        int u,
        int v,
        int sourceWidth,
        int sourceHeight,
        double x,
        double y,
        bool useLegacyRightLimb = false,
        int legacyU = 0,
        int legacyV = 0)
    {
        if (_skinTexture is null)
        {
            return;
        }

        if (useLegacyRightLimb)
        {
            u = legacyU;
            v = legacyV;
        }

        var partWidth = sourceWidth * SkinPreviewPixelSize;
        var partHeight = sourceHeight * SkinPreviewPixelSize;
        var part = new Canvas
        {
            Width = partWidth,
            Height = partHeight,
            Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, partWidth, partHeight) }
        };
        var texture = new Image
        {
            Source = _skinTexture,
            Width = SkinTexturePreviewWidth,
            Height = _skinTexture.PixelHeight / (double)(_skinTexture.PixelWidth / 64) * SkinPreviewPixelSize,
            Stretch = Stretch.Fill
        };
        Canvas.SetLeft(texture, -u * SkinPreviewPixelSize);
        Canvas.SetTop(texture, -v * SkinPreviewPixelSize);
        part.Children.Add(texture);
        Canvas.SetLeft(part, x);
        Canvas.SetTop(part, y);
        SkinModelCanvas.Children.Add(part);
    }

    private void SkinOuterLayerToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_skinTexture is not null && _skinTexture.PixelWidth > 0)
        {
            ConfigureSkinModel(_skinTexture, _skinIsSlim);
        }
    }

    private void SkinViewSideButton_Click(object sender, RoutedEventArgs e)
    {
        _showSkinBack = !_showSkinBack;
        if (_skinTexture is not null)
        {
            ConfigureSkinModel(_skinTexture, _skinIsSlim);
        }
        else if (_usingDefaultSkinFallback)
        {
            RenderDefaultSkinFallback();
        }
    }

    private void ShowDefaultSkinFallback(bool isSlim, string reason)
    {
        _skinTexture = null;
        _skinIsSlim = isSlim;
        _usingDefaultSkinFallback = true;
        SkinVariantText.Text = isSlim ? "默认角色示意 · Alex 窄臂" : "默认角色示意 · Classic 宽臂";
        SkinOuterLayerToggle.Header = "真实皮肤外层";
        SkinOuterLayerToggle.IsOn = false;
        SkinOuterLayerToggle.IsEnabled = false;
        SkinViewSideButton.IsEnabled = true;
        SkinPreviewEmptyState.Visibility = Visibility.Collapsed;
        RenderDefaultSkinFallback();
        SkinPreviewStatusText.Text = $"{reason}此处暂显示默认角色示意；设置或更新皮肤后重新打开账户页即可查看。";
    }

    private void RenderDefaultSkinFallback()
    {
        SkinModelCanvas.Children.Clear();
        SkinPreviewEmptyState.Visibility = Visibility.Collapsed;
        var center = 112d;
        var armWidth = _skinIsSlim ? 15d : 20d;
        var headX = center - 20;
        var headY = 12d;
        var bodyX = center - 20;
        var bodyY = 52d;
        var legY = 112d;
        var leftArmX = center - 40;
        var rightArmX = center + 20;

        var skin = Windows.UI.Color.FromArgb(255, 198, 139, 92);
        var hair = Windows.UI.Color.FromArgb(255, 76, 53, 39);
        var shirt = Windows.UI.Color.FromArgb(255, 55, 104, 166);
        var pants = Windows.UI.Color.FromArgb(255, 57, 72, 127);
        var boots = Windows.UI.Color.FromArgb(255, 50, 47, 45);
        var white = Windows.UI.Color.FromArgb(255, 238, 238, 226);
        var eye = Windows.UI.Color.FromArgb(255, 55, 102, 161);
        var mouth = Windows.UI.Color.FromArgb(255, 112, 58, 49);

        // Head
        AddDefaultSkinBlock(headX, headY, 40, 40, _showSkinBack ? hair : skin);
        if (_showSkinBack)
        {
            AddDefaultSkinBlock(headX + 5, headY + 5, 30, 10, hair);
        }
        else
        {
            AddDefaultSkinBlock(headX, headY, 40, 10, hair);
            AddDefaultSkinBlock(headX, headY + 10, 5, 10, hair);
            AddDefaultSkinBlock(headX + 35, headY + 10, 5, 10, hair);
            AddDefaultSkinBlock(headX + 10, headY + 20, 5, 5, white);
            AddDefaultSkinBlock(headX + 25, headY + 20, 5, 5, white);
            AddDefaultSkinBlock(headX + 15, headY + 20, 5, 5, eye);
            AddDefaultSkinBlock(headX + 25, headY + 20, 5, 5, eye);
            AddDefaultSkinBlock(headX + 15, headY + 30, 10, 5, mouth);
        }

        // Torso and arms
        AddDefaultSkinBlock(bodyX, bodyY, 40, 60, shirt);
        AddDefaultSkinBlock(bodyX + 15, bodyY, 10, 5, skin);
        AddDefaultSkinBlock(leftArmX, bodyY, armWidth, 40, shirt);
        AddDefaultSkinBlock(leftArmX, bodyY + 40, armWidth, 20, skin);
        AddDefaultSkinBlock(rightArmX, bodyY, armWidth, 40, shirt);
        AddDefaultSkinBlock(rightArmX, bodyY + 40, armWidth, 20, skin);

        // Legs and boots
        AddDefaultSkinBlock(bodyX, legY, 20, 50, pants);
        AddDefaultSkinBlock(bodyX, legY + 50, 20, 10, boots);
        AddDefaultSkinBlock(bodyX + 20, legY, 20, 50, pants);
        AddDefaultSkinBlock(bodyX + 20, legY + 50, 20, 10, boots);
        SkinViewSideButton.Content = _showSkinBack ? "显示正面" : "显示背面";
    }

    private void AddDefaultSkinBlock(double x, double y, double width, double height, Windows.UI.Color color)
    {
        var block = new Rectangle
        {
            Width = width,
            Height = height,
            Fill = new SolidColorBrush(color)
        };
        Canvas.SetLeft(block, x);
        Canvas.SetTop(block, y);
        SkinModelCanvas.Children.Add(block);
    }

    private void SkinModelCanvas_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _skinDragStartX = e.GetCurrentPoint(SkinModelCanvas).Position.X;
        _skinDragPointerId = e.Pointer.PointerId;
        SkinModelCanvas.CapturePointer(e.Pointer);
    }

    private void SkinModelCanvas_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_skinDragStartX is double startX && _skinDragPointerId == e.Pointer.PointerId)
        {
            var endX = e.GetCurrentPoint(SkinModelCanvas).Position.X;
            if (Math.Abs(endX - startX) >= 24)
            {
                SkinViewSideButton_Click(sender, e);
            }
        }

        SkinModelCanvas.ReleasePointerCapture(e.Pointer);
        _skinDragStartX = null;
        _skinDragPointerId = null;
    }

    private async Task RefreshSavedAccountsAsync()
    {
        var accounts = await _accountService.GetAccountsAsync(_clientId);
        SavedAccountsListPanel.Children.Clear();
        SavedAccountsCard.Visibility = accounts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AccountsEmptyText.Visibility = accounts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var account in accounts)
        {
            var rowGrid = new Grid { ColumnSpacing = 14, VerticalAlignment = VerticalAlignment.Center };
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var avatar = new Border
            {
                Width = 42,
                Height = 42,
                CornerRadius = new CornerRadius(14),
                Background = (Brush)App.CurrentApp.Resources["SakanaAccentSoftBrush"],
                VerticalAlignment = VerticalAlignment.Center,
                Child = new SymbolIcon { Symbol = Symbol.Contact, Foreground = (Brush)App.CurrentApp.Resources["SakanaAccentBrush"] }
            };
            rowGrid.Children.Add(avatar);

            var identity = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            identity.Children.Add(new TextBlock
            {
                Text = account.PlayerName,
                FontSize = 15,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = (Brush)App.CurrentApp.Resources["SakanaTextBrush"]
            });
            identity.Children.Add(new TextBlock
            {
                Text = account.NeedsVerification ? "需要重新验证账户资料" : account.PlayerUuid,
                FontSize = 12,
                Foreground = (Brush)App.CurrentApp.Resources["SakanaMutedBrush"]
            });
            Grid.SetColumn(identity, 1);
            rowGrid.Children.Add(identity);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            if (account.IsActive)
            {
                actions.Children.Add(new Button { Content = "当前账户", IsEnabled = false });
            }
            else if (!account.NeedsVerification)
            {
                var makeCurrentButton = new Button { Content = "设为当前", Tag = account.PlayerUuid };
                makeCurrentButton.Click += SetActiveAccount_Click;
                actions.Children.Add(makeCurrentButton);
            }

            var removeButton = new Button { Content = "移除", Tag = account.PlayerUuid };
            removeButton.Click += RemoveSavedAccount_Click;
            actions.Children.Add(removeButton);
            Grid.SetColumn(actions, 2);
            rowGrid.Children.Add(actions);

            SavedAccountsListPanel.Children.Add(new Border
            {
                Padding = new Thickness(12),
                CornerRadius = new CornerRadius(10),
                Background = (Brush)App.CurrentApp.Resources["SakanaSurfaceRaisedBrush"],
                Child = rowGrid
            });
        }
    }

    private async void SetActiveAccount_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string playerUuid } || string.IsNullOrWhiteSpace(playerUuid))
        {
            return;
        }

        try
        {
            await _accountService.SetActiveAccountAsync(playerUuid);
            await RefreshAccountStateAsync();
            AccountInfoBar.Severity = InfoBarSeverity.Success;
            AccountInfoBar.Title = "已切换账户";
            AccountInfoBar.Message = "后续启动 Minecraft 时将使用此账户。";
            AccountInfoBar.IsOpen = true;
        }
        catch (Exception exception)
        {
            await App.CurrentApp.Logger.ErrorAsync("account.switch_failed", "Could not switch the active Minecraft account.", exception);
            AccountInfoBar.Severity = InfoBarSeverity.Error;
            AccountInfoBar.Title = "无法切换账户";
            AccountInfoBar.Message = exception.Message;
            AccountInfoBar.IsOpen = true;
        }
    }

    private async void RemoveSavedAccount_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string playerUuid })
        {
            return;
        }

        try
        {
            var account = (await _accountService.GetAccountsAsync(_clientId))
                .FirstOrDefault(item => string.Equals(item.PlayerUuid, playerUuid, StringComparison.OrdinalIgnoreCase));
            var dialog = new ContentDialog
            {
                Title = "移除此账户？",
                Content = $"将从此设备移除“{account?.PlayerName ?? "待验证的账户"}”的加密登录凭据。之后仍可重新登录。",
                PrimaryButtonText = "移除账户",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            await _accountService.RemoveAccountAsync(playerUuid);
            await RefreshAccountStateAsync();
            AccountInfoBar.Severity = InfoBarSeverity.Success;
            AccountInfoBar.Title = "账户已移除";
            AccountInfoBar.Message = "该账户的加密登录凭据已从此设备删除。";
            AccountInfoBar.IsOpen = true;
        }
        catch (Exception exception)
        {
            await App.CurrentApp.Logger.ErrorAsync("account.remove_failed", "Could not remove a saved Minecraft account.", exception);
            AccountInfoBar.Severity = InfoBarSeverity.Error;
            AccountInfoBar.Title = "无法移除账户";
            AccountInfoBar.Message = exception.Message;
            AccountInfoBar.IsOpen = true;
        }
    }

    private void ShowClientIdAuthorizationRequired()
    {
        _accountStatus = "等待 Minecraft 应用授权";
        AccountStatusText.Text = _accountStatus;
        AccountStatusDot.Fill = (Brush)App.CurrentApp.Resources["SakanaMutedBrush"];
        SignInButton.Visibility = Visibility.Visible;
        SignOutButton.Visibility = SavedAccountsListPanel.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ClientIdApprovalLink.Visibility = Visibility.Visible;
        AccountInfoBar.Severity = InfoBarSeverity.Warning;
        AccountInfoBar.Title = "Minecraft 应用授权已获批";
        AccountInfoBar.Message = "Microsoft 与 Xbox 验证已通过，但 Minecraft Services 仍拒绝当前应用的 Client ID。Sakana 使用的应用已通过审核；请确认审核对应此版本使用的 Client ID，并稍后重试。切换 Microsoft 账户不会解决应用注册授权问题。";
        AccountInfoBar.IsOpen = true;
    }

    private void SetConnected(string playerName)
    {
        ClientIdApprovalLink.Visibility = Visibility.Collapsed;
        ManageSkinButton.Visibility = Visibility.Visible;
        _accountStatus = playerName;
        AccountStatusText.Text = _accountStatus;
        AccountStatusDot.Fill = (Brush)App.CurrentApp.Resources["SakanaAccentBrush"];
        SignInButton.Content = "添加账户";
        SignInButton.Visibility = Visibility.Visible;
        SignOutButton.Visibility = Visibility.Visible;
    }

    private void SetDisconnected(string status, bool canSignOut = false)
    {
        ClientIdApprovalLink.Visibility = Visibility.Collapsed;
        ManageSkinButton.Visibility = Visibility.Collapsed;
        CurrentSkinPreviewCard.Visibility = Visibility.Collapsed;
        SkinModelCanvas.Children.Clear();
        SkinPreviewEmptyState.Visibility = Visibility.Collapsed;
        _skinTexture = null;
        _skinIsSlim = false;
        _skinPreviewGeneration++;
        _accountStatus = status;
        AccountStatusText.Text = _accountStatus;
        AccountStatusDot.Fill = (Brush)App.CurrentApp.Resources["SakanaMutedBrush"];
        SignInButton.Content = "登录 Microsoft 账户";
        SignInButton.Visibility = Visibility.Visible;
        SignOutButton.Visibility = canSignOut ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetBusy(bool isBusy, string message)
    {
        SignInProgress.IsActive = isBusy;
        SignInProgress.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
        SignInButton.IsEnabled = !isBusy;
        SignOutButton.IsEnabled = !isBusy;
        CancelSignInButton.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
        if (isBusy)
        {
            AccountStatusText.Text = message;
        }
        else
        {
            AccountStatusText.Text = _accountStatus;
        }
    }
}
