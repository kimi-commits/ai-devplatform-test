using AI.Core.Tools;

namespace AI.Tools.Adapters;

/// <summary>呼叫外部服務,例如公司內部 API(規格書 v3 第 11 節)。</summary>
public sealed class RestToolAdapter : IToolAdapter
{
    private readonly HttpClient _httpClient;
    private readonly Dictionary<string, string> _toolNameToEndpoint;

    public RestToolAdapter(HttpClient httpClient, Dictionary<string, string> toolNameToEndpoint)
    {
        _httpClient = httpClient;
        _toolNameToEndpoint = toolNameToEndpoint;
    }

    public ToolAdapterKind Kind => ToolAdapterKind.Rest;

    public bool CanHandle(string toolName) => _toolNameToEndpoint.ContainsKey(toolName);

    public async Task<ToolResult> InvokeAsync(ToolRequest request, CancellationToken cancellationToken = default)
    {
        // TODO: 依 request.Parameters 組成實際 HTTP 呼叫。這裡先留最小可編譯骨架。
        await Task.CompletedTask;
        return new ToolResult(false, Error: "RestToolAdapter not yet implemented");
    }
}
