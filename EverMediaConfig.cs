// EverMediaConfig.cs
using MediaBrowser.Model.Attributes;
using Emby.Web.GenericEdit;
using System.ComponentModel;

namespace EverMedia;

public class EverMediaConfig : EditableOptionsBase
{
    public override string EditorTitle => "EverMedia Settings";

    [DisplayName("启用插件")]
    [Description("启用或禁用插件的核心功能（实时监听 .strm 文件变化）。")]
    public bool EnablePlugin { get; set; } = false; // 默认关闭

    [DisplayName("启用引导任务")]
    [Description("启用或禁用计划任务（扫描并持久化 .strm 文件的 MediaInfo）。")]
    public bool EnableBootstrapTask { get; set; } = false; // 默认关闭

    [DisplayName("备份模式")]
    [Description("选择存储方式。SideBySide: 与 .strm 文件同级目录存放 .medinfo 文件；Centralized: 统一存储在插件内置的高性能 LiteDB 数据库中。")]
    public BackupMode BackupMode { get; set; } = BackupMode.Centralized;

    [DisplayName("旧数据迁移目录")]
    [Description("【仅用于迁移任务】如果您以前使用的是独立文件夹存放 .medinfo 文件，请在这里填写旧的集中存放路径。")]
    [EditFolderPicker]
    public string CentralizedRootPath { get; set; } = "";

    // [DisplayName("启用孤立文件清理");
    // [Description("清理不再有对应 .strm 文件的 .medinfo 文件。");
    // public bool EnableOrphanCleanup { get; set; } = false;

    [DisplayName("上次任务运行时间（UTC）")]
    [Description("MediaInfo任务上次成功完成的 UTC 时间，用于增量扫描。如无必要，不要设置。")]
    public DateTime? LastBootstrapTaskRun { get; set; } = null; // 初始值为 null
    
    // --- 二级设置分组 1: 熔断策略 ---
    [DisplayName("高级设置：熔断策略")]
    [Description("配置.strm访问失败的重试次数与重置时间")]
    public ProbeFailureConfig FailureConfig { get; set; } = new ProbeFailureConfig();


    // --- 二级设置分组 2: 并发控制 ---
    [DisplayName("高级设置：线程访问")]
    [Description("配置多线程数量和访问间隔")]
    public ConcurrencyConfig TaskConfig { get; set; } = new ConcurrencyConfig();
    
}

public class ProbeFailureConfig : EditableOptionsBase
{
    public override string EditorTitle => "Probe Failure Settings";
    
    [DisplayName("FFProbe 最大重试次数")]
    [Description("当失败次数达到此限制后，插件将停止对该文件的刷新，直到“重置时间”过去。")]
    [MinValue(1), MaxValue(10)]
    public int MaxProbeRetries { get; set; } = 3;

    [DisplayName("FFProbe 失败重置时间 (分钟)")]
    [Description("达到最大重试次数后，需要重制时间过后，才能再次调用插件，防止死循环或过载。")]
    [MinValue(1)]
    public int ProbeFailureResetMinutes { get; set; } = 30;
}

public class ConcurrencyConfig : EditableOptionsBase
{
    public override string EditorTitle => "Concurrency Settings";
    
    [DisplayName("全局并发线程数")]
    [Description("同时处理 .strm 文件的最大线程数量。此设置同时应用于「计划任务」和「实时监控」。(注意：修改此值后，需重启 Emby Server 才能更新实时监控的线程池数量)。")]
    [MinValue(1)]
    public int MaxConcurrency { get; set; } = 2;

    [DisplayName("全局访问间隔（秒）")]
    [Description("每个线程处理两个文件之间的最小冷却时间（秒）。此设置同时应用于「计划任务」和「实时监控」，用于防止触发远程端风控。例如：并发数设为 2，间隔设为 2秒，则总体请求频率约为 1次/秒。此设置对所有模式生效。")]
    [MinValue(0), MaxValue(60)]
    public int BootstrapTaskRateLimitSeconds { get; set; } = 2;
}

public enum BackupMode { SideBySide, Centralized }