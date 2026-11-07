using System.Buffers;
using System.IO.Pipelines;
using System.Security.Cryptography;
using System.Text;
using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage.Registry;

/// <summary>Tags progress with the path a caller knows instead of an internal staging name.</summary>
internal sealed class PathProgress(IProgress<StorageTransferProgress> inner, string path) : IProgress<StorageTransferProgress>
{
    public void Report(StorageTransferProgress value) => inner.Report(value with { ItemPath = path });
}
