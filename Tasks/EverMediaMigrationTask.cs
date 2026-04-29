using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Tasks;
using MediaBrowser.Model.Logging;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using EverMedia.Services;
using MediaBrowser.Model.Serialization;

namespace EverMedia.Tasks;

public class EverMediaMigrationTask : IScheduledTask
{
    private readonly ILogger _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly EverMediaService _everMediaService;
    private readonly IJsonSerializer _jsonSerializer;

    public EverMediaMigrationTask(ILogManager logManager, ILibraryManager libraryManager, EverMediaService everMediaService, IJsonSerializer jsonSerializer)
    {
        _logger = logManager.GetLogger(GetType().Name);
        _libraryManager = libraryManager;
        _everMediaService = everMediaService;
        _jsonSerializer = jsonSerializer;
    }

    public string Name => "EverMedia: import .medinfo to Database";
    public string Key => "EverMediaMigrationTask";
    public string Description => "扫描硬盘上的 .medinfo 文件并自动导入到 LiteDB 数据库中（请在迁移完成后禁用此任务）。";
    public string Category => "EverMedia";

    public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null) return;

        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { "Movie", "Episode", "Video" },
            IsVirtualItem = false
        }).Where(i => !string.IsNullOrEmpty(i.Path) && i.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase)).ToList();

        int total = items.Count;
        int migratedCount = 0;
        int alreadyInDbCount = 0;

        for (int i = 0; i < total; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = items[i];

            if (await _everMediaService.HasBackupAsync(item))
            {
                alreadyInDbCount++;
                progress.Report((i + 1.0) / total * 100);
                continue;
            }

            string? jsonContent = null;

            // 1. Check SideBySide
            string sideBySidePath = Path.ChangeExtension(item.Path, ".medinfo");
            if (File.Exists(sideBySidePath))
            {
                jsonContent = await File.ReadAllTextAsync(sideBySidePath, cancellationToken);
            }
            // 2. Check Centralized
            else if (!string.IsNullOrWhiteSpace(config.CentralizedRootPath))
            {
                string libraryPath = GetLibraryRootPath(item);
                if (!string.IsNullOrEmpty(libraryPath) && item.Path != null)
                {
                    string relativeDir = Path.GetRelativePath(libraryPath, Path.GetDirectoryName(item.Path) ?? string.Empty);
                    string centralPath = Path.Combine(config.CentralizedRootPath, relativeDir, Path.GetFileNameWithoutExtension(item.Path) + ".medinfo");
                    if (File.Exists(centralPath))
                    {
                        jsonContent = await File.ReadAllTextAsync(centralPath, cancellationToken);
                    }
                }
            }

            if (!string.IsNullOrEmpty(jsonContent))
            {
                try
                {
                    var dto = _jsonSerializer.DeserializeFromString<EverMediaService.BackupDto>(jsonContent);
                    if (dto != null)
                    {
                        // Save directly to LiteDB through the migration service
                        await _everMediaService.MigrateToDatabaseAsync(item, dto, dto.ExternalSubtitleCount);
                        migratedCount++;
                        _logger.Info($"[EverMedia Migration] Migrated: {item.Name}");
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error($"[EverMedia Migration] Failed to parse .medinfo for {item.Name}: {ex.Message}");
                }
            }

            progress.Report((i + 1.0) / total * 100);
        }

        _logger.Info($"[EverMedia Migration] Completed! Total .strm: {total}. Already in DB: {alreadyInDbCount}. Newly Migrated: {migratedCount}.");
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return Enumerable.Empty<TaskTriggerInfo>();
    }

    private string GetLibraryRootPath(BaseItem item)
    {
        var collectionFolder = _libraryManager.GetCollectionFolders(item).FirstOrDefault();
        if (collectionFolder != null && !string.IsNullOrEmpty(collectionFolder.Path))
        {
            return collectionFolder.Path;
        }
        var topParent = item.GetTopParent();
        if (topParent != null && !string.IsNullOrEmpty(topParent.Path))
        {
            return topParent.Path;
        }
        return string.Empty;
    }
}
