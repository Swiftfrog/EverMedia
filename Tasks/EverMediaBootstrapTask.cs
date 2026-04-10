// Tasks/EverMediaBootstrapTask.cs
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;

using EverMedia.Services;

namespace EverMedia.Tasks;

// 计划任务：扫描并持久化 .strm 文件的 MediaInfo。
public class EverMediaBootstrapTask : IScheduledTask 
{
    private readonly ILogger _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly EverMediaService _everMediaService;
    private readonly IFileSystem _fileSystem;

    // --- 用于速率限制的线程安全锁 ---
    private readonly object _rateLimitLock = new();

    // --- 构造函数：接收依赖项 ---
    public EverMediaBootstrapTask(
        ILogManager logManager,
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        EverMediaService everMediaService,
        IFileSystem fileSystem
    )
    {
        _logger = logManager.GetLogger(GetType().Name);
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _everMediaService = everMediaService;
        _fileSystem = fileSystem; // 保存注入的 IFileSystem
    }

    public string Name => "EverMedia Bootstrap Task";

    public string Key => "EverMediaBootstrapTask";

    public string Description => "Scan and persist MediaInfo for .strm files.";

    public string Category => "EverMedia";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // 示例：如果希望任务每天凌晨 2 点运行，可以这样配置：
        // yield return new TaskTriggerInfo
        // {
        //     Type = TaskTriggerInfo.TriggerDaily,
        //     TimeOfDayTicks = TimeSpan.FromHours(2).Ticks // 2 AM
        // };

