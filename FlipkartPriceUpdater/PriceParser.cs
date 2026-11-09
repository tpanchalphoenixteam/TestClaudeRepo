using System.Globalization;
using System.Text.RegularExpressions;

namespace FlipkartPriceUpdater;

public static class PriceParser
{
    static readonly Regex Number = new(@"^\d+(\.\d{1,2})?$");

    /// <summary>Split pasted text (Excel columns or "SKU, price, mrp" lines) into cells.
    /// SKUs may contain spaces, so only comma and TAB separate values.</summary>
    public static List<string[]> SplitText(string text) =>
        text.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.Split(',', '\t').Select(p => p.Trim()).Where(p => p.Length > 0).ToArray())
            .ToList();

    /// <summary>Validate grid rows. Blank rows are ignored.</summary>
    public static (List<PriceChange> Changes, List<string> Errors) Validate(
        IEnumerable<(int Row, string? Sku, string? Price, string? Mrp)> rows)
    {
        var changes = new List<PriceChange>();
        var errors = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (row, rawSku, rawPrice, rawMrp) in rows)
        {
            var sku = rawSku?.Trim() ?? "";
            var price = rawPrice?.Trim() ?? "";
            var mrp = string.IsNullOrWhiteSpace(rawMrp) ? null : rawMrp.Trim();
            if (sku.Length == 0 && price.Length == 0 && mrp is null) continue;

            if (sku.Length == 0) { errors.Add($"Row {row}: SKU is empty"); continue; }
            if (!Number.IsMatch(price)) { errors.Add($"Row {row} ({sku}): price '{price}' is not a number"); continue; }
            if (mrp is not null && !Number.IsMatch(mrp)) { errors.Add($"Row {row} ({sku}): MRP '{mrp}' is not a number"); continue; }
            if (mrp is not null && decimal.Parse(price, CultureInfo.InvariantCulture) > decimal.Parse(mrp, CultureInfo.InvariantCulture))
            { errors.Add($"Row {row} ({sku}): selling price {price} is higher than MRP {mrp}"); continue; }
            if (!seen.Add(sku)) { errors.Add($"Row {row}: SKU '{sku}' entered twice"); continue; }

            changes.Add(new PriceChange { Row = row, Sku = sku, Price = price, Mrp = mrp });
        }
        return (changes, errors);
    }
}
