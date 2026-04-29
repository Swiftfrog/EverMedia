// Services/MediaInfoService.cs
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Serialization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using MediaBrowser.Common.Configuration;

namespace EverMedia.Services;

// .strm 文件 MediaInfo 的备份与恢复逻辑。
public class EverMediaService
{
    private readonly ILogger _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly IItemRepository _itemRepository;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;
    private readonly IJsonSerializer _jsonSerializer;
    private readonly IServerApplicationHost _applicationHost;
    private readonly IApplicationPaths _applicationPaths;

    private Plugin? _cachedPlugin;
    private readonly string _dbPath;

    public EverMediaService(
        ILogManager logManager,
        ILibraryManager libraryManager,
        IItemRepository itemRepository,
        IProviderManager providerManager,
        IFileSystem fileSystem,
        IJsonSerializer jsonSerializer,
        IServerApplicationHost applicationHost,
        IApplicationPaths applicationPaths)
    {
        _logger = logManager.GetLogger(GetType().Name);
        _libraryManager = libraryManager;
        _itemRepository = itemRepository;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
        _jsonSerializer = jsonSerializer;
        _applicationHost = applicationHost;
        _applicationPaths = applicationPaths;
        
        _dbPath = Path.Combine(_applicationPaths.PluginConfigurationsPath, "EverMedia.db");
        InitializeDatabase();
    }

    private Plugin? GetPlugin()
    {
        return _cachedPlugin ??= _applicationHost.Plugins.OfType<Plugin>().FirstOrDefault();
    }

    private EverMediaConfig? GetConfiguration()
    {
        return GetPlugin()?.Configuration;
    }

    private void InitializeDatabase()
    {
        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();
        var command = connection.CreateCommand();
        command.CommandText = @"
            CREATE TABLE IF NOT EXISTS MediaInfo (
                InternalId INTEGER PRIMARY KEY,
                Path TEXT,
                BackupData TEXT,
                ExternalSubCount INTEGER,
                LastUpdated INTEGER
            );
        ";
        command.ExecuteNonQuery();
    }

    public async Task<bool> HasBackupAsync(BaseItem item)
    {
        var config = GetConfiguration();
        if (config?.BackupMode == BackupMode.Centralized)
        {
            await using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM MediaInfo WHERE InternalId = $id";
            command.Parameters.AddWithValue("$id", item.InternalId);
            var result = await command.ExecuteScalarAsync();
            return result != null;
        }
        else
        {
            return _fileSystem.FileExists(GetMedInfoPath(item));
        }
    }

    public async Task DeleteBackupAsync(BaseItem item)
    {
        var config = GetConfiguration();
        if (config?.BackupMode == BackupMode.Centralized)
        {
            await using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM MediaInfo WHERE InternalId = $id";
            command.Parameters.AddWithValue("$id", item.InternalId);
            await command.ExecuteNonQueryAsync();
        }
        else
        {
            string path = GetMedInfoPath(item);
            try { _fileSystem.DeleteFile(path); } catch { }
        }
    }

