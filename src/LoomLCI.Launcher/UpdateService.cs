using System.IO.Compression;
using System.Text;

namespace LoomLCI.Launcher;

public sealed class UpdateService
{
    private const int MaxManifestBytes = 64 * 1024;
    private const int MaxSignatureBytes = 4 * 1024;
    private const long MaxExtractedBytes = 512L * 1024 * 1024;
    private const int MaxArchiveEntries = 10_000;

    private readonly AppPaths _paths;
    private readonly IUpdateRuntimeControl _runtime;
    private readonly HttpClient _httpClient;
    private readonly IUpdateSignatureVerifier _signatureVerifier;
    private readonly UpdateFeedOptions _feed;
    private readonly UpdatePackageDownloader _downloader;
    private readonly Action<string>? _transitionProbe;

    public UpdateService(
        AppPaths paths,
        IUpdateRuntimeControl runtime,
        HttpClient? httpClient = null,
        IUpdateSignatureVerifier? signatureVerifier = null,
        UpdateFeedOptions? feed = null,
        UpdateDownloadOptions? downloadOptions = null,
        Action<string>? transitionProbe = null)
    {
        _paths = paths;
        _runtime = runtime;
        _httpClient = httpClient ?? new HttpClient();
        _signatureVerifier =
            signatureVerifier ?? UpdateTrust.CreateVerifier();
        _feed = feed ?? UpdateFeedOptions.Default;
        _downloader = new UpdatePackageDownloader(_httpClient, downloadOptions);
        _transitionProbe = transitionProbe;
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
        CancellationToken cancellationToken,
        Action<UpdateDownloadProgress>? onDownloadProgress = null)
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

        var operationId = Guid.NewGuid().ToString("N");
        var tempRoot = Path.Combine(
            Path.GetTempPath(), "LoomLCI.Update", operationId);
        Directory.CreateDirectory(tempRoot);
        Directory.CreateDirectory(_paths.VersionsRoot);
        var staging = StagingPath(release.Version, operationId);

