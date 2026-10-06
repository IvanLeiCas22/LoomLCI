using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace LoomLCI.Launcher;

public sealed class UpdateService
{
    private const int MaxManifestBytes = 64 * 1024;
    private const int MaxSignatureBytes = 4 * 1024;
    private const long MaxPackageBytes = 256L * 1024 * 1024;
    private const long MaxExtractedBytes = 512L * 1024 * 1024;
    private const int MaxArchiveEntries = 10_000;

    private readonly AppPaths _paths;
    private readonly IUpdateRuntimeControl _runtime;
    private readonly HttpClient _httpClient;
    private readonly IUpdateSignatureVerifier _signatureVerifier;
    private readonly UpdateFeedOptions _feed;

    public UpdateService(
        AppPaths paths,
        IUpdateRuntimeControl runtime,
        HttpClient? httpClient = null,
        IUpdateSignatureVerifier? signatureVerifier = null,
        UpdateFeedOptions? feed = null)
    {
        _paths = paths;
        _runtime = runtime;
        _httpClient = httpClient ?? new HttpClient();
        _signatureVerifier =
            signatureVerifier ?? UpdateTrust.CreateVerifier();
        _feed = feed ?? UpdateFeedOptions.Default;
    }

    public async Task<UpdateCheckResult> CheckAsync(
        CancellationToken cancellationToken)
    {
        using var operationLock = DeploymentLock.Acquire(_paths);
        await RecoverIfNeededLockedAsync(cancellationToken);

        var config = MachineConfigStore.Load(_paths.MachineConfigPath);
        var release = await FetchReleaseAsync(cancellationToken);
        return Evaluate(config, release);
    }

    public async Task<UpdateApplyResult> ApplyAsync(
        CancellationToken cancellationToken)
    {
        using var operationLock = DeploymentLock.Acquire(_paths);
        await RecoverIfNeededLockedAsync(cancellationToken);

        var original = MachineConfigStore.Load(
            _paths.MachineConfigPath);
        var release = await FetchReleaseAsync(cancellationToken);
        var check = Evaluate(original, release);

        if (check.RequiresNewInstaller)
        {
            throw new InvalidOperationException(
                $"La versión {release.Version} requiere update protocol " +
                $"{release.MinUpdateProtocol}; este Launcher soporta " +
                $"{UpdateTrust.SupportedProtocol}. Ejecute el instalador nuevo.");
        }

        if (!check.UpdateAvailable)
        {
            return new UpdateApplyResult(
                false,
                original.ActiveVersion,
                original.ActiveSequence,
                original.PreviousVersion,
                original.PreviousSequence);
        }

        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            "LoomLCI.Update",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        var journalWritten = false;
        try
        {
            var packagePath = Path.Combine(
                tempRoot,
                "update-package.zip");
            await DownloadPackageAsync(
                release,
                packagePath,
                cancellationToken);

            var packageRoot = Path.Combine(tempRoot, "package");
            ExtractPackage(packagePath, packageRoot);

            var packageManifest =
                PortablePackageManifestStore.Load(packageRoot);
            ValidatePackageMatchesRelease(
                packageManifest,
                release);

            HostPackageInstaller.InstallHost(
                packageRoot,
                packageManifest,
                _paths,
                replaceExisting: true);

            var target = original with
            {
                ActiveVersion = release.Version,
                ActiveSequence = release.Sequence,
                PreviousVersion = original.ActiveVersion,
                PreviousSequence = original.ActiveSequence,
                HighestSequence = Math.Max(
                    original.HighestSequence,
                    release.Sequence)
            };

            var journal = new UpdateJournal
            {
                Operation = "update",
                OperationId = Guid.NewGuid().ToString("N"),
                Stage = UpdateJournalStage.Prepared,
                OriginalConfig = original,
                TargetConfig = target,
                StartedAt = DateTimeOffset.UtcNow
            };

            UpdateJournalStore.Save(
                _paths.UpdateJournalPath,
                journal);
            journalWritten = true;

            await ActivateAsync(
                journal,
                cancellationToken);

            UpdateJournalStore.Delete(
                _paths.UpdateJournalPath);
            CleanupVersions(target);

            return new UpdateApplyResult(
                true,
                target.ActiveVersion,
                target.ActiveSequence,
                target.PreviousVersion,
                target.PreviousSequence);
        }
        catch (Exception ex) when (journalWritten)
        {
            try
            {
                await RecoverIfNeededLockedAsync(
                    CancellationToken.None);
            }
            catch (Exception recoveryEx)
            {
                throw new InvalidOperationException(
                    "El update falló y también falló la recuperación automática. " +
                    "El journal se conservó para el próximo arranque.",
                    new AggregateException(ex, recoveryEx));
            }

            throw new InvalidOperationException(
                $"El update falló; LoomLCI restauró {original.ActiveVersion}.",
                ex);
        }
        finally
        {
            DeleteDirectoryBestEffort(tempRoot);
        }
    }

