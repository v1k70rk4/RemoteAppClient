namespace RemoteClient.Views;

/// <summary>A bal menüből megnyitható tartalom-nézetek közös felülete (a fő ablak tartalomterületén).</summary>
public interface IContentView
{
    /// <summary>A nézetre váltáskor hívódik (pl. friss lekérés).</summary>
    Task OnShownAsync();

    /// <summary>Téma-váltáskor a belső, kézzel színezett vezérlők (ListView) frissítése.</summary>
    void ApplyTheme() { }
}