        var journalWritten = false;
        try
        {
            var packagePath = Path.Combine(tempRoot, "update-package.zip");
            await _downloader.DownloadAsync(
                release, packagePath, onDownloadProgress, cancellationToken);

            var packageRoot = Path.Combine(tempRoot, "package");
            ExtractPackage(packagePath, packageRoot);
            var packageManifest = PortablePackageManifestStore.Load(packageRoot);
            ValidatePackageMatchesRelease(packageManifest, release);

            // Do not replace any installed version before a recovery journal exists.
            HostPackageInstaller.StageHost(packageRoot, packageManifest, staging);
            cancellationToken.ThrowIfCancellationRequested();

            var destination = _paths.VersionDirectory(release.Version);
            var existed = Directory.Exists(destination);
            ValidateExistingTarget(original, release, existed);

            var target = original with
            {
                ActiveVersion = release.Version,
                ActiveSequence = release.Sequence,
                PreviousVersion = original.ActiveVersion,
                PreviousSequence = original.ActiveSequence,
                HighestSequence = Math.Max(original.HighestSequence, release.Sequence)
            };
            var journal = new UpdateJournal
            {
                Operation = "update",
                OperationId = operationId,
                Stage = UpdateJournalStage.Prepared,
                OriginalConfig = original,
                TargetConfig = target,
                TargetExisted = existed,
                StartedAt = DateTimeOffset.UtcNow
            };

            UpdateJournalStore.Save(_paths.UpdateJournalPath, journal);
            journalWritten = true;
            _transitionProbe?.Invoke("prepared");

            PromoteTarget(journal);
            await ActivateAsync(journal, cancellationToken);
            _transitionProbe?.Invoke("activated");

            CompleteUpdate(journal with { Stage = UpdateJournalStage.RuntimeStarted });
            return new UpdateApplyResult(
                true, target.ActiveVersion, target.ActiveSequence,
                target.PreviousVersion, target.PreviousSequence);
        }
        catch (Exception ex) when (journalWritten)
        {
            try
            {
                await RecoverIfNeededLockedAsync(CancellationToken.None);
            }
            catch (Exception recoveryEx)
            {
                throw new InvalidOperationException(
                    "El update falló y también falló la recuperación automática. " +
                    "El journal se conservó para el próximo arranque.",
                    new AggregateException(ex, recoveryEx));
            }

            var afterRecovery = MachineConfigStore.Load(_paths.MachineConfigPath);
            if (afterRecovery.ActiveSequence == release.Sequence &&
                string.Equals(afterRecovery.ActiveVersion, release.Version,
                    StringComparison.Ordinal))
            {
                return new UpdateApplyResult(
                    true, afterRecovery.ActiveVersion, afterRecovery.ActiveSequence,
                    afterRecovery.PreviousVersion, afterRecovery.PreviousSequence);
            }

            throw new InvalidOperationException(
                $"El update falló; LoomLCI restauró {original.ActiveVersion}.", ex);
        }
        finally
        {
            if (!journalWritten)
            {
                DeleteDirectoryBestEffort(staging);
            }

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
        _transitionProbe?.Invoke("stopping");

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
        _transitionProbe?.Invoke("runtime_stopped");

        MachineConfigStore.Save(
            _paths.MachineConfigPath,
            journal.TargetConfig);
        _transitionProbe?.Invoke("config_saved");

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
        _transitionProbe?.Invoke("runtime_started");
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

        if (journal.SchemaVersion == 2 && journal.Operation == "update")
        {
            return await RecoverV2UpdateAsync(journal, cancellationToken);
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

    private static void ValidateExistingTarget(
        MachineConfig original, UpdateReleaseManifest release, bool existed)
    {
        if (string.Equals(release.Version, original.ActiveVersion,
            StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("No se permite sobrescribir el Host activo.");
        }

        if (existed &&
            (!string.Equals(release.Version, original.PreviousVersion,
                StringComparison.Ordinal) ||
             release.Sequence != original.PreviousSequence))
        {
            throw new InvalidDataException(
                "El directorio de destino ya existe y no coincide con la versión anterior.");
        }
    }

    private string StagingPath(string version, string operationId) =>
        _paths.VersionDirectory(version) + ".staging-" + operationId;

    private string BackupPath(string version, string operationId) =>
        _paths.VersionDirectory(version) + ".backup-" + operationId;

    private void PromoteTarget(UpdateJournal journal)
    {
        if (journal.Operation != "update" || journal.SchemaVersion != 2)
        {
            throw new InvalidDataException("Promoción sin journal v2.");
        }

        var version = journal.TargetConfig.ActiveVersion;
        var destination = _paths.VersionDirectory(version);
        var staging = StagingPath(version, journal.OperationId);
        var backup = BackupPath(version, journal.OperationId);
        if (!Directory.Exists(staging) || Directory.Exists(backup) ||
            Directory.Exists(destination) != journal.TargetExisted)
        {
            throw new IOException("Staging o destino de update cambió desde el preflight.");
        }

        UpdateJournalStore.Save(
            _paths.UpdateJournalPath, journal with { Stage = UpdateJournalStage.Promoting });
        _transitionProbe?.Invoke("promoting");

        if (journal.TargetExisted)
        {
            Directory.Move(destination, backup);
            _transitionProbe?.Invoke("backed_up");
        }

        Directory.Move(staging, destination);
        _transitionProbe?.Invoke("published");
        UpdateJournalStore.Save(
            _paths.UpdateJournalPath, journal with { Stage = UpdateJournalStage.Promoted });
    }

    private void RestoreTarget(UpdateJournal journal)
    {
        var version = journal.TargetConfig.ActiveVersion;
        if (string.Equals(version, journal.OriginalConfig.ActiveVersion,
            StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Journal v2 intenta restaurar el Host activo.");
        }

        var destination = _paths.VersionDirectory(version);
        var staging = StagingPath(version, journal.OperationId);
        var backup = BackupPath(version, journal.OperationId);
        var hasBackup = Directory.Exists(backup);
        var hasStaging = Directory.Exists(staging);
        var hasDestination = Directory.Exists(destination);

        if (journal.Stage == UpdateJournalStage.Prepared)
        {
            if (hasBackup || hasDestination != journal.TargetExisted)
            {
                throw new IOException("Estado inesperado de archivos en journal Prepared.");
            }
        }
        else if (hasBackup)
        {
            if (!journal.TargetExisted)
            {
                throw new IOException("El journal no esperaba un backup de destino.");
            }

            if (hasDestination)
            {
                Directory.Delete(destination, recursive: true);
            }

            // Keep the backup until the recovery journal has been deleted.
            // A crash during the copy can then retry recovery safely.
            HostPackageInstaller.CopyDirectory(backup, destination);
        }
        else if (journal.TargetExisted)
        {
            // With no backup, promotion can only be known not to have started
            // when the complete staging folder is still present.
            if (!hasStaging || !hasDestination)
            {
                throw new IOException(
                    "Falta el backup de una versión existente. Se conservó el journal.");
            }
        }
        else if (hasDestination)
        {
            if (hasStaging)
            {
                throw new IOException("Staging y destino inesperadamente presentes.");
            }

            Directory.Delete(destination, recursive: true);
        }

        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, recursive: true);
        }
    }

    private void CompleteUpdate(UpdateJournal journal)
    {
        if (journal.SchemaVersion != 2 || journal.Operation != "update")
        {
            throw new InvalidDataException("No se puede completar update sin journal v2.");
        }

        var committed = journal with { Stage = UpdateJournalStage.Committed };
        UpdateJournalStore.Save(_paths.UpdateJournalPath, committed);
        _transitionProbe?.Invoke("committed");

        DeleteUpdateArtifacts(committed);
        CleanupVersions(committed.TargetConfig);
        UpdateJournalStore.Delete(_paths.UpdateJournalPath);
    }

    private void DeleteUpdateArtifacts(UpdateJournal journal)
    {
        var version = journal.TargetConfig.ActiveVersion;
        var staging = StagingPath(version, journal.OperationId);
        var backup = BackupPath(version, journal.OperationId);
        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, recursive: true);
        }

        if (Directory.Exists(backup))
        {
            Directory.Delete(backup, recursive: true);
        }
    }

