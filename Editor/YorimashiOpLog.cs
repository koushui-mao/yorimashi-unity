// Yorimashi Op Log — M3-W1-D
// C# 侧改动 tool 操作日志。JSONL 每日文件，落 Application.persistentDataPath。
//
// 跟 gateway/oplog.py 结构对齐（保证服务端 + 客户端两边 log 可 join）：
//   {id, ts, tool, params, dry_run, outcome, session_id?, correlation_id?,
//    result_summary?, error?, backup_path?, confirmed_by?}
//
// Sentinel: "M3-W1-D OPLOG"
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using UnityEngine;

namespace Yorimashi.Modder.Editor
{
    public static class YorimashiOpLog
    {
        public const string SentinelTag = "M3-W1-D OPLOG";

        // Application.persistentDataPath/yorimashi_oplog/YYYY-MM-DD.jsonl
        // Win 上通常在 %LOCALAPPDATA%Low\<Company>\<Product>\yorimashi_oplog\
        private static readonly object _lock = new object();

        private static string OpLogRoot
        {
            get
            {
                var root = Path.Combine(Application.persistentDataPath, "yorimashi_oplog");
                if (!Directory.Exists(root)) Directory.CreateDirectory(root);
                return root;
            }
        }

        public struct Entry
        {
            public string Id;
            public string Ts;
            public string Tool;
            public string ParamsJson;
            public bool DryRun;
            public string Outcome;  // "ok" | "failed" | "cancelled_by_user" | "pending"
            public string SessionId;
            public string CorrelationId;
            public string ResultSummary;
            public string Error;
            public string BackupPath;
            public string ConfirmedBy;
        }

        public static string Append(Entry e)
        {
            if (string.IsNullOrEmpty(e.Id)) e.Id = "op_" + Guid.NewGuid().ToString("N").Substring(0, 12);
            if (string.IsNullOrEmpty(e.Ts)) e.Ts = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

            var sb = new StringBuilder(512);
            sb.Append('{');
            sb.Append("\"id\":").Append(YorimashiEnvelope.EncodeString(e.Id));
            sb.Append(",\"ts\":").Append(YorimashiEnvelope.EncodeString(e.Ts));
            sb.Append(",\"tool\":").Append(YorimashiEnvelope.EncodeString(e.Tool ?? ""));
            sb.Append(",\"params\":").Append(string.IsNullOrEmpty(e.ParamsJson) ? "null" : e.ParamsJson);
            sb.Append(",\"dry_run\":").Append(e.DryRun ? "true" : "false");
            sb.Append(",\"outcome\":").Append(YorimashiEnvelope.EncodeString(e.Outcome ?? "pending"));
            sb.Append(",\"session_id\":").Append(e.SessionId == null ? "null" : YorimashiEnvelope.EncodeString(e.SessionId));
            sb.Append(",\"correlation_id\":").Append(e.CorrelationId == null ? "null" : YorimashiEnvelope.EncodeString(e.CorrelationId));
            sb.Append(",\"result_summary\":").Append(e.ResultSummary == null ? "null" : YorimashiEnvelope.EncodeString(TruncateSummary(e.ResultSummary)));
            sb.Append(",\"error\":").Append(e.Error == null ? "null" : YorimashiEnvelope.EncodeString(e.Error));
            sb.Append(",\"backup_path\":").Append(e.BackupPath == null ? "null" : YorimashiEnvelope.EncodeString(e.BackupPath));
            sb.Append(",\"confirmed_by\":").Append(e.ConfirmedBy == null ? "null" : YorimashiEnvelope.EncodeString(e.ConfirmedBy));
            sb.Append('}').Append('\n');

            lock (_lock)
            {
                var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
                var file = Path.Combine(OpLogRoot, today + ".jsonl");
                File.AppendAllText(file, sb.ToString(), new UTF8Encoding(false));
            }
            return e.Id;
        }

        public static string GetRootPath() => OpLogRoot;

        private static string TruncateSummary(string s)
        {
            const int max = 512;
            if (s == null || s.Length <= max) return s;
            return s.Substring(0, max - 3) + "...";
        }
    }
}
