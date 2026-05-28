using Microsoft.Extensions.DependencyInjection;
using Xenon.Core.Services;
using Xenon.Core.ViewModels;

namespace Xenon.Core;

public static class IoC
{
    private static ServiceProvider? _provider = null!;

    public static void Build(ServiceCollection? services = null)
    {

        services.AddScoped<WebSearchService>();
        services.AddScoped<IPlaylistManager, PlaylistManager>();
        services.AddScoped<IRecentTracksStore, RecentTracksStore>();
        
        services.AddScoped<MainViewModel>();
        services.AddScoped<MiniPlayerBarViewModel>();
        services.AddScoped<HomePageViewModel>();
        services.AddScoped<AlbumPageViewModel>();
        services.AddScoped<SearchPageViewModel>();
        services.AddScoped<CollectionBrowsePageViewModel>();
        
        _provider =  services.BuildServiceProvider();
    }
    
    public static T Resolve<T>() => _provider.GetRequiredService<T>();
}
