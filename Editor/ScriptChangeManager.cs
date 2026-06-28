using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;

namespace WasimDevelopment.UnityMcpBridge
{
    [Serializable]
    internal sealed class ScriptChangeProposal
    {
        public string Id = string.Empty;
        public string Path = string.Empty;
        public string Summary = string.Empty;
        public string OriginalSha256 = string.Empty;
        public string ProposedSha256 = string.Empty;
        public string ProposedContent = string.Empty;
        public string CreatedUtc = string.Empty;
        public string Status = "Pending";
        public string ResultMessage = string.Empty;
        public string BackupPath = string.Empty;
    }

    internal static class ScriptChangeManager
    {
        private const int MaximumPending = 20;
        private const int MaximumDiffLines = 220;
        private static readonly object Gate = new object();
        private static List<ScriptChangeProposal> _proposals;

        public static event Action Changed;

        private static string DataRoot => Path.Combine(ProjectSecurity.ProjectRoot, "Library", "WasimUnityMcpBridge");
        private static string PendingPath => Path.Combine(DataRoot, "script-change-proposals.json");
        private static string BackupRoot => Path.Combine(DataRoot, "Backups");

        public static int PendingCount => Snapshot().Count(p => string.Equals(p.Status, "Pending", StringComparison.OrdinalIgnoreCase));

        public static ScriptChangeProposal[] Snapshot()
        {
            lock (Gate)
            {
                EnsureLoaded();
                return _proposals.Select(Clone).ToArray();
            }
        }

