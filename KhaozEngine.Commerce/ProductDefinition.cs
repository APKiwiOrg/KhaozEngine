namespace KhaozEngine.Commerce;

/// <summary>Maps a store/checkout product id to the in-game currency it grants.</summary>
public readonly record struct ProductDefinition(string ProductId, CurrencyId Currency, long AmountPerUnit);
