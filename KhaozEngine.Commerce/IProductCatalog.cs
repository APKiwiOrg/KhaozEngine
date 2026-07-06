using System.Collections.Generic;

namespace KhaozEngine.Commerce;

/// <summary>Consumer-supplied product data. The engine defines the shape; the game supplies entries.</summary>
public interface IProductCatalog
{
    bool TryGet(string productId, out ProductDefinition definition);
}

/// <summary>Simple dictionary-backed catalog.</summary>
public sealed class InMemoryProductCatalog : IProductCatalog
{
    private readonly Dictionary<string, ProductDefinition> map = new();
    public InMemoryProductCatalog(IEnumerable<ProductDefinition> products)
    {
        foreach (ProductDefinition p in products) map[p.ProductId] = p;
    }
    public bool TryGet(string productId, out ProductDefinition definition) => map.TryGetValue(productId, out definition);
}
