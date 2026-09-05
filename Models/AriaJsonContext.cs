using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AriaUI.Models;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(List<AriaTaskInfo>))]
[JsonSerializable(typeof(AriaTaskInfo))]
[JsonSerializable(typeof(AriaGlobalStat))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(string[]))]
public partial class AriaJsonContext : JsonSerializerContext
{
}
