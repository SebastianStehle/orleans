using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime.Configuration;

namespace Orleans.Runtime
{
    internal class ThreadTrackingStatistic
    {
        public ITimeInterval ExecutingCpuCycleTime;
        public ITimeInterval ExecutingWallClockTime;
        public ITimeInterval ProcessingCpuCycleTime;
        public ITimeInterval ProcessingWallClockTime;
        public ulong NumRequests;
        public string Name;

        private const string CONTEXT_SWTICH_COUNTER_NAME = "Context Switches/sec";
        public static bool ClientConnected = false;
        private bool firstStart;

        private static readonly List<FloatValueStatistic> allExecutingCpuCycleTime = new List<FloatValueStatistic>();
        private static readonly List<FloatValueStatistic> allExecutingWallClockTime = new List<FloatValueStatistic>();
        private static readonly List<FloatValueStatistic> allProcessingCpuCycleTime = new List<FloatValueStatistic>();
        private static readonly List<FloatValueStatistic> allProcessingWallClockTime = new List<FloatValueStatistic>();
        private static readonly List<FloatValueStatistic> allNumProcessedRequests = new List<FloatValueStatistic>();
        private static FloatValueStatistic totalExecutingCpuCycleTime;
        private static FloatValueStatistic totalExecutingWallClockTime;
        private static FloatValueStatistic totalProcessingCpuCycleTime;
        private static FloatValueStatistic totalProcessingWallClockTime;
        private static FloatValueStatistic totalNumProcessedRequests;
        private readonly StatisticsLevel statisticsLevel;

        /// <summary>
        /// Keep track of thread statistics, mainly timing, can be created outside the thread to be tracked.
        /// </summary>
        /// <param name="threadName">Name used for logging the collected statistics</param>
        /// <param name="loggerFactory">LoggerFactory used to create loggers</param>
        /// <param name="statisticsOptions"></param>
        /// <param name="schedulerStageStatistics"></param>
        public ThreadTrackingStatistic(
            string threadName,
            ILoggerFactory loggerFactory,
            IOptions<StatisticsOptions> statisticsOptions,
            StageAnalysisStatisticsGroup schedulerStageStatistics)
        {
            ExecutingCpuCycleTime = new TimeIntervalThreadCycleCounterBased(loggerFactory);
            ExecutingWallClockTime = TimeIntervalFactory.CreateTimeInterval(true);
            ProcessingCpuCycleTime = new TimeIntervalThreadCycleCounterBased(loggerFactory);
            ProcessingWallClockTime = TimeIntervalFactory.CreateTimeInterval(true);

            NumRequests = 0;
            firstStart = true;
            Name = threadName;

            statisticsLevel = statisticsOptions.Value.CollectionLevel;

            CounterStorage storage = statisticsLevel.ReportDetailedThreadTimeTrackingStats() ? CounterStorage.LogOnly : CounterStorage.DontStore;
            CounterStorage aggrCountersStorage = CounterStorage.LogOnly;

            // 4 direct counters
            allExecutingCpuCycleTime.Add(
                FloatValueStatistic.FindOrCreate(new StatisticName(StatisticNames.THREADS_EXECUTION_TIME_TOTAL_CPU_CYCLES, threadName),
                    () => (float)ExecutingCpuCycleTime.Elapsed.TotalMilliseconds, storage));

            allExecutingWallClockTime.Add(
                FloatValueStatistic.FindOrCreate(new StatisticName(StatisticNames.THREADS_EXECUTION_TIME_TOTAL_WALL_CLOCK, threadName),
                        () => (float)ExecutingWallClockTime.Elapsed.TotalMilliseconds, storage));

            allProcessingCpuCycleTime.Add(
                FloatValueStatistic.FindOrCreate(new StatisticName(StatisticNames.THREADS_PROCESSING_TIME_TOTAL_CPU_CYCLES, threadName),
                        () => (float)ProcessingCpuCycleTime.Elapsed.TotalMilliseconds, storage));

            allProcessingWallClockTime.Add(
                FloatValueStatistic.FindOrCreate(new StatisticName(StatisticNames.THREADS_PROCESSING_TIME_TOTAL_WALL_CLOCK, threadName),
                        () => (float)ProcessingWallClockTime.Elapsed.TotalMilliseconds, storage));

            // numRequests
            allNumProcessedRequests.Add(
                FloatValueStatistic.FindOrCreate(new StatisticName(StatisticNames.THREADS_PROCESSED_REQUESTS_PER_THREAD, threadName),
                        () => (float)NumRequests, storage));

            // aggregate stats
            if (totalExecutingCpuCycleTime == null)
            {
                totalExecutingCpuCycleTime = FloatValueStatistic.FindOrCreate(
                    new StatisticName(StatisticNames.THREADS_EXECUTION_TIME_AVERAGE_CPU_CYCLES, "AllThreads"),
                    () => CalculateTotalAverage(allExecutingCpuCycleTime, totalNumProcessedRequests), aggrCountersStorage);
            }
            if (totalExecutingWallClockTime == null)
            {
                totalExecutingWallClockTime = FloatValueStatistic.FindOrCreate(
                    new StatisticName(StatisticNames.THREADS_EXECUTION_TIME_AVERAGE_WALL_CLOCK, "AllThreads"),
                    () => CalculateTotalAverage(allExecutingWallClockTime, totalNumProcessedRequests), aggrCountersStorage);
            }
            if (totalProcessingCpuCycleTime == null)
            {
                totalProcessingCpuCycleTime = FloatValueStatistic.FindOrCreate(
                    new StatisticName(StatisticNames.THREADS_PROCESSING_TIME_AVERAGE_CPU_CYCLES, "AllThreads"),
                    () => CalculateTotalAverage(allProcessingCpuCycleTime, totalNumProcessedRequests), aggrCountersStorage);
            }
            if (totalProcessingWallClockTime == null)
            {
                totalProcessingWallClockTime = FloatValueStatistic.FindOrCreate(
                    new StatisticName(StatisticNames.THREADS_PROCESSING_TIME_AVERAGE_WALL_CLOCK, "AllThreads"),
                    () => CalculateTotalAverage(allProcessingWallClockTime, totalNumProcessedRequests), aggrCountersStorage);
            }
            if (totalNumProcessedRequests == null)
            {
                totalNumProcessedRequests = FloatValueStatistic.FindOrCreate(
                    new StatisticName(StatisticNames.THREADS_PROCESSED_REQUESTS_PER_THREAD, "AllThreads"),
                        () => (float)allNumProcessedRequests.Select(cs => cs.GetCurrentValue()).Sum(), aggrCountersStorage);
            }

            if (schedulerStageStatistics.PerformStageAnalysis)
                schedulerStageStatistics.AddTracking(this);
        }