    // --- 核心方法：备份 MediaInfo ---
    public async Task<bool> BackupAsync(BaseItem item)
    {
        _logger.Info($"[EverMedia] Service: Starting BackupAsync for item: {item.Name ?? item.Path} (ID: {item.Id})");

        var config = GetConfiguration();
        if (config == null)
        {
            _logger.Error("[EverMedia] Service: Failed to get plugin configuration for BackupAsync.");
            return false;
        }

        try
        {
            var libraryOptions = _libraryManager.GetLibraryOptions(item);
            if (libraryOptions == null)
            {
                _logger.Error($"[EverMedia] Service: Failed to get LibraryOptions for item: {item.Name ?? item.Path}. Cannot proceed with backup.");
                return false;
            }
            
            var mediaSources = item.GetMediaSources(false, false, libraryOptions)?.ToList();
            
            if (mediaSources == null || !mediaSources.Any())
            {
                _logger.Info($"[EverMedia] Service: No MediaSources found for item: {item.Name ?? item.Path}. Skipping backup.");
                return false;
            }
            
            var chapters = _itemRepository.GetChapters(item);

            var mediaSourcesWithChapters = mediaSources.Select(ms => new MediaSourceWithChapters
            {
                MediaSourceInfo = ms,
                Chapters = chapters.ToList()
            }).ToList();

            var validSourcesWithChapters = mediaSourcesWithChapters.Where(swc => swc.MediaSourceInfo != null).ToList();
            if (!validSourcesWithChapters.Any())
            {
                _logger.Warn($"[EverMedia] Service: All MediaSourceInfo objects were null for item: {item.Name ?? item.Path}. Skipping backup.");
                return false;
            }

            foreach (var sourceWithChapters in validSourcesWithChapters)
            {
                var msInfo = sourceWithChapters.MediaSourceInfo!;
                msInfo.Id = null;
                msInfo.ItemId = null;
                msInfo.Path = null;

                if (msInfo.MediaStreams != null)
                {
                    foreach (var stream in msInfo.MediaStreams.Where(s => s.IsExternal && s.Type == MediaStreamType.Subtitle && s.Path != null))
                    {
                        stream.Path = Path.GetFileName(stream.Path);
                    }
                }

                foreach (var chapter in sourceWithChapters.Chapters)
                {
                    chapter.ImageTag = null;
                }
            }

            var plugin = GetPlugin();
            var pluginVersionString = plugin?.Version.ToString() ?? "Unknown";

            // 计算当前的外挂字幕数量
            var allStreams = item.GetMediaStreams();
            int externalSubCount = allStreams?.Count(s => s.Type == MediaStreamType.Subtitle && s.IsExternal) ?? 0;
            _logger.Debug($"[EverMedia] Service: Found {externalSubCount} external subtitles to save in backup for {item.Name}");

            var backupData = new BackupDto
            {
                EmbyVersion = _applicationHost.ApplicationVersion.ToString(),
                PluginVersion = pluginVersionString,
                ExternalSubtitleCount = externalSubCount,
                Data = validSourcesWithChapters.ToArray()
            };

            if (config.BackupMode == BackupMode.Centralized)
            {
                var jsonString = _jsonSerializer.SerializeToString(backupData);
                await using var connection = new SqliteConnection($"Data Source={_dbPath}");
                await connection.OpenAsync();
                var command = connection.CreateCommand();
                command.CommandText = @"
                    INSERT INTO MediaInfo (InternalId, Path, BackupData, ExternalSubCount, LastUpdated)
                    VALUES ($id, $path, $data, $count, $time)
                    ON CONFLICT(InternalId) DO UPDATE SET
                        Path = excluded.Path,
                        BackupData = excluded.BackupData,
                        ExternalSubCount = excluded.ExternalSubCount,
                        LastUpdated = excluded.LastUpdated;
                ";
                command.Parameters.AddWithValue("$id", item.InternalId);
                command.Parameters.AddWithValue("$path", item.Path ?? string.Empty);
                command.Parameters.AddWithValue("$data", jsonString);
                command.Parameters.AddWithValue("$count", externalSubCount);
                command.Parameters.AddWithValue("$time", System.DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                await command.ExecuteNonQueryAsync();
                _logger.Info($"[EverMedia] Service: Backup completed (Centralized DB) for item: {item.Name ?? item.Path}");
            }
            else
            {
                string medInfoPath = GetMedInfoPath(item);
                var parentDir = Path.GetDirectoryName(medInfoPath);
                if (!string.IsNullOrEmpty(parentDir) && !_fileSystem.DirectoryExists(parentDir))
                {
                    _fileSystem.CreateDirectory(parentDir);
                }
                await Task.Run(() => _jsonSerializer.SerializeToFile(backupData, medInfoPath));
                _logger.Info($"[EverMedia] Service: Backup completed for item: {item.Name ?? item.Path}. File written: {medInfoPath}");
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.Error($"[EverMedia] Service: Error during BackupAsync for item {item.Name ?? item.Path}: {ex.Message}");
            _logger.Debug(ex.StackTrace);
            return false;
        }
    }

    // --- 恢复 MediaInfo ---
    public async Task<bool> RestoreAsync(BaseItem item)
    {
        _logger.Info($"[EverMedia] Service: Starting RestoreAsync for item: {item.Name ?? item.Path} (ID: {item.Id})");
    
        var config = GetConfiguration();
        if (config == null)
        {
            _logger.Error("[EverMedia] Service: Failed to get plugin configuration for RestoreAsync.");
            return false;
        }
    
        try
        {
            BackupDto? backupDto = null;
            string medInfoPath = string.Empty;

            if (config.BackupMode == BackupMode.Centralized)
            {
                await using var connection = new SqliteConnection($"Data Source={_dbPath}");
                await connection.OpenAsync();
                var command = connection.CreateCommand();
                command.CommandText = "SELECT BackupData FROM MediaInfo WHERE InternalId = $id";
                command.Parameters.AddWithValue("$id", item.InternalId);
                var result = await command.ExecuteScalarAsync();
                if (result is string jsonString)
                {
                    backupDto = _jsonSerializer.DeserializeFromString<BackupDto>(jsonString);
                }

                if (backupDto == null)
                {
                    _logger.Info($"[EverMedia] Service: No medinfo found in DB for item: {item.Name ?? item.Path}.");
                    return false;
                }
            }
            else
            {
                medInfoPath = GetMedInfoPath(item);
                _logger.Debug($"[EverMedia] Service: Looking for medinfo file: {medInfoPath}");

                if (!_fileSystem.FileExists(medInfoPath))
                {
                    _logger.Info($"[EverMedia] Service: No medinfo file found for item: {item.Name ?? item.Path}. Path checked: {medInfoPath}");
                    return false;
                }

                try
                {
                    backupDto = _jsonSerializer.DeserializeFromFile<BackupDto>(medInfoPath);
                }
                catch (Exception ex)
                {
                    _logger.Error($"[EverMedia] Service: Error deserializing medinfo file {medInfoPath} into BackupDto: {ex.Message}");
                    _logger.Debug(ex.StackTrace);
                    return false;
                }
            }
    
            if (backupDto == null || backupDto.Data == null || !backupDto.Data.Any())
            {
                _logger.Warn($"[EverMedia] Service: No data found in medinfo file {medInfoPath}.");
                return false;
            }
    
            _logger.Debug($"[EverMedia] Service: Restoring from EmbyVersion: {backupDto.EmbyVersion ?? "Unknown"}, PluginVersion: {backupDto.PluginVersion ?? "Unknown"}");
    
            var sourceToRestore = backupDto.Data.First();
            var mediaSourceInfo = sourceToRestore.MediaSourceInfo;
            var chaptersToRestore = sourceToRestore.Chapters ?? new List<ChapterInfo>();
    
            if (mediaSourceInfo == null)
            {
                _logger.Warn($"[EverMedia] Service: MediaSourceInfo in medinfo file {medInfoPath} is null.");
                return false;
            }
    
            item.Size = mediaSourceInfo.Size.GetValueOrDefault();
            item.RunTimeTicks = mediaSourceInfo.RunTimeTicks;
            item.Container = mediaSourceInfo.Container;
            item.TotalBitrate = mediaSourceInfo.Bitrate.GetValueOrDefault();
    
            var videoStream = mediaSourceInfo.MediaStreams
                .Where(s => s.Type == MediaStreamType.Video && s.Width.HasValue && s.Height.HasValue)
                .OrderByDescending(s => (long)s.Width!.Value * s.Height!.Value)
                .FirstOrDefault();
    
            if (videoStream != null)
            {
                item.Width = videoStream.Width.GetValueOrDefault();
                item.Height = videoStream.Height.GetValueOrDefault();
            }
    
            var streamsToSave = mediaSourceInfo.MediaStreams?.ToList() ?? new List<MediaStream>();
            foreach (var stream in streamsToSave.Where(s => s.IsExternal && s.Type == MediaStreamType.Subtitle && s.Path != null))
            {
                stream.Path = Path.Combine(item.ContainingFolderPath, stream.Path);
            }
    
            _logger.Debug($"[EverMedia] Service: Saving {streamsToSave.Count} media streams for item: {item.Name ?? item.Path}");
            _itemRepository.SaveMediaStreams(item.InternalId, streamsToSave, CancellationToken.None);
    
            foreach (var chapter in chaptersToRestore)
            {
                chapter.ImageTag = null;
            }
    
            _logger.Debug($"[EverMedia] Service: Saving {chaptersToRestore.Count} chapters for item: {item.Name ?? item.Path}");
            _itemRepository.SaveChapters(item.InternalId, true, chaptersToRestore);
            _libraryManager.UpdateItem(item, item.Parent, ItemUpdateType.MetadataImport, null);
    
            _logger.Info($"[EverMedia] Service: Restore completed successfully for item: {item.Name ?? item.Path}.");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error($"[EverMedia] Service: Error during RestoreAsync for item {item.Name ?? item.Path}: {ex.Message}");
            _logger.Debug(ex.StackTrace);
            return false;
        }
    }

    // --- GetMedInfoPath：支持多路径库 + 手动相对路径 ---
    public string GetMedInfoPath(BaseItem item)
    {
        if (string.IsNullOrEmpty(item.Path))
        {
            _logger.Warn($"[EverMedia] Service: Item path is null or empty for ID: {item.Id}. Using fallback path.");
            string fallbackDir = item.ContainingFolderPath ?? string.Empty;
            return Path.Combine(fallbackDir, item.Id.ToString() + ".medinfo");
        }

        string fileName = Path.GetFileNameWithoutExtension(item.Path) + ".medinfo";
        return Path.Combine(item.ContainingFolderPath, fileName);
    }

    // --- 从 .medinfo 读取外挂字幕计数 ---
    public int GetSavedExternalSubCount(BaseItem item)
    {
        var config = GetConfiguration();
        if (config?.BackupMode == BackupMode.Centralized)
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();
            var command = connection.CreateCommand();
            command.CommandText = "SELECT ExternalSubCount FROM MediaInfo WHERE InternalId = $id";
            command.Parameters.AddWithValue("$id", item.InternalId);
            var result = command.ExecuteScalar();
            if (result != null && int.TryParse(result.ToString(), out int count))
            {
                return count;
            }
            return 0;
        }
        else
        {
            string medInfoPath = GetMedInfoPath(item);
            if (!_fileSystem.FileExists(medInfoPath))
            {
                _logger.Debug($"[EverMedia] Service: GetSavedExternalSubCount: No medinfo file found for {item.Name}. Returning 0.");
                return 0;
            }

            try
            {
                var backupDto = _jsonSerializer.DeserializeFromFile<BackupDto>(medInfoPath);
                if (backupDto != null)
                {
                    _logger.Debug($"[EverMedia] Service: GetSavedExternalSubCount: Found {backupDto.ExternalSubtitleCount} saved external subs in {medInfoPath}.");
                    return backupDto.ExternalSubtitleCount;
                }
            }
            catch (Exception ex)
            {
                _logger.Warn($"[EverMedia] Service: Error deserializing {medInfoPath} in GetSavedExternalSubCount: {ex.Message}");
            }
            return 0;
        }
    }

    private class BackupDto
    {
        public string? EmbyVersion { get; set; }
        public string? PluginVersion { get; set; }
        public int ExternalSubtitleCount { get; set; }
        public MediaSourceWithChapters[] Data { get; set; } = Array.Empty<MediaSourceWithChapters>();
    }

    internal class MediaSourceWithChapters
    {
        public MediaSourceInfo? MediaSourceInfo { get; set; }
        public List<ChapterInfo> Chapters { get; set; } = new List<ChapterInfo>();
        public bool? ZeroFingerprintConfidence { get; set; }
        public string? EmbeddedImage { get; set; }
    }
}
