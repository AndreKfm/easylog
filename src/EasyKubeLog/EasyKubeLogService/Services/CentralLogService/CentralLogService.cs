﻿using LogEntries;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace EasyKubeLogService.Services.CentralLogService
{
    //
    // CentralLogService: It's safe to call AddLogEntry in parallel
    //
    //                          +--------------+
    //                          |              |       +------------
    //    +-------------+       |    Central   |       |  Central  |
    //    | AddLogEntry | +---> |    Log       | +---> |  Log      |
    //    +-------------+       |    Service   |       |  Cache    |
    //                          |              |       +------------
    //                          |              |
    //                          |              |
    //                          +---+------+---+
    //                              |      ^
    //                              v      |
    //                          +---+------+---+
    //                          |              |
    //                          |   Channel    |
    //                          |              |
    //                          +--------------+
    //

    /// <summary>
    /// This class holds the logs passed to EasyKubeLogService
    /// </summary>
    public class CentralLogService : ICentralLogService, ICentralLogServiceQuery
    {
        private Channel<LogEntry> _logEntryChannel;
        private readonly ICentralLogServiceCache _cache;

        /// <summary>
        /// Creates a central object used to aggregate all incomming log entries
        /// </summary>
        /// <param name="cache"></param>
        /// <param name="maxEntriesInChannelQueue">Specifies how man entries can be added asynchronously to the channgel</param>
        public CentralLogService(ICentralLogServiceCache cache = null, int maxEntriesInChannelQueue = 1024)
        {
            _logEntryChannel = Channel.CreateBounded<LogEntry>(maxEntriesInChannelQueue);
            _cache = cache ?? throw new ArgumentNullException(nameof(cache)); // ?? new CentralLogServiceCache(new CentralLogServiceCacheSettings { }, config, logger);
        }

        public void Start()
        {
            Stop();
            _source = new CancellationTokenSource();
            _currentTask = Task.Factory.StartNew(WaitForNewEntriesAndWrite, TaskCreationOptions.LongRunning);
        }

        public void Stop()
        {
            _source.Cancel();
            _currentTask.Wait();
        }

        private Task _currentTask = Task.CompletedTask;
        private CancellationTokenSource _source = new CancellationTokenSource();

        private async Task WaitForNewEntriesAndWrite()
        {
            var token = _source.Token;
            while (token.IsCancellationRequested == false)
            {
                try
                {
                    var available = await _logEntryChannel.Reader.WaitToReadAsync(token);
                    if (!available) // If false the channel is closed
                        break;

                    var newEntry = await _logEntryChannel.Reader.ReadAsync(token);

                    if (token.IsCancellationRequested)
                        break;

                    //Trace.TraceInformation($"CentralLogService add log entry to cache: [{newEntry.FileName}] - [{newEntry.Lines}]");
                    _cache.AddEntry(newEntry);
                }
                catch (Exception e)
                {
                    Trace.TraceError($"WaitForNewEntriesAndWrite - Exception: {e.Message}");
                }
            }
        }

        public async ValueTask<bool> AddLogEntry(LogEntry newEntry)
        {
            try
            {
                if (_source.Token.IsCancellationRequested)
                    return false;
                return _logEntryChannel.Writer.TryWrite(newEntry);
            }
            catch (Exception e)
            {
                Trace.TraceError($"AddLogEntry - Exception: {e.Message}");
            }
            return await Task.FromResult(false);
        }

        public void Dispose()
        {
            _logEntryChannel.Writer.Complete();
            Stop();
            _logEntryChannel = null;
        }

        KubernetesLogEntry[] ICentralLogServiceQuery.Query(string simpleQuery, int maxResults, TimeRange timeRange)
        {
            return _cache.Query(simpleQuery, maxResults, timeRange);
        }
    }
}