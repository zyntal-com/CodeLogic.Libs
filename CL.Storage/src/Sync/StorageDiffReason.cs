using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage.Sync;

/// <summary>Why two files were judged different.</summary>
[Flags]
public enum StorageDiffReason
{
    /// <summary>No difference.</summary>
    None = 0,
    /// <summary>Sizes differ.</summary>
    Size = 1,
    /// <summary>The source was modified later than the destination.</summary>
    SourceNewer = 2,
    /// <summary>The destination was modified later than the source.</summary>
    DestinationNewer = 4,
    /// <summary>Checksums differ.</summary>
    Checksum = 8,
    /// <summary>One side is a file and the other a directory.</summary>
    Type = 16,
    /// <summary>
    /// A checksum was needed but could not be had — the hashing budget ran out, or one side's file could not
    /// be read (locked, deleted after listing, no permission) — so equality is unknown.
    /// </summary>
    Undecidable = 32
}
