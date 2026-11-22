using Sakana.Application;
using Sakana.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace Sakana.App.Pages;

public sealed partial class AccountsPage : Page
{
    private readonly IMinecraftAccountService _accountService;
    private CancellationTokenSource? _signInCancellation;
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
            var session = await _accountService.GetCurrentSessionAsync(_clientId);
            if (session is null)
            {
                SetDisconnected("尚未连接账户");
                return;
            }

            SetConnected(session.PlayerName);
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
            SetDisconnected("尚未连接账户");
            AccountInfoBar.Severity = InfoBarSeverity.Success;
            AccountInfoBar.Title = "已退出登录";
            AccountInfoBar.Message = "本设备保存的 Microsoft 刷新凭据已删除。";
            AccountInfoBar.IsOpen = true;
            await App.CurrentApp.Logger.InformationAsync("account.sign_out_succeeded", "Microsoft account credentials were removed.");
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

    private void ShowClientIdAuthorizationRequired()
    {
        _accountStatus = "等待 Minecraft 应用授权";
        AccountStatusText.Text = _accountStatus;
        AccountStatusDot.Fill = (Brush)App.CurrentApp.Resources["SakanaMutedBrush"];
        SignInButton.Visibility = Visibility.Visible;
        SignOutButton.Visibility = Visibility.Visible;
        ClientIdApprovalLink.Visibility = Visibility.Visible;
        AccountInfoBar.Severity = InfoBarSeverity.Warning;
        AccountInfoBar.Title = "需要申请 Minecraft Java API 接入";
        AccountInfoBar.Message = "Microsoft 与 Xbox 验证已通过，但 Minecraft Services 拒绝了 Sakana 的 Client ID（HTTP 403）。需要提交 Minecraft Java Edition Game Service API 申请并获批；获批前，换 Microsoft 账号或重复登录都无法完成验证。Azure / M365 目录设置不能自行授予此权限。";
        AccountInfoBar.IsOpen = true;
    }

    private void SetConnected(string playerName)
    {
        ClientIdApprovalLink.Visibility = Visibility.Collapsed;
        _accountStatus = playerName;
        AccountStatusText.Text = _accountStatus;
        AccountStatusDot.Fill = (Brush)App.CurrentApp.Resources["SakanaAccentBrush"];
        SignInButton.Visibility = Visibility.Collapsed;
        SignOutButton.Visibility = Visibility.Visible;
    }

    private void SetDisconnected(string status, bool canSignOut = false)
    {
        ClientIdApprovalLink.Visibility = Visibility.Collapsed;
        _accountStatus = status;
        AccountStatusText.Text = _accountStatus;
        AccountStatusDot.Fill = (Brush)App.CurrentApp.Resources["SakanaMutedBrush"];
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