    public async Task<UpdateApplyResult> RollbackAsync(
        CancellationToken cancellationToken)
    {
        using var operationLock = DeploymentLock.Acquire(_paths);
        await RecoverIfNeededLockedAsync(cancellationToken);

        var original = MachineConfigStore.Load(
            _paths.MachineConfigPath);

        if (string.IsNullOrWhiteSpace(
                original.PreviousVersion))
        {
            throw new InvalidOperationException(
                "No hay una versión anterior disponible para rollback.");
        }

        VersionName.Validate(original.PreviousVersion);

        if (!File.Exists(
                _paths.HostPath(original.PreviousVersion)))
        {
            throw new InvalidOperationException(
                $"Falta la versión anterior instalada: " +
                $"{original.PreviousVersion}.");
        }

        var target = original with
        {
            ActiveVersion = original.PreviousVersion,
            ActiveSequence = original.PreviousSequence,
            PreviousVersion = original.ActiveVersion,
            PreviousSequence = original.ActiveSequence
        };

        var journal = new UpdateJournal
        {
            Operation = "rollback",
            OperationId = Guid.NewGuid().ToString("N"),
            Stage = UpdateJournalStage.Prepared,
            OriginalConfig = original,
            TargetConfig = target,
            StartedAt = DateTimeOffset.UtcNow
        };

        UpdateJournalStore.Save(
            _paths.UpdateJournalPath,
            journal);

        try
        {
            await ActivateAsync(
                journal,
                cancellationToken);

            UpdateJournalStore.Delete(
                _paths.UpdateJournalPath);
            CleanupVersions(target);

            return new UpdateApplyResult(
                true,
                target.ActiveVersion,
                target.ActiveSequence,
                target.PreviousVersion,
                target.PreviousSequence);
        }
        catch (Exception ex)
        {
            try
            {
                await RecoverIfNeededLockedAsync(
                    CancellationToken.None);
            }
            catch (Exception recoveryEx)
            {
                throw new InvalidOperationException(
                    "El rollback falló y también falló la recuperación automática. " +
                    "El journal se conservó para el próximo arranque.",
                    new AggregateException(ex, recoveryEx));
            }

            throw new InvalidOperationException(
                $"El rollback falló; LoomLCI restauró {original.ActiveVersion}.",
                ex);
        }
    }

    public async Task<bool> RecoverIfNeededAsync(
        CancellationToken cancellationToken)
    {
        using var operationLock = DeploymentLock.Acquire(_paths);
        return await RecoverIfNeededLockedAsync(
            cancellationToken);
    }

    private async Task ActivateAsync(
        UpdateJournal journal,
        CancellationToken cancellationToken)
    {
        journal = journal with
        {
            Stage = UpdateJournalStage.Stopping
        };
        UpdateJournalStore.Save(
            _paths.UpdateJournalPath,
            journal);

        await _runtime.StopAndConfirmAsync(
            _paths,
            journal.OriginalConfig,
            cancellationToken);

        journal = journal with
        {
            Stage = UpdateJournalStage.RuntimeStopped
        };
        UpdateJournalStore.Save(
            _paths.UpdateJournalPath,
            journal);

        MachineConfigStore.Save(
            _paths.MachineConfigPath,
            journal.TargetConfig);

        journal = journal with
        {
            Stage = UpdateJournalStage.Activated
        };
        UpdateJournalStore.Save(
            _paths.UpdateJournalPath,
            journal);

        journal = journal with
        {
            Stage = UpdateJournalStage.Starting
        };
        UpdateJournalStore.Save(
            _paths.UpdateJournalPath,
            journal);

        await _runtime.StartAndConfirmAsync(
            _paths,
            journal.TargetConfig,
            cancellationToken);

        journal = journal with
        {
            Stage = UpdateJournalStage.RuntimeStarted
        };
        UpdateJournalStore.Save(
            _paths.UpdateJournalPath,
            journal);
    }

