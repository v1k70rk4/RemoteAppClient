namespace RemoteClient.Views;

/// <summary>Common interface for content views opened from the left menu in the main content area.</summary>
public interface IContentView
{
    /// <summary>Called when switching to the view, for example to refresh data.</summary>
    Task OnShownAsync();

    /// <summary>Refreshes manually colored inner controls such as ListView after theme changes.</summary>
    void ApplyTheme() { }

    /// <summary>Optional topbar subtitle under the page title (e.g. live counts). Null = none.</summary>
    string? Subtitle => null;
}
