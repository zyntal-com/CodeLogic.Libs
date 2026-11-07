using System.Runtime.CompilerServices;
using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CL.Storage.Errors;
using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage.Abstractions;

/// <summary>Provider-neutral streaming and batch helpers for <see cref="IStorageService"/>.</summary>
public static partial class StorageServiceExtensions
{
    private const int DefaultSerializedContentLimit = 16 * 1024 * 1024;
}
