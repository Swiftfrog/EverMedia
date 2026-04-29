using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using EverMedia.Services;

namespace EverMedia.Events;

/// <summary>
/// 事件监听器：基于多线程队列的生产者-消费者模型。
/// 支持 MaxConcurrency 并发控制，兼顾效率与风控。
/// </summary>
public class EverMediaEventListener : IAsyncDisposable
{
    private readonly ILogger _logger;
    private readonly EverMediaService _everMediaService;
    private readonly IFileSystem _fileSystem;

    // --- 去抖动与重试追踪 ---
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _debounceTokens = new();
    private readonly ConcurrentDictionary<Guid, (int Count, DateTime LastAttempt)> _probeFailureTracker = new();
    private readonly TimeSpan _shortTermRetryDelay = TimeSpan.FromSeconds(10);

    // --- 队列处理系统 ---
    private readonly Channel<BaseItem> _processingChannel;
    private readonly ConcurrentDictionary<Guid, byte> _queuedItems = new(); // 队列去重
    private readonly CancellationTokenSource _disposeCts = new();
    
    // [修改] 变为任务列表，支持多线程
    private readonly List<Task> _workerTasks = new(); 

    public EverMediaEventListener(
        ILogger logger,
        EverMediaService everMediaService,
        IFileSystem fileSystem)
    {
        _logger = logger;
        _everMediaService = everMediaService;
        _fileSystem = fileSystem;

        // 创建无界通道
        _processingChannel = Channel.CreateUnbounded<BaseItem>();

        // [修改] 读取并发配置并启动对应数量的 Worker
        var config = Plugin.Instance.Configuration;
        int concurrency = config?.TaskConfig?.MaxConcurrency ?? 1;
        if (concurrency < 1) concurrency = 1;

        _logger.Info($"[EverMedia] Queue: Starting {concurrency} worker threads for real-time monitoring.");

        for (int i = 0; i < concurrency; i++)
        {
            int workerId = i + 1;
            _workerTasks.Add(Task.Run(() => ProcessQueueAsync(workerId)));
        }
    }

    // --- [修改] 消费者循环：增加 WorkerId ---
    private async Task ProcessQueueAsync(int workerId)
    {
        // _logger.Info($"[EverMedia] Worker-{workerId}: Started.");

        while (!_disposeCts.Token.IsCancellationRequested)
        {
            try
            {
                // 1. 等待并读取队列 (多个线程竞争读取，Channel 内部线程安全)
                var item = await _processingChannel.Reader.ReadAsync(_disposeCts.Token);

                // 2. 移除去重标记
                _queuedItems.TryRemove(item.Id, out _);

                // 3. 获取实时配置
                var config = Plugin.Instance.Configuration;
                int rateLimitSeconds = config?.TaskConfig?.BootstrapTaskRateLimitSeconds ?? 2;

                // 4. 执行核心逻辑
                await ProcessItemInternalAsync(item, workerId, config);

                // 5. [关键] 线程级限流
                // 每个线程独立休眠。如果并发是5，间隔是2秒，整体吞吐量约 2.5个/秒
                if (rateLimitSeconds > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(rateLimitSeconds), _disposeCts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                break; 
            }
            catch (Exception ex)
            {
                _logger.Error($"[EverMedia] Worker-{workerId}: Error in loop: {ex.Message}");
            }
        }
    }

    // --- 统一业务逻辑 (透传 workerId 用于日志) ---
    private async Task ProcessItemInternalAsync(BaseItem item, int workerId, EverMediaConfig? config)
    {
        try
        {
            if (config == null || !config.EnablePlugin) return;
            if (item == null) return;

            // _logger.Debug($"[EverMedia] Worker-{workerId}: Processing '{item.Name}'");

            bool medInfoExists = await _everMediaService.HasBackupAsync(item);
            
            var mediaStreams = item.GetMediaStreams();
            var hasVideoOrAudio = mediaStreams?.Any(s => s.Type == MediaStreamType.Video || s.Type == MediaStreamType.Audio) == true;

            // --- 优先判断：如果存在备份，检查字幕是否发生变化 ---
            if (medInfoExists)
            {
                int savedExternalCount = _everMediaService.GetSavedExternalSubCount(item);
                int currentExternalCount = mediaStreams?.Count(s => s.Type == MediaStreamType.Subtitle && s.IsExternal) ?? 0;

                if (currentExternalCount != savedExternalCount)
                {
                    _logger.Info($"[EverMedia] Worker-{workerId}: Subtitle mismatch for {item.Name} (Saved: {savedExternalCount}, Current: {currentExternalCount}). Probing.");
                    try { await _everMediaService.DeleteBackupAsync(item); } catch { }
                    await HandleProbeWithRetryAsync(item, config, workerId);
                    return; // 结束，等待探测完成后的事件
                }
            }

            // --- 场景 1: 恢复 ---
            if (!hasVideoOrAudio && medInfoExists)
            {
                _logger.Info($"[EverMedia] Worker-{workerId}: Restoring {item.Name} from backup.");
                await _everMediaService.RestoreAsync(item);
                _probeFailureTracker.TryRemove(item.Id, out _);
            }
            // --- 场景 2: 备份 ---
            else if (hasVideoOrAudio && !medInfoExists)
            {
                _logger.Info($"[EverMedia] Worker-{workerId}: Backup {item.Name}.");
                await _everMediaService.BackupAsync(item);
                _probeFailureTracker.TryRemove(item.Id, out _);
            }
            // --- 场景 3: 探测 (FFProbe) ---
            else if (!hasVideoOrAudio && !medInfoExists)
            {
                 await HandleProbeWithRetryAsync(item, config, workerId);
            }
            else
            {
                _probeFailureTracker.TryRemove(item.Id, out _);
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"[EverMedia] Worker-{workerId}: Error processing {item.Name}: {ex.Message}");
        }
    }

    private async Task HandleProbeWithRetryAsync(BaseItem item, EverMediaConfig config, int workerId)
    {
        var now = DateTime.UtcNow;
        int maxRetries = config.FailureConfig.MaxProbeRetries;
        TimeSpan resetInterval = TimeSpan.FromMinutes(config.FailureConfig.ProbeFailureResetMinutes);
        
        (int currentCount, DateTime lastAttempt) = _probeFailureTracker.GetValueOrDefault(item.Id, (0, DateTime.MinValue));

        if (currentCount >= maxRetries && (now - lastAttempt > resetInterval))
        {
            currentCount = 0;
            _logger.Info($"[EverMedia] Worker-{workerId}: Reset failure count for {item.Name}.");
        }

        if (currentCount >= maxRetries)
        {
            _logger.Debug($"[EverMedia] Worker-{workerId}: {item.Name} hit max retries. Skipping.");
            return;
        }

        if (now - lastAttempt < _shortTermRetryDelay)
        {
            await Task.Delay(_shortTermRetryDelay - (now - lastAttempt));
            now = DateTime.UtcNow;
        }

        currentCount++;
        _probeFailureTracker.AddOrUpdate(item.Id, (currentCount, now), (key, old) => (currentCount, now));

        _logger.Info($"[EverMedia] Worker-{workerId}: Triggering FFProbe for {item.Name} (Attempt {currentCount}/{maxRetries}).");
        
        // 调用原始的 FFProbe 方法
        await TriggerFullProbeAsync(item);
    }

    private void EnqueueItem(BaseItem item)
    {
        if (_queuedItems.TryAdd(item.Id, 0))
        {
            if (!_processingChannel.Writer.TryWrite(item))
            {
                _logger.Warn($"[EverMedia] Queue: Failed to enqueue {item.Name}.");
                _queuedItems.TryRemove(item.Id, out _);
            }
        }
    }

    public void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is BaseItem item && item.Path != null && item.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
        {
             EnqueueItem(item);
        }
    }

