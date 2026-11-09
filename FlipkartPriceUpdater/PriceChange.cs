namespace FlipkartPriceUpdater;

/// <summary>One requested price change, plus the result after it is applied.</summary>
public sealed class PriceChange
{
    public required int Row { get; init; }          // grid row (for showing status)
    public required string Sku { get; init; }
    public required string Price { get; init; }
    public string? Mrp { get; init; }
    public string Status { get; set; } = "pending";
    public bool Succeeded => Status.StartsWith("OK", StringComparison.Ordinal);
}
