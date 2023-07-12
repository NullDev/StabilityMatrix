using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NLog;
using NLog.Config;
using NLog.Extensions.Logging;
using NLog.Targets;
using Octokit;
using Polly;
using Polly.Contrib.WaitAndRetry;
using Polly.Extensions.Http;
using Polly.Timeout;
using Refit;
using StabilityMatrix.Avalonia.DesignData;
using StabilityMatrix.Avalonia.Services;
using StabilityMatrix.Avalonia.ViewModels;
using StabilityMatrix.Avalonia.ViewModels.Dialogs;
using StabilityMatrix.Avalonia.Views;
using StabilityMatrix.Core.Api;
using StabilityMatrix.Core.Converters.Json;
using StabilityMatrix.Core.Database;
using StabilityMatrix.Core.Helper;
using StabilityMatrix.Core.Helper.Cache;
using StabilityMatrix.Core.Helper.Factory;
using StabilityMatrix.Core.Models.Api;
using StabilityMatrix.Core.Models.Packages;
using StabilityMatrix.Core.Python;
using StabilityMatrix.Core.Services;
using Application = Avalonia.Application;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace StabilityMatrix.Avalonia;

public partial class App : Application
{
    [NotNull]
    public static IServiceProvider? Services { get; private set; }
    
    [NotNull]
    public static Visual? VisualRoot { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // Set design theme
        if (Design.IsDesignMode)
        {
            RequestedThemeVariant = ThemeVariant.Dark;
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        ConfigureServiceProvider();

        var mainViewModel = Services.GetRequiredService<MainWindowViewModel>();
        var notificationService = Services.GetRequiredService<INotificationService>();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = Services.GetRequiredService<MainWindow>();
            VisualRoot = mainWindow;
            mainWindow.DataContext = mainViewModel;
            mainWindow.NotificationService = notificationService;
            desktop.MainWindow = mainWindow;
            desktop.Exit += OnExit;
            Debug.WriteLine($"Shutdown mode: {desktop.ShutdownMode}");
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void ConfigureServiceProvider()
    {
        var services = ConfigureServices();
        Services = services.BuildServiceProvider();
        Services.GetRequiredService<ISettingsManager>().TryFindLibrary();
    }

    internal static void ConfigurePageViewModels(IServiceCollection services)
    {
        services.AddSingleton<PackageManagerViewModel>()
            .AddSingleton<SettingsViewModel>()
            .AddSingleton<CheckpointBrowserViewModel>()
            .AddSingleton<CheckpointBrowserCardViewModel>()
            .AddSingleton<CheckpointsPageViewModel>()
            .AddSingleton<LaunchPageViewModel>();
        
        // Register disposable view models for shutdown cleanup
        services.AddSingleton<IDisposable>(p 
            => p.GetRequiredService<LaunchPageViewModel>());
    }

    internal static void ConfigureDialogViewModels(IServiceCollection services)
    {
        // Dialog view models (transient)
        services.AddTransient<InstallerViewModel>();
        services.AddTransient<OneClickInstallViewModel>();
        services.AddTransient<SelectModelVersionViewModel>();
        
        // Other transients (usually sub view models)
        services.AddTransient<CheckpointFolder>();
        services.AddTransient<CheckpointFile>();
        
        // Dialog factory
        services.AddSingleton<ServiceManager<ViewModelBase>>(provider =>
            new ServiceManager<ViewModelBase>()
                .Register(provider.GetRequiredService<InstallerViewModel>)
                .Register(provider.GetRequiredService<OneClickInstallViewModel>)
                .Register(provider.GetRequiredService<SelectModelVersionViewModel>)
                .Register(provider.GetRequiredService<CheckpointFolder>)
                .Register(provider.GetRequiredService<CheckpointFile>));
    }

    internal static void ConfigureViews(IServiceCollection services)
    {
        services.AddTransient<CheckpointsPage>();
        services.AddTransient<LaunchPageView>();
        services.AddTransient<PackageManagerPage>();
        services.AddTransient<SettingsPage>();
        services.AddTransient<CheckpointBrowserPage>();
        services.AddSingleton<MainWindow>();
    }
    
    internal static void ConfigurePackages(IServiceCollection services)
    {
        services.AddSingleton<BasePackage, A3WebUI>();
        services.AddSingleton<BasePackage, VladAutomatic>();
        services.AddSingleton<BasePackage, ComfyUI>();
    }

    private static IServiceCollection ConfigureServices()
    {
        var services = new ServiceCollection();

        services.AddMemoryCache();

        ConfigurePageViewModels(services);
        ConfigureDialogViewModels(services);
        ConfigurePackages(services);
        
        // Other services
        services.AddSingleton<ISettingsManager, SettingsManager>();
        services.AddSingleton<ISharedFolders, SharedFolders>();
        services.AddSingleton<ModelFinder>();
        services.AddSingleton<IPackageFactory, PackageFactory>();
        services.AddSingleton<IDownloadService, DownloadService>();
        services.AddSingleton<IGithubApiCache, GithubApiCache>();
        services.AddSingleton<IPrerequisiteHelper, PrerequisiteHelper>();
        services.AddSingleton<INotificationService, NotificationService>();
        services.AddSingleton<IPyRunner, PyRunner>();
        
        services.AddSingleton<MainWindowViewModel>(provider =>
            new MainWindowViewModel(provider.GetRequiredService<ISettingsManager>(),
                provider.GetRequiredService<ServiceManager<ViewModelBase>>())
            {
                Pages = new List<PageViewModelBase>
                {
                    provider.GetRequiredService<LaunchPageViewModel>(),
                    provider.GetRequiredService<PackageManagerViewModel>(),
                    provider.GetRequiredService<CheckpointsPageViewModel>(),
                    provider.GetRequiredService<CheckpointBrowserViewModel>(),
                },
                FooterPages = new List<PageViewModelBase>
                {
                    provider.GetRequiredService<SettingsViewModel>()
                }
            });
        
        ConfigureViews(services);

        if (Design.IsDesignMode)
        {
            services.AddSingleton<ILiteDbContext, MockLiteDbContext>();
        }
        else
        {
            services.AddSingleton<ILiteDbContext, LiteDbContext>();
            services.AddSingleton<IDisposable>(p => p.GetRequiredService<ILiteDbContext>());
        }

        services.AddTransient<IGitHubClient, GitHubClient>(_ =>
        {
            var client = new GitHubClient(new ProductHeaderValue("StabilityMatrix"));
            // var githubApiKey = Config["GithubApiKey"];
            // if (string.IsNullOrWhiteSpace(githubApiKey))
            //     return client;
            //
            // client.Credentials = new Credentials(githubApiKey);
            return client;
        });

        // Configure Refit and Polly
        var jsonSerializerOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        jsonSerializerOptions.Converters.Add(new ObjectToInferredTypesConverter());
        jsonSerializerOptions.Converters.Add(new DefaultUnknownEnumConverter<CivitFileType>());
        jsonSerializerOptions.Converters.Add(
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        jsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;

        var defaultRefitSettings = new RefitSettings
        {
            ContentSerializer =
                new SystemTextJsonContentSerializer(jsonSerializerOptions)
        };

        // HTTP Policies
        var retryStatusCodes = new[]
        {
            HttpStatusCode.RequestTimeout, // 408
            HttpStatusCode.InternalServerError, // 500
            HttpStatusCode.BadGateway, // 502
            HttpStatusCode.ServiceUnavailable, // 503
            HttpStatusCode.GatewayTimeout // 504
        };
        var delay = Backoff
            .DecorrelatedJitterBackoffV2(medianFirstRetryDelay: TimeSpan.FromMilliseconds(80),
                retryCount: 5);
        var retryPolicy = HttpPolicyExtensions
            .HandleTransientHttpError()
            .Or<TimeoutRejectedException>()
            .OrResult(r => retryStatusCodes.Contains(r.StatusCode))
            .WaitAndRetryAsync(delay);

        // Shorter timeout for local requests
        var localTimeout = Policy.TimeoutAsync<HttpResponseMessage>(TimeSpan.FromSeconds(3));
        var localDelay = Backoff
            .DecorrelatedJitterBackoffV2(medianFirstRetryDelay: TimeSpan.FromMilliseconds(50),
                retryCount: 3);
        var localRetryPolicy = HttpPolicyExtensions
            .HandleTransientHttpError()
            .Or<TimeoutRejectedException>()
            .OrResult(r => retryStatusCodes.Contains(r.StatusCode))
            .WaitAndRetryAsync(localDelay, onRetryAsync: (x, y) =>
            {
                Debug.WriteLine("Retrying local request...");
                return Task.CompletedTask;
            });

        // named client for update
        services.AddHttpClient("UpdateClient")
            .AddPolicyHandler(retryPolicy);

        // Add Refit clients
        services.AddRefitClient<ICivitApi>(defaultRefitSettings)
            .ConfigureHttpClient(c =>
            {
                c.BaseAddress = new Uri("https://civitai.com");
                c.Timeout = TimeSpan.FromSeconds(15);
            })
            .AddPolicyHandler(retryPolicy);

        // Add Refit client managers
        services.AddHttpClient("A3Client")
            .AddPolicyHandler(localTimeout.WrapAsync(localRetryPolicy));

        // Add logging
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddFilter("Microsoft.Extensions.Http", LogLevel.Warning)
                .AddFilter("Microsoft", LogLevel.Warning)
                .AddFilter("System", LogLevel.Warning);
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddNLog(ConfigureLogging());
        });

        return services;
    }

    private void OnExit(object? sender, ControlledApplicationLifetimeExitEventArgs args)
    {
        Debug.WriteLine("Start OnExit");
        // Services.GetRequiredService<LaunchViewModel>().OnShutdown();
        var settingsManager = Services.GetRequiredService<ISettingsManager>();

        // Unless KeepFolderLinksOnShutdown is set, delete all package junctions
        if (settingsManager is
            {
                IsLibraryDirSet: true,
                Settings.KeepFolderLinksOnShutdown: false
            })
        {
            var sharedFolders = Services.GetRequiredService<ISharedFolders>();
            sharedFolders.RemoveLinksForAllPackages();
        }

        Debug.WriteLine("Start OnExit: Disposing services");
        // Dispose all services
        foreach (var disposable in Services.GetServices<IDisposable>())
        {
            Debug.WriteLine($"Disposing {disposable.GetType().Name}");
            disposable.Dispose();
        }

        Debug.WriteLine("End OnExit");
    }

    private static LoggingConfiguration ConfigureLogging()
    {
        var logConfig = new LoggingConfiguration();

        var fileTarget = new FileTarget("logfile")
        {
            ArchiveOldFileOnStartup = true,
            FileName = "${specialfolder:folder=ApplicationData}/StabilityMatrix/app.log",
            ArchiveFileName = "${specialfolder:folder=ApplicationData}/StabilityMatrix/app.{#}.log",
            ArchiveNumbering = ArchiveNumberingMode.Rolling,
            MaxArchiveFiles = 2
        };
        var debugTarget = new DebuggerTarget("debugger") {Layout = "${message}"};
        logConfig.AddRule(NLog.LogLevel.Debug, NLog.LogLevel.Fatal, fileTarget);
        logConfig.AddRule(NLog.LogLevel.Trace, NLog.LogLevel.Fatal, debugTarget);

        LogManager.Configuration = logConfig;
        // Add Sentry to NLog if enabled
        if (true)
        {
            logConfig.AddSentry(o =>
            {
                o.InitializeSdk = false;
                o.Layout = "${message}";
                o.ShutdownTimeoutSeconds = 5;
                o.IncludeEventDataOnBreadcrumbs = true;
                o.BreadcrumbLayout = "${logger}: ${message}";
                // Debug and higher are stored as breadcrumbs (default is Info)
                o.MinimumBreadcrumbLevel = NLog.LogLevel.Debug;
                // Error and higher is sent as event (default is Error)
                o.MinimumEventLevel = NLog.LogLevel.Error;
            });
        }

        return logConfig;
    }
}
