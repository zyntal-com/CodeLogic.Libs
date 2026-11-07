using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage.Sync;

/// <summary>A listed tree: included items keyed by path, what was left out, and whether the root was missing.</summary>
internal sealed record StorageTree(Dictionary<string, StorageItem> Items, StorageExclusions Exclusions, bool Missing)
{
    /// <summary>The links followed on this side, and where the content of each item reached through one is read.</summary>
    public StorageFollowedLinks Links { get; init; } = new();
}
