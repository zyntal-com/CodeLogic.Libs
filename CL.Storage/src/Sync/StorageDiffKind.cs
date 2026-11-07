using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage.Sync;

/// <summary>How two items relate after a comparison.</summary>
public enum StorageDiffKind
{
    /// <summary>Exists only on the source side.</summary>
    OnlyInSource = 0,
    /// <summary>Exists only on the destination side.</summary>
    OnlyInDestination = 1,
    /// <summary>Exists on both sides with different content, by the chosen criteria.</summary>
    Different = 2,
    /// <summary>Exists on both sides and matches, by the chosen criteria.</summary>
    Same = 3
}
