using System;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace XLab.UnityMcp.Editor
{
    public static class McpPrototypeMenu
    {
        [MenuItem("XLab/MCP/Start Prototype Server")]
        public static void StartPrototypeServer()
        {
            var serverPath = ResolveServerPath();
            if (serverPath == null)
            {
                Debug.LogWarning($"MCP server binary not found. Checked: {string.Join("; ", ServerPathCandidates())}");
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
                Debug.LogError("Failed to start MCP server process.");
                return;
            }

            Debug.Log($"MCP server started (pid={proc.Id})");
        }

        private static string? ResolveServerPath()
        {
            foreach (var candidate in ServerPathCandidates())
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        private static string[] ServerPathCandidates()
        {
            return new[]
            {
                Environment.GetEnvironmentVariable("XLAB_MCP_SERVER_PATH"),
                Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Library", "XLabMcpServerRuntime", "XLab.UnityMcp.Server.exe")),
            };
        }
    }
}
