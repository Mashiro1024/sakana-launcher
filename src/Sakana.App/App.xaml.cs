using Sakana.Application;
using Sakana.Domain;
using Sakana.Infrastructure;
using Sakana.Minecraft;
using Microsoft.UI.Xaml;
using System.Net.Http.Headers;

namespace Sakana.App;

public partial class App : Microsoft.UI.Xaml.Application
{
    private Window? _window;
    private HttpClient? _httpClient;
    public App()
    {
        InitializeComponent();
        UnhandledException += OnAppUnhandledException;
    }

    private void OnAppUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        if (Logger is not null)
        {
            _ = Logger.ErrorAsync("app.unhandled_exception", e.Message, e.Exception);
        }
    }

    public static App CurrentApp => (App)Current;

    public ILauncherService LauncherService { get; private set; } = null!;

    public ISakanaLogger Logger { get; private set; } = null!;

    public IMinecraftAccountService MinecraftAccountService { get; private set; } = null!;

    public ICurseForgeApiKeyStore CurseForgeApiKeyStore { get; private set; } = null!;

    public IntPtr WindowHandle => _window is null ? IntPtr.Zero : WinRT.Interop.WindowNative.GetWindowHandle(_window);

    public event Action<string>? NavigationRequested;

    public event Action<ThemePreference>? ThemeChanged;

    public void RequestNavigation(string destination) => NavigationRequested?.Invoke(destination);

    public void NotifyThemeChanged(ThemePreference theme) => ThemeChanged?.Invoke(theme);

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var store = new SqliteLauncherStore();
        Logger = new JsonFileSakanaLogger(store.DataDirectory);
        var downloadConcurrencyOptions = new DownloadConcurrencyOptions();
        _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Sakana", "0.1"));
        var metadataClient = new MojangMetadataClient(_httpClient, store.DataDirectory);
        var fabricLoaderCatalog = new FabricMetaClient(_httpClient);
        var quiltLoaderCatalog = new QuiltMetaClient(_httpClient);
        var forgeNeoForgeLoaderCatalog = new ForgeNeoForgeMetaClient(_httpClient);
        var javaRuntimeManager = new MojangJavaRuntimeManager(_httpClient, store.DataDirectory, downloadConcurrencyOptions);
        var modrinthModpackService = new ModrinthModpackService(_httpClient, downloadConcurrencyOptions);
        CurseForgeApiKeyStore = new WindowsDpapiCurseForgeApiKeyStore(store.DataDirectory);
        var curseForgeModpackService = new CurseForgeModpackService(_httpClient, CurseForgeApiKeyStore, downloadConcurrencyOptions);
        var localModService = new LocalFabricModService(store.DataDirectory);
        var localPackService = new LocalMinecraftPackService(store.DataDirectory);
        var localMinecraftWorldService = new LocalMinecraftWorldService(store.DataDirectory);
        var localMinecraftServerService = new LocalMinecraftServerService(store.DataDirectory);
        var minecraftServerStatusService = new MinecraftServerStatusService();
        var modrinthModCatalog = new ModrinthModCatalog(_httpClient, store.DataDirectory, localModService, localPackService, downloadConcurrencyOptions);
        var installer = new MinecraftInstaller(_httpClient, store.DataDirectory, metadataClient, fabricLoaderCatalog, quiltLoaderCatalog, forgeNeoForgeLoaderCatalog, javaRuntimeManager, modrinthModpackService, curseForgeModpackService, downloadConcurrencyOptions);
        var launchPlanner = new MinecraftLaunchPlanner(store.DataDirectory, metadataClient, javaRuntimeManager);
        var credentialStore = new WindowsDpapiMicrosoftCredentialStore(store.DataDirectory);
        MinecraftAccountService = new MicrosoftMinecraftAccountService(_httpClient, credentialStore);
        LauncherService = new LauncherService(store, metadataClient, fabricLoaderCatalog, quiltLoaderCatalog, forgeNeoForgeLoaderCatalog, modrinthModpackService, curseForgeModpackService, modrinthModCatalog, localModService, localPackService, localMinecraftWorldService, localMinecraftServerService, minecraftServerStatusService, javaRuntimeManager, installer, launchPlanner, new MinecraftProcessRunner(), Logger, downloadConcurrencyOptions);
        var settings = Sakana.Domain.LauncherSettings.Default;

        try
        {
            await LauncherService.InitializeAsync();
            settings = await LauncherService.GetSettingsAsync();
            downloadConcurrencyOptions.MaxConcurrentDownloads = settings.MaxConcurrentDownloads;
            await Logger.InformationAsync("app.started", "Sakana Launcher started.");
        }
        catch (Exception exception)
        {
            await Logger.ErrorAsync("app.startup_failed", "Sakana could not initialize its local data store.", exception);
        }

        _window = new MainWindow(settings.Theme);
        _window.Activate();
    }
}
