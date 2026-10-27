namespace ProcureAi.Suppliers.Application;

/// <summary>Input limits shared by the API's validation and the handlers.</summary>
public static class SuppliersLimits
{
    public const int MaxCodeLength = 60;
    public const int MaxSearchLength = 100;
    public const int MaxSkuLength = 40;

    /// <summary>The most SKUs one internal lookup may ask for.</summary>
    public const int MaxLookupSkus = 100;
}