    public async void OnItemUpdated(object? sender, ItemChangeEventArgs e)
    {
        try
        {
            if (e.Item is BaseItem item && item.Path != null && item.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
            {
                var itemId = item.Id;
                if (_debounceTokens.TryGetValue(itemId, out var existingCts))
                {
                    try { existingCts.Cancel(); existingCts.Dispose(); } catch { }
                }

                var newCts = new CancellationTokenSource();
                _debounceTokens[itemId] = newCts;

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), newCts.Token);
                    EnqueueItem(item);
                }
                catch (OperationCanceledException) { }
                finally
                {
                    // 仅移除由当前任务创建的 token，防止被覆盖后错误移除他人的 token
                    if (_debounceTokens.TryGetValue(itemId, out var currentCts) && currentCts == newCts)
                    {
                        _debounceTokens.TryRemove(itemId, out _);
                    }
                    newCts.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"[EverMedia] Error in OnItemUpdated for {e.Item?.Name}: {ex}");
        }
    }

    private async Task TriggerFullProbeAsync(BaseItem item)
    {
        var directoryService = new DirectoryService(_logger, _fileSystem);
        var refreshOptions = new MetadataRefreshOptions(directoryService)
        {
            EnableRemoteContentProbe = true,
            MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
            ReplaceAllMetadata = false,
            ImageRefreshMode = MetadataRefreshMode.ValidationOnly,
            ReplaceAllImages = false,
            EnableThumbnailImageExtraction = false,
            EnableSubtitleDownloading = false
        };

        try
        {
            await item.RefreshMetadata(refreshOptions, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.Error($"[EverMedia] FFProbe failed for {item.Name}: {ex.Message}");
        }
    }

    // --- [修改] 资源销毁：等待所有 Worker ---
    public async ValueTask DisposeAsync()
    {
        _disposeCts.Cancel();
        _processingChannel.Writer.TryComplete();

        if (_workerTasks.Count > 0)
        {
            try 
            {
                // 等待所有线程完成当前工作
                await Task.WhenAll(_workerTasks);
            } 
            catch { }
        }
        _workerTasks.Clear();

        foreach (var kvp in _debounceTokens)
        {
            try { kvp.Value.Cancel(); kvp.Value.Dispose(); } catch { }
        }
        
        _debounceTokens.Clear();
        _probeFailureTracker.Clear();
        _queuedItems.Clear();
        _disposeCts.Dispose();
    }
}