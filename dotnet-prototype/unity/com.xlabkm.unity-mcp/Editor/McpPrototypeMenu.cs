using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace XLab.UnityMcp.Editor
{
public static class McpPrototypeMenu
{
    [MenuItem("XLab/MCP/Start Prototype Server")]
    public static void StartPrototypeServer()
    {
        var serverPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "dotnet-prototype", "src", "XLab.UnityMcp.Server", "bin", "Debug", "net8.0", "XLab.UnityMcp.Server.exe"));
        if (!File.Exists(serverPath))
        {
            UnityEngine.Debug.LogWarning($"MCP server binary not found: {serverPath}");
            return;
        }

        var psi = new ProcessStartInfo
        {
            FileName = serverPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        var proc = Process.Start(psi);
        if (proc == null)
        {
            UnityEngine.Debug.LogError("Failed to start MCP server process.");
            return;
        }

        UnityEngine.Debug.Log($"MCP server started (pid={proc.Id})");
    }
}
}
