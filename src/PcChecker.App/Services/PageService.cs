using System.Windows;
using Wpf.Ui;

namespace PcChecker.App.Services;

/// <summary>Resolves navigation pages from the DI container so they get their view models injected.</summary>
public sealed class PageService : IPageService
{
    private readonly IServiceProvider _services;

    public PageService(IServiceProvider services)
    {
        _services = services;
    }

    public T? GetPage<T>() where T : class => _services.GetService(typeof(T)) as T;

    public FrameworkElement? GetPage(Type pageType)
    {
        if (!typeof(FrameworkElement).IsAssignableFrom(pageType))
        {
            throw new InvalidOperationException($"{pageType.Name} is not a WPF element and cannot be used as a page.");
        }

        return _services.GetService(pageType) as FrameworkElement;
    }
}