        return Array.Empty<TaskTriggerInfo>(); // 返回空集合
    }

    // --- 核心执行方法 ---
    public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
    {
        
        var config = Plugin.Instance.Configuration;
        if (config == null)
        {
            _logger.Warn("[EverMedia] BootstrapTask: Plugin configuration is null. Skipping execution.");
            progress?.Report(100);
            return;
        }
    
        if (!config.EnableBootstrapTask)
        {
            _logger.Info("[EverMedia] BootstrapTask: Task execution is disabled via configuration. Exiting.");
            progress?.Report(100);
            return;
        }
    
        _logger.Info("[EverMedia] BootstrapTask: Task execution started.");

        // 记录任务开始时间，用于后续更新配置和查询
        var taskStartTime = DateTime.UtcNow;

        try
        {
            // 智能扫描：高效查询库中所有可能的 .strm 文件
            // 使用 MinDateLastSaved 实现增量更新
            var lastRunTimestamp = config.LastBootstrapTaskRun;
            
            _logger.Info($"[EverMedia] BootstrapTask: Querying library for .strm files with metadata updated since {lastRunTimestamp?.ToString("O") ?? "the beginning of time"}...");

            var query = new InternalItemsQuery
            {
                // .strm 文件在 Emby 中被识别为视频类型。这是最有效的数据库索引过滤条件。
                MediaTypes = new[] { MediaType.Video },

                // 确保返回的项目都有一个文件系统路径，这是处理 .strm 文件的先决条件。
                HasPath = true,

                // 至关重要：确保查询能深入媒体库的所有子文件夹，以找到所有 .strm 文件。
                Recursive = true,

                // 只查询自上次运行后元数据被保存过的项目
                MinDateLastSaved = lastRunTimestamp
            };

            var allVideoItems = _libraryManager.GetItemList(query);

            // 过滤出 Path 以 .strm 结尾的项目
            var strmItemsToProcess = allVideoItems.Where(item => item.Path != null && item.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase)).ToList();

            _logger.Info($"[EverMedia] BootstrapTask: Found {strmItemsToProcess.Count} .strm files with metadata updated since last run to process.");

            // 计算总进度 (基于过滤后的列表)
            var totalItems = strmItemsToProcess.Count;
            if (totalItems == 0)
            {
                _logger.Info("[EverMedia] BootstrapTask: No .strm files found with updated metadata since last run. Task completed.");
                progress?.Report(100);
                return;
            }

            var processedCount = 0;
            var restoredCount = 0;
            var probedCount = 0;
            var backedUpCount = 0;
            var skippedCount = 0;

            // --- Rate Limiting: Config-based delay using TimeSpan ---
            // var configRateLimitSeconds = config.BootstrapTaskRateLimitSeconds;
            var configRateLimitSeconds = config.TaskConfig.BootstrapTaskRateLimitSeconds;
            TimeSpan rateLimitInterval;
            if (configRateLimitSeconds <= 0)
            {
                // 如果配置值 <= 0，则禁用速率限制
                rateLimitInterval = TimeSpan.Zero;
                _logger.Info("[EverMedia] BootstrapTask: Rate limiting is disabled (BootstrapTaskRateLimitSeconds <= 0).");
            }
            else
            {
                // 否则，使用配置的秒数创建 TimeSpan
                rateLimitInterval = TimeSpan.FromSeconds(configRateLimitSeconds);
                _logger.Info($"[EverMedia] BootstrapTask: Rate limiting enabled: {rateLimitInterval.TotalSeconds} seconds interval between FFProbe calls.");
            }

            var lastProbeStart = DateTimeOffset.MinValue;

            // --- Concurrency Control ---
            // var maxConcurrency = config.MaxConcurrency > 0 ? config.MaxConcurrency : 1;
            var maxConcurrency = config.TaskConfig.MaxConcurrency > 0 ? config.TaskConfig.MaxConcurrency : 1;
            using var semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);

            // 使用自定义并发控制
            var tasks = new List<Task>();

            var directoryService = new DirectoryService(_logger, _fileSystem);
            var refreshOptions = new MetadataRefreshOptions(directoryService)
            {
                EnableRemoteContentProbe = true,
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                ReplaceAllMetadata = false, // 不替换其他元数据
                ImageRefreshMode = MetadataRefreshMode.ValidationOnly, // 不强制刷新图片
                ReplaceAllImages = false,
                EnableThumbnailImageExtraction = false, // 不提取缩略图
                EnableSubtitleDownloading = false // 不下载字幕
            };

            foreach (var item in strmItemsToProcess)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.Info("[EverMedia] BootstrapTask: Task execution was cancelled during processing.");
                    break;
                }

                // 等待并发信号量，控制同时运行的探测数
                await semaphore.WaitAsync(cancellationToken);

                var task = Task.Run(async () =>
                {
                    try
                    {
                        // 检查取消令牌（在获取到并发许可后再次检查）
                        if (cancellationToken.IsCancellationRequested) return;

                        // --- Config-based Rate Limiting Logic (with thread-safe lock) ---
                        if (rateLimitInterval > TimeSpan.Zero)
                        {
                            TimeSpan timeToWait;
                            lock (_rateLimitLock)
                            {
                                DateTimeOffset now = DateTimeOffset.UtcNow;
                                TimeSpan timeElapsed = now - lastProbeStart;
                                
                                if (timeElapsed >= rateLimitInterval)
                                {
                                    // 距离上次探测已经超过间隔，无需等待，直接占用当前时间槽
                                    timeToWait = TimeSpan.Zero;
                                    lastProbeStart = now;
                                }
                                else
                                {
                                    // 距离上次探测太近，必须等待。计算等待时间并预订未来的时间槽
                                    timeToWait = rateLimitInterval - timeElapsed;
                                    lastProbeStart = lastProbeStart + rateLimitInterval;
                                }
                            }
                        
                            if (timeToWait > TimeSpan.Zero)
                            {
                                _logger.Debug($"[EverMedia] BootstrapTask: Waiting {timeToWait.TotalMilliseconds:F0}ms before processing {item.Path} to respect rate limit.");
                                await Task.Delay(timeToWait, cancellationToken);
                            }
                        }
                        // --- End of Rate Limiting Logic ---

                        _logger.Debug($"[EverMedia] BootstrapTask: Processing .strm file: {item.Path} (DateLastSaved: {item.DateLastSaved:O})");

                        // 检查是否存在 .medinfo 文件
                        string medInfoPath = _everMediaService.GetMedInfoPath(item);
                        bool hasMediaInfo = item.GetMediaStreams()?.Any(i => i.Type == MediaStreamType.Video || i.Type == MediaStreamType.Audio) ?? false;

                        if (_fileSystem.FileExists(medInfoPath))
                        {
                            if (!hasMediaInfo)
                            {
                                _logger.Info($"[EverMedia] BootstrapTask: Found .medinfo file but no MediaInfo for {item.Path}. Attempting restore.");
                                
                                // 存在 .medinfo 文件且媒体信息丢失：尝试恢复 (自愈)
                                var restoreResult = await _everMediaService.RestoreAsync(item);
                                if (restoreResult)
                                {
                                    Interlocked.Increment(ref restoredCount);
                                    _logger.Info($"[EverMedia] BootstrapTask: Successfully restored MediaInfo for {item.Path}.");
                                }
                                else
                                {
                                    _logger.Warn($"[EverMedia] BootstrapTask: Failed to restore MediaInfo for {item.Path}.");
                                }
                            }
                            else
                            {
                                // Emby有源数据且有medinfo备份，检查是否是字幕发生变化
                                int savedSubCount = _everMediaService.GetSavedExternalSubCount(item);
                                int currentSubCount = item.GetMediaStreams()?.Count(s => s.Type == MediaStreamType.Subtitle && s.IsExternal) ?? 0;
                                
                                if (currentSubCount != savedSubCount)
                                {
                                    _logger.Info($"[EverMedia] BootstrapTask: Subtitle change detected for {item.Path}. Deleting medinfo and triggering probe.");
                                    try { _fileSystem.DeleteFile(medInfoPath); } catch { }
                                    await item.RefreshMetadata(refreshOptions, cancellationToken);
                                    Interlocked.Increment(ref probedCount);
                                }
                                else
                                {
                                    _logger.Debug($"[EverMedia] BootstrapTask: {item.Path} is up-to-date. Skipping.");
                                    Interlocked.Increment(ref skippedCount);
                                }
                            }
                        }
                        else
                        {
                            _logger.Debug($"[EverMedia] BootstrapTask: No .medinfo file found for {item.Path}.");
                            
                            // 不存在 .medinfo 文件：
                            if (!hasMediaInfo)
                            {
                                _logger.Info($"[EverMedia] BootstrapTask: No MediaInfo found for {item.Path} and no .medinfo file. Attempting probe.");
                                // 没有 MediaStreams 且没有 .medinfo 文件：触发探测
                                // 使用预先创建的 MetadataRefreshOptions 来触发探测
                                await item.RefreshMetadata(refreshOptions, cancellationToken);
                                
                                // 探测成功后，ItemUpdated 事件会被触发，EventListener 会处理备份
                                Interlocked.Increment(ref probedCount);
                                _logger.Info($"[EverMedia] BootstrapTask: Probe initiated for {item.Path}. Event listener will handle backup if successful.");
                            }
                            else
                            {
                                // 有 MediaInfo 但没有 .medinfo → 立即备份
                                _logger.Info($"[EverMedia] BootstrapTask: MediaInfo exists for {item.Path} but no .medinfo file. Backing up now.");
                                var backupResult = await _everMediaService.BackupAsync(item);
                                if (backupResult)
                                {
                                    Interlocked.Increment(ref backedUpCount);
                                    _logger.Info($"[EverMedia] BootstrapTask: Successfully backed up MediaInfo for {item.Path}.");
                                }
                                else
                                {
                                    _logger.Warn($"[EverMedia] BootstrapTask: Failed to back up MediaInfo for {item.Path}.");
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"[EverMedia] BootstrapTask: Error processing item {item.Path}: {ex.Message}");
                        _logger.Debug(ex.StackTrace);
                    }
                    finally
                    {
                        semaphore.Release();
                        Interlocked.Increment(ref processedCount);
                        var currentProgress = (double)processedCount / totalItems * 100.0;
                        progress?.Report(currentProgress);
                    }
                }, cancellationToken);

                tasks.Add(task);
            }

            // 等待所有任务完成
            await Task.WhenAll(tasks);

            var totalProcessed = restoredCount + probedCount + backedUpCount + skippedCount;
            _logger.Info($"[EverMedia] BootstrapTask: Task execution completed. Total .strm files processed: {totalProcessed}. Restored from .medinfo: {restoredCount}, Probed for new meta {probedCount}, Backup for media info existed: {backedUpCount},  Skipped: {skippedCount}.");

            // 在任务成功完成后，记录任务**开始时**的时间戳作为下一次运行的基准。
            // 使用开始时间而不是结束时间，可以确保在此任务运行期间（这可能需要几个小时）修改或添加的项目
            // 不会在下一次增量查询中被漏掉。
            Plugin.Instance.UpdateLastBootstrapTaskRun(taskStartTime);
            _logger.Info($"[EverMedia] BootstrapTask: Last run timestamp updated to task start time: {taskStartTime:O} via Plugin.Instance.");

        }
        catch (OperationCanceledException)
        {
            // 任务被取消，不应该更新 LastBootstrapTaskRun 时间戳
            _logger.Info("[EverMedia] BootstrapTask: Task execution was cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error($"[EverMedia] BootstrapTask: Task execution failed: {ex.Message}");
            _logger.Debug(ex.StackTrace);
            throw;
        }
    }
}
