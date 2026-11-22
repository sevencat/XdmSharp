using System.Text.Json;
using System.Text.Json.Serialization;
using Xdm.Core.Configuration;
using Xdm.Core.Downloads;

namespace Xdm.Core.Storage;

/// <summary>
/// 落盘 JSON 的序列化元数据（source generator 生成）。
/// NativeAOT / 裁剪下 <c>JsonSerializer</c> 的反射序列化会被报 IL2026/IL3050 并在发布包里
/// 静默丢掉类型信息，所以本工程所有落盘类型都必须在这里登记。
/// </summary>
/// <remarks>
/// 选项与 <see cref="AtomicJson"/> 原来的运行期配置等价：Web 默认（camelCase）+ 缩进 +
/// 枚举写字符串（<c>UseStringEnumConverter</c> 取代原来的 <c>JsonStringEnumConverter</c> 实例，
/// 后者自身带 <c>RequiresDynamicCode</c>）。
/// 新增落盘类型时补一行 <c>[JsonSerializable]</c>，否则 AOT 包运行时会抛
/// <c>NotSupportedException: Metadata for type ... was not provided</c>。
/// </remarks>
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    WriteIndented = true,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(DownloadState))]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(Dictionary<string, string>))]
public sealed partial class XdmJsonContext : JsonSerializerContext;
