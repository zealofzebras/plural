namespace SmartMike.App.ViewModels;

/// <summary>One selectable chip in the settings sheet.</summary>
public sealed record Choice(string Label, int Value, string? Detail = null)
{
    public override string ToString() => Label;
}
