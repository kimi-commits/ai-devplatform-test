namespace AI.Tools.Runtime;

/// <summary>
/// 把 MCP/Native 的實體 Tool 名稱(例如 "git.push")對應到規格書 v3 第 6 節的抽象 Capability 名稱
/// (例如 "Git.Push"),讓 <see cref="ToolRuntime"/> 可以在真正執行某個 Tool 之前,用
/// <c>ICapabilityGuard</c> 查詢風險等級並在 High 風險時要求人工核准。
///
/// 只列出真正有風險、值得管制的操作;沒列在這裡的 Tool(例如 file.readFile、search.*、
/// git.status、git.diff、browser.open)視為沒有對應 Capability,一律自動執行——這些本質上是
/// 唯讀 / 檢視性質的操作,規格書也沒有把它們列進 CapabilityRisk。
/// </summary>
public static class ToolCapabilityMap
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["file.writeFile"] = "File.Write",
        ["file.copy"] = "File.Write",
        ["file.move"] = "File.Write",
        ["file.deleteFile"] = "File.Delete",

        ["git.commit"] = "Git.Commit",
        ["git.checkout"] = "Git.Commit", // 會改動工作目錄狀態,可能遺失未提交的變更,歸類為與 commit 同等級的 Medium。
        ["git.push"] = "Git.Push", // GitAgent 真的會呼叫(見 GitAgent.cs),Guard 在核准前完全不會執行 git push。

        ["build.run"] = "Build.Execute",
        ["unity.build"] = "Build.Execute", // Phase 5 起 BuildAgent 真的會呼叫(見 BuildAgent.cs 與
                                             // NativeUnityToolHandlers.cs),風險等級跟 dotnet build 一致(Low)。
        ["test.run"] = "Test.Run",

        ["terminal.run"] = "Terminal.Execute", // 任意 shell 指令,風險等同 Docker/Deploy,務必列管。

        ["deploy.execute"] = "Deploy.Execute" // DeployAgent 真的會呼叫(見 DeployAgent.cs 與
                                                // NativeDeployToolHandlers.cs),Guard 在核准前不會執行部署指令。
    };

    /// <summary>取得 Tool 名稱對應的 Capability 名稱;若沒有對應(視為唯讀/無風險操作),回傳 null。</summary>
    public static string? GetCapabilityName(string toolName) => Map.TryGetValue(toolName, out var capability) ? capability : null;
}
