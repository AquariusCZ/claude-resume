using System.Text;
using System.Text.Json;
using AiResume.Secrets;
using Microsoft.Extensions.Logging;

namespace AiResume.Worker.Logging;

/// <summary>
/// 按日滚动的结构化单行 JSON 文件日志(S2-F;规格 §3.8)。
///
/// 每行一个 JSON 对象:{ ts(本地时间+偏移), level, component, run_id?, event, data }。
/// - data 是 formatter 输出,先经 SecretRedactor.RedactText 脱敏再写入;
/// - 任何路径不得写机密(机密明文绝不进入参数/状态/异常输出);
/// - 文件按日滚动:logs\worker-yyyyMMdd.log,并发写由锁串行化。
/// </summary>
public sealed class DailyJsonFileLoggerProvider : ILoggerProvider
{
    private readonly string _logsDirectory;
    private readonly string _filePrefix;

    public DailyJsonFileLoggerProvider(string logsDirectory, string filePrefix = "worker")
    {
        ArgumentNullException.ThrowIfNull(logsDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePrefix);
        _logsDirectory = logsDirectory;
        _filePrefix = filePrefix;
    }

    public ILogger CreateLogger(string categoryName) =>
        new DailyJsonFileLogger(_logsDirectory, _filePrefix, categoryName);

    public void Dispose()
    {
        // 无独立资源;文件句柄每次写时打开关闭。
    }

    private sealed class DailyJsonFileLogger : ILogger
    {
        private static readonly object WriteLock = new();
        private readonly string _logsDirectory;
        private readonly string _filePrefix;
        private readonly string _category;

        public DailyJsonFileLogger(string logsDirectory, string filePrefix, string category)
        {
            _logsDirectory = logsDirectory;
            _filePrefix = filePrefix;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            string message = formatter(state, exception);
            string data = SecretRedactor.RedactText(message);
            if (exception is not null)
            {
                data += " | exception=" + SecretRedactor.RedactText(exception.ToString());
            }

            DateTimeOffset now = DateTimeOffset.Now;
            string line = JsonSerializer.Serialize(new
            {
                ts = now.ToString("o"),
                level = logLevel.ToString().ToLowerInvariant(),
                component = ComponentName(),
                run_id = (string?)null,
                @event = string.IsNullOrEmpty(eventId.Name) ? "log" : eventId.Name,
                data,
            });

            Directory.CreateDirectory(_logsDirectory);
            string path = Path.Combine(_logsDirectory, $"{_filePrefix}-{now:yyyyMMdd}.log");
            lock (WriteLock)
            {
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
        }

        /// <summary>类别末段小写化作为组件名:AiResume.Worker.ObservationWorker → observationworker。</summary>
        private string ComponentName()
        {
            string last = _category;
            int dot = _category.LastIndexOf('.');
            if (dot >= 0 && dot < _category.Length - 1)
            {
                last = _category[(dot + 1)..];
            }

            return last.ToLowerInvariant();
        }
    }
}
