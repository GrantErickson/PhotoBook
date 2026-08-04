namespace PhotoBook.Core.Model;

/// <summary>
/// Links the two sides of a matched spread pair (R22). Pairing is a soft scoring bonus, never a
/// constraint: swapping or detaching one side leaves the other alone.
/// </summary>
public sealed record TemplatePair
{
    /// <summary>The pair id shared by the left and right templates, e.g. <c>sp-a</c>.</summary>
    public string PairId { get; set; } = string.Empty;

    /// <summary>Which side this template is.</summary>
    public PairSide Side { get; set; }
}
