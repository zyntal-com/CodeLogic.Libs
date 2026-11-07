using System.Buffers;
using System.IO.Pipelines;
using System.Security.Cryptography;
using System.Text;
using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage.Registry;

/// <summary>Opens the source for reading from <paramref name="offset"/>; the caller disposes the stream.</summary>
internal delegate Task<Result<Stream>> StagedSourceOpener(long offset, CancellationToken cancellationToken);
