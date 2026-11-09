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

/// <summary>How the price is changed on My Listings.</summary>
public enum PriceMethod
{
    /// <summary>Pencil (Edit) icon in the "Price &amp; Settlement" box: enter the listing (selling) price.</summary>
    EditPencil,
    /// <summary>The ">" arrow in the "Price &amp; Settlement" box: enter the bank settlement amount.</summary>
    BankSettlementArrow,
}