    private async Task<bool> RecoverIfNeededLockedAsync(
        CancellationToken cancellationToken)
    {
        var journal = UpdateJournalStore.TryLoad(
            _paths.UpdateJournalPath);
        if (journal is null)
        {
            return false;
        }

        if (journal.Stage == UpdateJournalStage.RuntimeStarted)
        {
            var current = MachineConfigStore.Load(
                _paths.MachineConfigPath);

            if (SameActivation(
                    current,
                    journal.TargetConfig) &&
                await _runtime.IsReadyAsync(
                    _paths,
                    journal.TargetConfig,
                    cancellationToken))
            {
                UpdateJournalStore.Delete(
                    _paths.UpdateJournalPath);
                CleanupVersions(journal.TargetConfig);
                return true;
            }
        }

        if (journal.Stage == UpdateJournalStage.Prepared)
        {
            var current = MachineConfigStore.Load(
                _paths.MachineConfigPath);

            if (SameActivation(
                    current,
                    journal.OriginalConfig))
            {
                CleanupFailedTarget(journal);
                UpdateJournalStore.Delete(
                    _paths.UpdateJournalPath);
                return true;
            }
        }

        MachineConfig currentConfig;
        try
        {
            currentConfig = MachineConfigStore.Load(
                _paths.MachineConfigPath);
        }
        catch
        {
            currentConfig = journal.TargetConfig;
        }

        await _runtime.StopAndConfirmAsync(
            _paths,
            currentConfig,
            cancellationToken);

        MachineConfigStore.Save(
            _paths.MachineConfigPath,
            journal.OriginalConfig);

        await _runtime.StartAndConfirmAsync(
            _paths,
            journal.OriginalConfig,
            cancellationToken);

        CleanupFailedTarget(journal);
        UpdateJournalStore.Delete(
            _paths.UpdateJournalPath);
        return true;
    }

    private async Task<UpdateReleaseManifest> FetchReleaseAsync(
        CancellationToken cancellationToken)
    {
        var manifestBytes = await DownloadBytesBoundedAsync(
            _feed.ManifestUri,
            MaxManifestBytes,
            cancellationToken);
        var signatureText = Encoding.UTF8.GetString(
            await DownloadBytesBoundedAsync(
                _feed.SignatureUri,
                MaxSignatureBytes,
                cancellationToken)).Trim();

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(
                signatureText);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException(
                "La firma del manifest no es Base64 válido.",
                ex);
        }

        if (!_signatureVerifier.Verify(
                manifestBytes,
                signature))
        {
            throw new InvalidDataException(
                "La firma del manifest de update no es válida.");
        }