        public static JObject CreateProposal(string path, string expectedSha256, string proposedContent, string summary)
        {
            if (!BridgePreferences.EnableScriptChangeProposals)
                throw new InvalidOperationException("Script change proposals are disabled in the Unity bridge window.");

            if (!ProjectSecurity.TryResolveWritableAssetScript(path, out string fullPath, out string error))
                throw new InvalidOperationException(error);

            proposedContent = proposedContent ?? string.Empty;
            int proposedBytes = Encoding.UTF8.GetByteCount(proposedContent);
            if (proposedBytes == 0) throw new ArgumentException("Proposed script content cannot be empty.");
            if (proposedBytes > ProjectSecurity.MaxScriptBytes)
                throw new InvalidOperationException("Proposed script exceeds the " + (ProjectSecurity.MaxScriptBytes / 1024) + " KB limit.");

            string originalContent = File.ReadAllText(fullPath);
            string originalSha = ComputeSha256(originalContent);
            string normalizedExpected = (expectedSha256 ?? string.Empty).Trim().ToLowerInvariant();
            if (normalizedExpected.Length > 0 && !string.Equals(normalizedExpected, originalSha, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The script changed after it was read. Expected SHA-256 " + normalizedExpected + " but current is " + originalSha + ". Read it again before proposing a replacement.");

            string proposedSha = ComputeSha256(proposedContent);
            if (string.Equals(originalSha, proposedSha, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The proposed content is identical to the current script.");

            ScriptChangeProposal proposal = new ScriptChangeProposal
            {
                Id = Guid.NewGuid().ToString("N").Substring(0, 12),
                Path = ProjectSecurity.ToProjectRelative(fullPath),
                Summary = Truncate((summary ?? string.Empty).Trim(), 1000),
                OriginalSha256 = originalSha,
                ProposedSha256 = proposedSha,
                ProposedContent = proposedContent,
                CreatedUtc = DateTime.UtcNow.ToString("O"),
                Status = "Pending"
            };

            lock (Gate)
            {
                EnsureLoaded();
                int existingIndex = _proposals.FindIndex(p => p.Status == "Pending" && string.Equals(p.Path, proposal.Path, StringComparison.OrdinalIgnoreCase));
                if (existingIndex >= 0)
                {
                    _proposals[existingIndex].Status = "Superseded";
                    _proposals[existingIndex].ResultMessage = "Replaced by proposal " + proposal.Id;
                }
                _proposals.Insert(0, proposal);
                TrimHistory();
                Save();
            }
            RaiseChanged();

            return ToJson(proposal, originalContent, true);
        }

        public static JObject CreateTextPatchProposal(string path, string expectedSha256, string oldText, string newText, string summary)
        {
            if (!BridgePreferences.EnableScriptChangeProposals)
                throw new InvalidOperationException("Script change proposals are disabled in the Unity bridge window.");

            if (!ProjectSecurity.TryResolveWritableAssetScript(path, out string fullPath, out string error))
                throw new InvalidOperationException(error);

            oldText = oldText ?? string.Empty;
            newText = newText ?? string.Empty;
            if (oldText.Length == 0)
                throw new ArgumentException("oldText cannot be empty.");
            if (string.Equals(oldText, newText, StringComparison.Ordinal))
                throw new InvalidOperationException("oldText and newText are identical.");

            string originalContent = File.ReadAllText(fullPath);
            string originalSha = ComputeSha256(originalContent);
            string normalizedExpected = (expectedSha256 ?? string.Empty).Trim().ToLowerInvariant();
            if (normalizedExpected.Length > 0 && !string.Equals(normalizedExpected, originalSha, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The script changed after it was read. Expected SHA-256 " + normalizedExpected + " but current is " + originalSha + ". Read it again before proposing a patch.");

            string proposedContent;
            int rawMatches = CountOccurrences(originalContent, oldText);
            if (rawMatches == 1)
            {
                int index = originalContent.IndexOf(oldText, StringComparison.Ordinal);
                proposedContent = originalContent.Substring(0, index) + newText + originalContent.Substring(index + oldText.Length);
            }
            else
            {
                string normalizedOriginal = NormalizeLineEndings(originalContent);
                string normalizedOld = NormalizeLineEndings(oldText);
                string normalizedNew = NormalizeLineEndings(newText);
                int normalizedMatches = CountOccurrences(normalizedOriginal, normalizedOld);
                if (normalizedMatches != 1)
                {
                    int found = rawMatches > 0 ? rawMatches : normalizedMatches;
                    throw new InvalidOperationException("oldText must match exactly one location in the current script, but " + found + " matches were found. Read the relevant lines again and provide a more specific block.");
                }

                int index = normalizedOriginal.IndexOf(normalizedOld, StringComparison.Ordinal);
                string normalizedProposed = normalizedOriginal.Substring(0, index) + normalizedNew + normalizedOriginal.Substring(index + normalizedOld.Length);
                string newline = DetectPreferredNewline(originalContent);
                proposedContent = newline == "\n" ? normalizedProposed : normalizedProposed.Replace("\n", newline);
            }

            JObject proposal = CreateProposal(path, originalSha, proposedContent, summary);
            proposal["proposalMode"] = "textPatch";
            proposal["matchedOccurrences"] = 1;
            return proposal;
        }

        public static JArray GetProposalSummaries(bool includeCompleted)
        {
            ScriptChangeProposal[] proposals = Snapshot();
            return new JArray(proposals
                .Where(p => includeCompleted || p.Status == "Pending")
                .Select(p => ToJson(p, null, false)));
        }

        public static bool Approve(string id, out string message)
        {
            message = string.Empty;
            ScriptChangeProposal proposal;
            lock (Gate)
            {
                EnsureLoaded();
                proposal = _proposals.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
                if (proposal == null) { message = "Proposal not found."; return false; }
                if (proposal.Status != "Pending") { message = "Proposal is already " + proposal.Status + "."; return false; }
            }

            if (!ProjectSecurity.TryResolveWritableAssetScript(proposal.Path, out string fullPath, out string error))
            {
                MarkFailed(proposal.Id, error);
                message = error;
                return false;
            }

            string currentContent = File.ReadAllText(fullPath);
            string currentSha = ComputeSha256(currentContent);
            if (!string.Equals(currentSha, proposal.OriginalSha256, StringComparison.OrdinalIgnoreCase))
            {
                message = "Approval blocked because the current script changed after the proposal was created.";
                MarkFailed(proposal.Id, message);
                return false;
            }

            try
            {
                string timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
                string backupFolder = Path.Combine(BackupRoot, timestamp + "-" + proposal.Id);
                Directory.CreateDirectory(backupFolder);
                string backupPath = Path.Combine(backupFolder, proposal.Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(backupPath) ?? backupFolder);
                File.Copy(fullPath, backupPath, true);

                string tempPath = fullPath + ".wasim-mcp.tmp";
                File.WriteAllText(tempPath, proposal.ProposedContent, new UTF8Encoding(false));
                File.Copy(tempPath, fullPath, true);
                File.Delete(tempPath);

                lock (Gate)
                {
                    EnsureLoaded();
                    ScriptChangeProposal live = _proposals.First(p => p.Id == proposal.Id);
                    live.Status = "Applied";
                    live.BackupPath = ProjectSecurity.ToProjectRelativeOrAbsolute(backupPath);
                    live.ResultMessage = "Applied after Unity approval at " + DateTime.UtcNow.ToString("O");
                    Save();
                }

                AssetDatabase.ImportAsset(proposal.Path, ImportAssetOptions.ForceUpdate);
                CompilationPipeline.RequestScriptCompilation();
                message = "Applied " + proposal.Path + ". Backup: " + ProjectSecurity.ToProjectRelativeOrAbsolute(backupPath);
                RaiseChanged();
                return true;
            }
            catch (Exception ex)
            {
                message = "Failed to apply proposal: " + ex.GetBaseException().Message;
                MarkFailed(proposal.Id, message);
                return false;
            }
        }

        public static bool Reject(string id, out string message)
        {
            lock (Gate)
            {
                EnsureLoaded();
                ScriptChangeProposal proposal = _proposals.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
                if (proposal == null) { message = "Proposal not found."; return false; }
                if (proposal.Status != "Pending") { message = "Proposal is already " + proposal.Status + "."; return false; }
                proposal.Status = "Rejected";
                proposal.ResultMessage = "Rejected in Unity at " + DateTime.UtcNow.ToString("O");
                Save();
                message = "Rejected " + proposal.Path + ".";
            }
            RaiseChanged();
            return true;
        }

        public static string GetDiffPreview(ScriptChangeProposal proposal)
        {
            if (proposal == null) return string.Empty;
            if (!ProjectSecurity.TryResolveWritableAssetScript(proposal.Path, out string fullPath, out _))
                return "Current script is unavailable.";
            return BuildDiffPreview(File.ReadAllText(fullPath), proposal.ProposedContent);
        }

        public static string ComputeFileSha256(string path)
        {
            if (!ProjectSecurity.TryResolveReadableScript(path, out string fullPath, out string error))
                throw new InvalidOperationException(error);
            return ComputeSha256(File.ReadAllText(fullPath));
        }

        private static JObject ToJson(ScriptChangeProposal proposal, string originalContent, bool includeDiff)
        {
            JObject result = new JObject
            {
                ["id"] = proposal.Id,
                ["path"] = proposal.Path,
                ["summary"] = proposal.Summary,
                ["status"] = proposal.Status,
                ["createdUtc"] = proposal.CreatedUtc,
                ["originalSha256"] = proposal.OriginalSha256,
                ["proposedSha256"] = proposal.ProposedSha256,
                ["resultMessage"] = proposal.ResultMessage,
                ["backupPath"] = proposal.BackupPath,
                ["requiresUnityApproval"] = proposal.Status == "Pending"
            };
            if (includeDiff)
            {
                if (originalContent == null && ProjectSecurity.TryResolveWritableAssetScript(proposal.Path, out string fullPath, out _))
                    originalContent = File.ReadAllText(fullPath);
                result["diffPreview"] = BuildDiffPreview(originalContent ?? string.Empty, proposal.ProposedContent);
            }
            return result;
        }

        private static string BuildDiffPreview(string originalContent, string proposedContent)
        {
            string[] oldLines = NormalizeLines(originalContent);
            string[] newLines = NormalizeLines(proposedContent);
            int prefix = 0;
            while (prefix < oldLines.Length && prefix < newLines.Length && oldLines[prefix] == newLines[prefix]) prefix++;

            int oldSuffix = oldLines.Length - 1;
            int newSuffix = newLines.Length - 1;
            while (oldSuffix >= prefix && newSuffix >= prefix && oldLines[oldSuffix] == newLines[newSuffix])
            {
                oldSuffix--;
                newSuffix--;
            }

            int contextStart = Math.Max(0, prefix - 3);
            int contextOldEnd = Math.Min(oldLines.Length - 1, oldSuffix + 3);
            int contextNewEnd = Math.Min(newLines.Length - 1, newSuffix + 3);
            StringBuilder builder = new StringBuilder();
            builder.Append("@@ current lines ").Append(prefix + 1).Append('-').Append(Math.Max(prefix + 1, oldSuffix + 1))
                .Append(" → proposed lines ").Append(prefix + 1).Append('-').Append(Math.Max(prefix + 1, newSuffix + 1)).AppendLine(" @@");

            int written = 0;
            for (int i = contextStart; i < prefix && written < MaximumDiffLines; i++, written++)
                builder.Append("  ").Append(i + 1).Append("  ").AppendLine(oldLines[i]);
            for (int i = prefix; i <= oldSuffix && written < MaximumDiffLines; i++, written++)
                builder.Append("- ").Append(i + 1).Append("  ").AppendLine(oldLines[i]);
            for (int i = prefix; i <= newSuffix && written < MaximumDiffLines; i++, written++)
                builder.Append("+ ").Append(i + 1).Append("  ").AppendLine(newLines[i]);

            int afterCount = Math.Min(3, Math.Min(oldLines.Length - oldSuffix - 1, newLines.Length - newSuffix - 1));
            for (int i = 1; i <= afterCount && written < MaximumDiffLines; i++, written++)
                builder.Append("  ").Append(newSuffix + i + 1).Append("  ").AppendLine(newLines[newSuffix + i]);

            if (written >= MaximumDiffLines) builder.AppendLine("… diff preview truncated …");
            return builder.ToString();
        }

        private static int CountOccurrences(string source, string value)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(value)) return 0;
            int count = 0;
            int index = 0;
            while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += value.Length;
            }
            return count;
        }

        private static string NormalizeLineEndings(string content)
        {
            return (content ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
        }

        private static string DetectPreferredNewline(string content)
        {
            if (!string.IsNullOrEmpty(content) && content.Contains("\r\n")) return "\r\n";
            return "\n";
        }

        private static string[] NormalizeLines(string content) => (content ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        private static string ComputeSha256(string content)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(content ?? string.Empty));
                return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private static void MarkFailed(string id, string reason)
        {
            lock (Gate)
            {
                EnsureLoaded();
                ScriptChangeProposal proposal = _proposals.FirstOrDefault(p => p.Id == id);
                if (proposal != null)
                {
                    proposal.Status = "Failed";
                    proposal.ResultMessage = reason ?? string.Empty;
                    Save();
                }
            }
            RaiseChanged();
        }

        private static void EnsureLoaded()
        {
            if (_proposals != null) return;
            try
            {
                if (File.Exists(PendingPath))
                    _proposals = JsonConvert.DeserializeObject<List<ScriptChangeProposal>>(File.ReadAllText(PendingPath)) ?? new List<ScriptChangeProposal>();
                else
                    _proposals = new List<ScriptChangeProposal>();
            }
            catch
            {
                _proposals = new List<ScriptChangeProposal>();
            }
        }

        private static void Save()
        {
            Directory.CreateDirectory(DataRoot);
            File.WriteAllText(PendingPath, JsonConvert.SerializeObject(_proposals, Formatting.Indented), new UTF8Encoding(false));
        }

        private static void TrimHistory()
        {
            if (_proposals.Count <= MaximumPending + 30) return;
            List<ScriptChangeProposal> pending = _proposals.Where(p => p.Status == "Pending").Take(MaximumPending).ToList();
            List<ScriptChangeProposal> completed = _proposals.Where(p => p.Status != "Pending").Take(30).ToList();
            _proposals = pending.Concat(completed).OrderByDescending(p => p.CreatedUtc).ToList();
        }

        private static ScriptChangeProposal Clone(ScriptChangeProposal source)
        {
            return new ScriptChangeProposal
            {
                Id = source.Id,
                Path = source.Path,
                Summary = source.Summary,
                OriginalSha256 = source.OriginalSha256,
                ProposedSha256 = source.ProposedSha256,
                ProposedContent = source.ProposedContent,
                CreatedUtc = source.CreatedUtc,
                Status = source.Status,
                ResultMessage = source.ResultMessage,
                BackupPath = source.BackupPath
            };
        }

        private static string Truncate(string value, int maximum) => string.IsNullOrEmpty(value) || value.Length <= maximum ? value ?? string.Empty : value.Substring(0, maximum) + "…";

        private static void RaiseChanged()
        {
            MainThreadDispatcher.Post(() =>
            {
                try { Changed?.Invoke(); } catch { }
            });
        }
    }
}
