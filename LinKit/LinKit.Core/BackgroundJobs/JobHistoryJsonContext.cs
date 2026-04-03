using System.Text.Json.Serialization;

namespace LinKit.Core.BackgroundJobs;

[JsonSerializable(typeof(JobExecutionHistory))]
internal partial class JobHistoryJsonContext : JsonSerializerContext { }