    private async Task<bool> RecoverV2UpdateAsync(
        UpdateJournal journal, CancellationToken cancellationToken)
    {
        var current = MachineConfigStore.Load(_paths.MachineConfigPath);
        if (journal.Stage == UpdateJournalStage.Committed)
        {
            if (!SameActivation(current, journal.TargetConfig) ||
                !File.Exists(_paths.HostPath(journal.TargetConfig.ActiveVersion)))
            {
                throw new IOException("Update committed no coincide con Host/configuración.");
            }

            if (!await _runtime.IsReadyAsync(
                _paths, journal.TargetConfig, cancellationToken))
            {
                await _runtime.StartAndConfirmAsync(
                    _paths, journal.TargetConfig, cancellationToken);
            }

            DeleteUpdateArtifacts(journal);
            CleanupVersions(journal.TargetConfig);
            UpdateJournalStore.Delete(_paths.UpdateJournalPath);
            return true;
        }

        if (journal.Stage == UpdateJournalStage.RuntimeStarted &&
            SameActivation(current, journal.TargetConfig) &&
            await _runtime.IsReadyAsync(
                _paths, journal.TargetConfig, cancellationToken))
        {
            CompleteUpdate(journal);
            return true;
        }

        if (journal.Stage is UpdateJournalStage.Prepared or
            UpdateJournalStage.Promoting or UpdateJournalStage.Promoted)
        {
            if (!SameActivation(current, journal.OriginalConfig))
            {
                throw new IOException("Configuración inesperada antes de detener runtime.");
            }

            RestoreTarget(journal);
            _transitionProbe?.Invoke("restored_files");
            UpdateJournalStore.Delete(_paths.UpdateJournalPath);
            DeleteUpdateArtifacts(journal);
            return true;
        }

        await _runtime.StopAndConfirmAsync(_paths, current, cancellationToken);
        MachineConfigStore.Save(_paths.MachineConfigPath, journal.OriginalConfig);
        RestoreTarget(journal);
        _transitionProbe?.Invoke("restored_files");
        await _runtime.StartAndConfirmAsync(
            _paths, journal.OriginalConfig, cancellationToken);
        UpdateJournalStore.Delete(_paths.UpdateJournalPath);
        DeleteUpdateArtifacts(journal);
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
        if (updateAvailable &&
            string.Equals(release.Version, config.ActiveVersion,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "El feed intenta sobrescribir la versión activa con otra sequence.");
        }

        if (updateAvailable &&
            !string.IsNullOrWhiteSpace(config.PreviousVersion) &&
            string.Equals(release.Version, config.PreviousVersion,
                StringComparison.OrdinalIgnoreCase) &&
            (release.Sequence != config.PreviousSequence ||
             !string.Equals(release.Version, config.PreviousVersion,
                 StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                "La versión anterior tiene otra identidad o sequence.");
        }

        return new UpdateCheckResult(
            config.ActiveVersion,
            config.ActiveSequence,
            highest,
            release,
            updateAvailable,
            release.MinUpdateProtocol >
                UpdateTrust.SupportedProtocol);
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
            package.UpdateProtocol < release.MinUpdateProtocol ||
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