        return UpdateReleaseManifestStore.Load(
            manifestBytes);
    }

    private static UpdateCheckResult Evaluate(
        MachineConfig config,
        UpdateReleaseManifest release)
    {
        var highest = Math.Max(
            config.HighestSequence,
            config.ActiveSequence);

        if (release.Sequence < highest)
        {
            throw new InvalidDataException(
                $"El feed retrocedió a sequence {release.Sequence}; " +
                $"esta instalación ya alcanzó {highest}.");
        }

        if (release.Sequence == config.ActiveSequence &&
            !string.Equals(
                release.Version,
                config.ActiveVersion,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "El feed reutiliza el sequence activo para otra versión.");
        }

        var updateAvailable =
            release.Sequence > config.ActiveSequence;

        return new UpdateCheckResult(
            config.ActiveVersion,
            config.ActiveSequence,
            highest,
            release,
            updateAvailable,
            release.MinUpdateProtocol >
                UpdateTrust.SupportedProtocol);
    }

    private async Task DownloadPackageAsync(
        UpdateReleaseManifest release,
        string destination,
        CancellationToken cancellationToken)
    {
        var packageUri = new Uri(
            release.PackageUrl,
            UriKind.Absolute);

        using var response = await _httpClient.GetAsync(
            packageUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is long declared &&
            declared != release.PackageSizeBytes)
        {
            throw new InvalidDataException(
                $"El paquete declara {declared} bytes por HTTP, " +
                $"pero el manifest espera {release.PackageSizeBytes}.");
        }

        if (release.PackageSizeBytes > MaxPackageBytes)
        {
            throw new InvalidDataException(
                $"El paquete excede el máximo de {MaxPackageBytes} bytes.");
        }

        await using var source =
            await response.Content.ReadAsStreamAsync(
                cancellationToken);
        await using var target = File.Create(destination);

        var buffer = new byte[64 * 1024];
        long total = 0;
        using var hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);

        while (true)
        {
            var read = await source.ReadAsync(
                buffer,
                cancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > MaxPackageBytes ||
                total > release.PackageSizeBytes)
            {
                throw new InvalidDataException(
                    "El paquete descargado excede el tamaño esperado.");
            }

            hash.AppendData(buffer, 0, read);
            await target.WriteAsync(
                buffer.AsMemory(0, read),
                cancellationToken);
        }

        if (total != release.PackageSizeBytes)
        {
            throw new InvalidDataException(
                $"Tamaño de paquete inesperado: {total}; " +
                $"esperado {release.PackageSizeBytes}.");
        }

        var actualHash = Convert.ToHexString(
                hash.GetHashAndReset())
            .ToLowerInvariant();

        if (!string.Equals(
                actualHash,
                release.PackageSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"SHA-256 inesperado para el update: {actualHash}.");
        }
    }

    private async Task<byte[]> DownloadBytesBoundedAsync(
        Uri uri,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(
            uri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is long declared &&
            declared > maxBytes)
        {
            throw new InvalidDataException(
                $"La respuesta de {uri} excede {maxBytes} bytes.");
        }

        await using var source =
            await response.Content.ReadAsStreamAsync(
                cancellationToken);
        using var target = new MemoryStream();

        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(
                buffer,
                cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (target.Length + read > maxBytes)
            {
                throw new InvalidDataException(
                    $"La respuesta de {uri} excede {maxBytes} bytes.");
            }

            target.Write(buffer, 0, read);
        }

        return target.ToArray();
    }

    private static void ExtractPackage(
        string archivePath,
        string destination)
    {
        Directory.CreateDirectory(destination);
        var destinationRoot = Path.GetFullPath(destination);
        var destinationPrefix =
            destinationRoot.EndsWith(
                Path.DirectorySeparatorChar)
                ? destinationRoot
                : destinationRoot +
                  Path.DirectorySeparatorChar;

        using var archive = ZipFile.OpenRead(archivePath);

        if (archive.Entries.Count > MaxArchiveEntries)
        {
            throw new InvalidDataException(
                "El paquete contiene demasiadas entradas.");
        }

        long total = 0;
        foreach (var entry in archive.Entries)
        {
            total += entry.Length;
            if (total > MaxExtractedBytes)
            {
                throw new InvalidDataException(
                    "El contenido extraído excede el límite permitido.");
            }

            var relative = entry.FullName
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar);
            var targetPath = Path.GetFullPath(
                Path.Combine(destinationRoot, relative));

            if (!targetPath.StartsWith(
                    destinationPrefix,
                    StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(
                    targetPath,
                    destinationRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "El ZIP contiene una ruta que escapa del staging.");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(targetPath);
                continue;
            }

            Directory.CreateDirectory(
                Path.GetDirectoryName(targetPath)!);
            using var source = entry.Open();
            using var target = new FileStream(
                targetPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None);
            source.CopyTo(target);
        }
    }

    private static void ValidatePackageMatchesRelease(
        PortablePackageManifest package,
        UpdateReleaseManifest release)
    {
        if (!string.Equals(
                package.Version,
                release.Version,
                StringComparison.Ordinal) ||
            package.Sequence != release.Sequence ||
            package.UpdateProtocol >
                UpdateTrust.SupportedProtocol ||
            !string.Equals(
                package.Platform,
                release.Platform,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "package.json no coincide con el manifest firmado.");
        }
    }

    private void CleanupFailedTarget(
        UpdateJournal journal)
    {
        if (!string.Equals(
                journal.Operation,
                "update",
                StringComparison.Ordinal))
        {
            return;
        }

        var version = journal.TargetConfig.ActiveVersion;
        if (string.Equals(
                version,
                journal.OriginalConfig.ActiveVersion,
                StringComparison.Ordinal) ||
            string.Equals(
                version,
                journal.OriginalConfig.PreviousVersion,
                StringComparison.Ordinal))
        {
            return;
        }

        DeleteDirectoryBestEffort(
            _paths.VersionDirectory(version));
    }

    private void CleanupVersions(
        MachineConfig config)
    {
        if (!Directory.Exists(_paths.VersionsRoot))
        {
            return;
        }

        var keep = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase)
        {
            config.ActiveVersion
        };

        if (!string.IsNullOrWhiteSpace(
                config.PreviousVersion))
        {
            keep.Add(config.PreviousVersion);
        }

        foreach (var directory in Directory.EnumerateDirectories(
                     _paths.VersionsRoot))
        {
            var name = Path.GetFileName(directory);
            if (!keep.Contains(name))
            {
                DeleteDirectoryBestEffort(directory);
            }
        }
    }

    private static bool SameActivation(
        MachineConfig left,
        MachineConfig right) =>
        string.Equals(
            left.ActiveVersion,
            right.ActiveVersion,
            StringComparison.Ordinal) &&
        left.ActiveSequence == right.ActiveSequence;

    private static void DeleteDirectoryBestEffort(
        string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }
}
