namespace Movewise.Core.Export;

/// <summary>Builds Graph query paths with safely quoted and encoded OData values.</summary>
public static class GraphQuery
{
    /// <summary>Quotes a value for an OData string literal (single quotes are doubled).</summary>
    public static string Literal(string value) => "'" + value.Replace("'", "''") + "'";

    public static string Where(string collection, string filter, string select, int? top = null)
    {
        var path = $"{collection}?$filter={Uri.EscapeDataString(filter)}&$select={select}";
        return top is null ? path : $"{path}&$top={top}";
    }

    public static string Item(string collection, string id, string select) =>
        $"{collection}/{Uri.EscapeDataString(id)}?$select={select}";
}
