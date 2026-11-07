using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage.Sync;

/// <summary>What a comparison looks at.</summary>
[Flags]
public enum StorageCompareBy
{
    /// <summary>File sizes.</summary>
    Size = 1,
    /// <summary>Modification times, within <see cref="StorageCompareOptions.TimeTolerance"/>.</summary>
    Time = 2,
    /// <summary>Content checksums: the server's where it keeps one, otherwise downloaded and hashed within the budget.</summary>
    Checksum = 4
}
