namespace CL.Data;

/// <summary>
/// Package-level constants shared by CL.Data and its provider packages.
/// </summary>
public static class DataPackage
{
    /// <summary>The CodeLogic library id of the core package (<c>CL.Data</c>).</summary>
    public const string LibraryId = "CL.Data";

    /// <summary>
    /// Version of the provider contract (<c>DataProvider</c> and its parts). A provider built for a
    /// different contract version is rejected with <c>data.provider_incompatible</c>.
    /// </summary>
    public const int SpiVersion = 1;
}