        private float CalculateTotalAverage(List<FloatValueStatistic> allthreasCounters, FloatValueStatistic allNumRequestsCounter)
        {
            float allThreads = allthreasCounters.Select(cs => cs.GetCurrentValue()).Sum();
            float numRequests = allNumRequestsCounter.GetCurrentValue();
            if (numRequests > 0) return allThreads / numRequests;
            return 0;
        }

        public static void FirstClientConnectedStartTracking()
        {
            ClientConnected = true;
        }

        /// <summary>
        /// Call once when the thread is started, must be called from the thread being tracked
        /// </summary>
        public void OnStartExecution()
        {
            // Only once a client has connected do we start tracking statistics
            if (ClientConnected)
            {
                ExecutingCpuCycleTime.Start();
                ExecutingWallClockTime.Start();
            }
        }

        /// <summary>
        /// Call once when the thread is stopped, must be called from the thread being tracked
        /// </summary>
        public void OnStopExecution()
        {
            // Only once a client has connected do we start tracking statistics
            if (ClientConnected)
            {
                ExecutingCpuCycleTime.Stop();
                ExecutingWallClockTime.Stop();
            }
        }

        /// <summary>
        /// Call once before processing a request, must be called from the thread being tracked
        /// </summary>
        public void OnStartProcessing()
        {
            // Only once a client has connected do we start tracking statistics
            if (!ClientConnected) return;

            // As this function is called constantly, we perform two additional tasks in this function which require calls from the thread being tracked (the constructor is not called from the tracked thread)
            if (firstStart)
            {
                // If this is the first function call where client has connected, we ensure execution timers are started and context switches are tracked
                firstStart = false;
                OnStartExecution();
            }
            else
            {
                // Must toggle this counter as its "Elapsed" value contains the value when it was last stopped, this is a limitation of our techniques for CPU tracking of threads
                ExecutingCpuCycleTime.Stop();
                ExecutingCpuCycleTime.Start();
            }

            ProcessingCpuCycleTime.Start();
            ProcessingWallClockTime.Start();
        }

        /// <summary>
        /// Call once after processing multiple requests as a batch or a single request, must be called from the thread being tracked
        /// </summary>
        public void OnStopProcessing()
        {
            // Only once a client has connected do we start tracking statistics
            if (ClientConnected)
            {
                ProcessingCpuCycleTime.Stop();
                ProcessingWallClockTime.Stop();
            }
        }

        /// <summary>
        /// Call once to just increment the statistic of processed requests
        /// </summary>
        /// <param name="num">Number of processed requests</param>
        public void IncrementNumberOfProcessed(int num = 1)
        {
            // Only once a client has connected do we start tracking statistics
            if (ClientConnected)
            {
                if (num > 0)
                {
                    NumRequests += (ulong)num;
                }
            }
        }
    }
}