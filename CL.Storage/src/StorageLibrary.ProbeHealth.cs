using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Events;
using CL.Storage.Models;
using CL.Storage.Providers;
using CL.Storage.Providers.Azure;
using CL.Storage.Providers.Ftp;
using CL.Storage.Providers.GoogleCloud;
using CL.Storage.Providers.Local;
using CL.Storage.Providers.S3;
using CL.Storage.Providers.Sftp;
using CL.Storage.Providers.Swift;
using CL.Storage.Providers.WebDav;
using CL.Storage.Registry;
using CodeLogic.Core.Configuration;
using CodeLogic.Core.Logging;
using CodeLogic.Core.Results;
using CodeLogic.Framework.Libraries;

namespace CL.Storage;

public sealed partial class StorageLibrary
{

    private static async Task<HealthProbe> ProbeHealthAsync(
        HealthTarget target,
        int timeoutSeconds,
        ILogger logger)
    {
        Task<Result>? probeTask = null;
        var provider = target.Lease.Backend.Provider;
        var started = Stopwatch.GetTimestamp();
        HealthProbe Probe(bool healthy, string detail) =>
            new(target.Id, healthy, detail) { Provider = provider, Latency = Stopwatch.GetElapsedTime(started) };
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)))
        {
            try
            {
                probeTask = target.Lease.Backend.CheckHealthAsync(timeout.Token);
                var result = await probeTask
                    .WaitAsync(TimeSpan.FromSeconds(timeoutSeconds))
                    .ConfigureAwait(false);
                return result.IsSuccess
                    ? Probe(true, string.Empty)
                    : Probe(false, result.Error?.Code ?? "storage.provider_error");
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                return Probe(false, "storage.timeout");
            }
            catch (TimeoutException)
            {
                timeout.Cancel();
                return Probe(false, "storage.timeout");
            }
            catch (Exception)
            {
                return Probe(false, "storage.provider_error");
            }
            finally
            {
                if (probeTask is null)
                {
                    target.Lease.Dispose();
                }
                else
                {
                    var release = ObserveProbeAndReleaseHealthLeaseAsync(target, probeTask, logger);
                    if (probeTask.IsCompleted)
                        await release.ConfigureAwait(false);
                    else
                        _ = release;
                }
            }
        }
    }

    private static async Task ObserveProbeAndReleaseHealthLeaseAsync(
        HealthTarget target,
        Task<Result> probeTask,
        ILogger logger)
    {
        try { await probeTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            TryLog(logger, $"Storage connection '{target.Id}' health probe failed after the caller stopped waiting.", error);
        }
        finally
        {
            target.Lease.Dispose();
        }
    }

    private static void TryLog(ILogger logger, string message, Exception error)
    {
        try { logger.Error(message, error); }
        catch { }
    }

    private LifecycleState GetLifecycleState()
    {
        lock (_stateGate)
            return _state;
    }

    private string GetDefaultConnectionId()
    {
        var runtime = CaptureRuntimeSnapshot();
        _defaultConnectionSnapshotCaptured?.Invoke();
        return runtime.DefaultConnection;
    }

    private RuntimeSnapshot CaptureRuntimeSnapshot()
    {
        if (TryCaptureRuntimeSnapshot(out var runtime, out var state))
            return runtime;
        throw new InvalidOperationException($"Storage library is {DescribeState(state)}; public access requires an initialized library.");
    }

    private bool TryCaptureRuntimeSnapshot(out RuntimeSnapshot runtime, out LifecycleState state)
    {
        lock (_stateGate)
        {
            state = _state;
            if (state is not (LifecycleState.Initialized or LifecycleState.Started))
            {
                runtime = default;
                return false;
            }

            var context = _context ?? throw new InvalidOperationException(
                "Storage library runtime context is unavailable while the library is initialized.");
            var storageConfig = _storageConfig ?? throw new InvalidOperationException(
                "Storage library runtime configuration is unavailable while the library is initialized.");
            runtime = new RuntimeSnapshot(
                context,
                storageConfig,
                context.Logger,
                _enabled,
                storageConfig.DefaultConnection,
                storageConfig.MaxBufferedDownloadBytes,
                storageConfig.HealthCheckTimeoutSeconds);
            return true;
        }
    }

    private void EnsureOperational() => _ = CaptureRuntimeSnapshot();

    private static string DescribeState(LifecycleState state) => state switch
    {
        LifecycleState.Created => "not initialized",
        LifecycleState.Configured => "configured but not initialized",
        LifecycleState.Stopping => "stopping",
        LifecycleState.Stopped => "stopped",
        LifecycleState.Disposed => "disposed",
        _ => state.ToString().ToLowerInvariant()
    };

    private static void EnsureValid(string section, CodeLogic.Core.Configuration.ConfigValidationResult validation)
    {
        if (!validation.IsValid)
            throw new InvalidOperationException($"Storage configuration section '{section}' is invalid: {string.Join("; ", validation.Errors)}");
    }

    private static StorageConnectionConfigBase CloneProviderConnection(StorageConnectionConfigBase source) =>
        (StorageConnectionConfigBase)JsonSerializer.Deserialize(
            JsonSerializer.Serialize(source, source.GetType()),
            source.GetType())!;

    private static ProviderDescriptor? DescribeProviderConnection(Type connectionType)
    {
        if (connectionType == typeof(S3ConnectionConfig))
            return new ProviderDescriptor(StorageProvider.S3, "storage.s3");
        if (connectionType == typeof(FtpConnectionConfig))
            return new ProviderDescriptor(StorageProvider.Ftp, "storage.ftp");
        if (connectionType == typeof(SftpConnectionConfig))
            return new ProviderDescriptor(StorageProvider.Sftp, "storage.sftp");
        if (connectionType == typeof(WebDavConnectionConfig))
            return new ProviderDescriptor(StorageProvider.WebDav, "storage.webdav");
        if (connectionType == typeof(AzureBlobConnectionConfig))
            return new ProviderDescriptor(StorageProvider.AzureBlob, "storage.azure");
        if (connectionType == typeof(GoogleCloudConnectionConfig))
            return new ProviderDescriptor(StorageProvider.GoogleCloudStorage, "storage.gcs");
        if (connectionType == typeof(SwiftConnectionConfig))
            return new ProviderDescriptor(StorageProvider.OpenStackSwift, "storage.swift");
        return null;
    }

    private ProviderStorageConfigBase GetProviderConfig(LibraryContext context, Type connectionType)
    {
        if (connectionType == typeof(S3ConnectionConfig)) return ReadConfig<S3StorageConfig>(context);
        if (connectionType == typeof(FtpConnectionConfig)) return ReadConfig<FtpStorageConfig>(context);
        if (connectionType == typeof(SftpConnectionConfig)) return ReadConfig<SftpStorageConfig>(context);
        if (connectionType == typeof(WebDavConnectionConfig)) return ReadConfig<WebDavStorageConfig>(context);
        if (connectionType == typeof(AzureBlobConnectionConfig)) return ReadConfig<AzureStorageConfig>(context);
        if (connectionType == typeof(GoogleCloudConnectionConfig)) return ReadConfig<GoogleCloudStorageConfig>(context);
        if (connectionType == typeof(SwiftConnectionConfig)) return ReadConfig<SwiftStorageConfig>(context);
        throw new NotSupportedException($"Provider connection type '{connectionType.FullName}' is not supported.");
    }

    private Task SaveProviderConfigAsync(LibraryContext context, ProviderStorageConfigBase config) => config switch
    {
        S3StorageConfig value => SaveConfigAsync(context, value),
        FtpStorageConfig value => SaveConfigAsync(context, value),
        SftpStorageConfig value => SaveConfigAsync(context, value),
        WebDavStorageConfig value => SaveConfigAsync(context, value),
        AzureStorageConfig value => SaveConfigAsync(context, value),
        GoogleCloudStorageConfig value => SaveConfigAsync(context, value),
        SwiftStorageConfig value => SaveConfigAsync(context, value),
        _ => throw new NotSupportedException($"Provider configuration type '{config.GetType().FullName}' is not supported.")
    };

    private string? FindConfiguredSection(
        string id,
        LibraryContext context,
        Type? exceptConnectionType = null)
    {
        if (exceptConnectionType != typeof(LocalConnectionConfig) &&
            TryFindKey(ReadConfig<LocalStorageConfig>(context).Connections, id, out _))
            return "storage.local";
        if (exceptConnectionType != typeof(S3ConnectionConfig) && ReadConfig<S3StorageConfig>(context).ContainsConnection(id))
            return "storage.s3";
        if (exceptConnectionType != typeof(FtpConnectionConfig) && ReadConfig<FtpStorageConfig>(context).ContainsConnection(id))
            return "storage.ftp";
        if (exceptConnectionType != typeof(SftpConnectionConfig) && ReadConfig<SftpStorageConfig>(context).ContainsConnection(id))
            return "storage.sftp";
        if (exceptConnectionType != typeof(WebDavConnectionConfig) && ReadConfig<WebDavStorageConfig>(context).ContainsConnection(id))
            return "storage.webdav";
        if (exceptConnectionType != typeof(AzureBlobConnectionConfig) && ReadConfig<AzureStorageConfig>(context).ContainsConnection(id))
            return "storage.azure";
        if (exceptConnectionType != typeof(GoogleCloudConnectionConfig) && ReadConfig<GoogleCloudStorageConfig>(context).ContainsConnection(id))
            return "storage.gcs";
        if (exceptConnectionType != typeof(SwiftConnectionConfig) && ReadConfig<SwiftStorageConfig>(context).ContainsConnection(id))
            return "storage.swift";
        return null;
    }

    private ProviderConfigMatch? FindProviderConfigContaining(string id, LibraryContext context)
    {
        ProviderStorageConfigBase[] configs =
        [
            ReadConfig<S3StorageConfig>(context),
            ReadConfig<FtpStorageConfig>(context),
            ReadConfig<SftpStorageConfig>(context),
            ReadConfig<WebDavStorageConfig>(context),
            ReadConfig<AzureStorageConfig>(context),
            ReadConfig<GoogleCloudStorageConfig>(context),
            ReadConfig<SwiftStorageConfig>(context)
        ];
        foreach (var config in configs)
        {
            if (!config.ContainsConnection(id)) continue;
            var connectionType = config.EnumerateConnections().First().Value.GetType();
            return new ProviderConfigMatch(config, DescribeProviderConnection(connectionType)!.Value);
        }
        return null;
    }
}
