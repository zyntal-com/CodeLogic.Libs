using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage.Sync;

/// <summary>Compares directory trees across any two storage connections.</summary>
public static partial class StorageCompare
{

    /// <summary>How many followed links to folders may lie inside one another.</summary>
    internal const int MaxLinkDepth = 8;
}
