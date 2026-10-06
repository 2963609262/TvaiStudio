using System.Diagnostics;

namespace VideoEnhancer.Router;

/// <summary>
/// 子进程字节级转发：创建 KILL_ON_JOB_CLOSE 作业对象 → 启动目标进程 → stdout/stderr 原样透传
/// （保留 ffmpeg 的 \r 进度刷新）→ 等待退出并返回其退出码。stdin 不重定向（继承调用方的句柄）。
/// 被路由器与编排器（VideoEnhancer.Tvai，通过 Compile Link 引用本文件）共用，避免两份实现漂移。
/// </summary>
internal static class ChildProcessRelay
{
    /// <summary>启动并转发；目标不存在或启动失败时 returnCode = -1 并给出 errorMessage。</summary>
    public static int Run(
        string exePath,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string> environmentVariables,
        out string errorMessage)
    {
        errorMessage = "";
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            errorMessage = $"目标不存在：{exePath}";
            return -1;
        }

        var startInfo = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // stdin 不重定向：继承调用方的重定向句柄（3FUI 只重定向、从不写入）。
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        foreach (var pair in environmentVariables)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        var job = JobObject.CreateKillOnClose();
        // 安全不变量：目标由调用方给定为配置内的固定路径（已 File.Exists 校验），
        // 参数经 ArgumentList 逐项传递且 UseShellExecute=false —— 无 shell 参与，不存在命令拼接。
        try
        {
            using var child = Process.Start(startInfo);
            if (child is null)
            {
                errorMessage = $"无法启动：{exePath}";
                return -1;
            }

            JobObject.Assign(job, child);

            using var standardOutput = Console.OpenStandardOutput();
            using var standardError = Console.OpenStandardError();
            var copyOutput = child.StandardOutput.BaseStream.CopyToAsync(standardOutput);
            var copyError = child.StandardError.BaseStream.CopyToAsync(standardError);

            child.WaitForExit();
            Task.WaitAll(copyOutput, copyError);
            standardOutput.Flush();
            standardError.Flush();
            return child.ExitCode;
        }
        catch (Exception ex)
        {
            errorMessage = $"转发失败：{exePath}{Environment.NewLine}{ex.Message}";
            return -1;
        }
        finally
        {
            JobObject.Close(job);
        }
    }
}
