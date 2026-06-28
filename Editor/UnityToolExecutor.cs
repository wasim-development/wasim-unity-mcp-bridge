using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace WasimDevelopment.UnityMcpBridge
{
    internal static class UnityToolExecutor
    {
        private static readonly PropertyInfo ObjectReferenceInstanceIdProperty = typeof(SerializedProperty).GetProperty(
            "objectReferenceInstanceIDValue",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly MethodInfo GetNearestPrefabInstanceRootMethod = typeof(PrefabUtility).GetMethod(
            "GetNearestPrefabInstanceRoot",
            BindingFlags.Static | BindingFlags.Public,
            null,
            new[] { typeof(UnityEngine.Object) },
            null);

        public static JToken Execute(string toolName, JObject arguments)
        {
            arguments = arguments ?? new JObject();
            switch (toolName)
            {
                case "unity_get_status": return GetStatus();
                case "unity_get_project_info": return GetProjectInfo();
                case "unity_get_compilation_status": return CompilationMonitor.GetStatus();
                case "unity_read_console": return ReadConsole(arguments);
                case "unity_read_editor_log": return ReadEditorLog(arguments);
                case "unity_search_scripts": return SearchScripts(arguments);
                case "unity_find_script_references": return FindScriptReferences(arguments);
                case "unity_read_script": return ReadScript(arguments);
                case "unity_get_selected_script": return GetSelectedScript();
                case "unity_get_active_scene": return GetActiveScene(arguments);
                case "unity_inspect_selected_object": return InspectSelectedObject(arguments);
                case "unity_inspect_selected_hierarchy": return InspectSelectedHierarchy(arguments);
                case "unity_inspect_selected_prefab": return InspectSelectedPrefab(arguments);
                case "unity_get_selected_asset_info": return GetSelectedAssetInfo(arguments);
                case "unity_analyze_selected_folder": return FolderAnalysisService.Analyze(arguments);
                case "unity_inspect_animator_controller":
                case "unity_get_animator_info": return GetAnimatorInfo(arguments);
                case "unity_get_packages": return GetPackages();
                case "unity_get_script_change_proposals": return ScriptChangeManager.GetProposalSummaries(ReadBool(arguments, "includeCompleted", false));
                case "unity_propose_script_text_patch":
                    return ScriptChangeManager.CreateTextPatchProposal(
                        ReadString(arguments, "path", string.Empty),
                        ReadString(arguments, "expectedSha256", string.Empty),
                        ReadString(arguments, "oldText", string.Empty),
                        ReadString(arguments, "newText", string.Empty),
                        ReadString(arguments, "summary", string.Empty));
                case "unity_propose_script_replacement":
                    return ScriptChangeManager.CreateProposal(
                        ReadString(arguments, "path", string.Empty),
                        ReadString(arguments, "expectedSha256", string.Empty),
                        ReadString(arguments, "newContent", string.Empty),
                        ReadString(arguments, "summary", string.Empty));
                default: throw new ArgumentException("Unknown Unity MCP tool: " + toolName);
            }
        }

        private static JObject GetStatus()
        {
            Scene scene = SceneManager.GetActiveScene();
            JArray advertisedTools = McpToolCatalog.Build();
            var advertisedToolNames = new JArray();
            foreach (JToken tool in advertisedTools)
                advertisedToolNames.Add(tool["name"] == null ? string.Empty : tool["name"].Value<string>());
            return new JObject
            {
                ["bridgeVersion"] = BridgeVersion.PackageVersion,
                ["advertisedToolCount"] = advertisedTools.Count,
                ["advertisedTools"] = advertisedToolNames,
                ["connected"] = CompanionManager.IsRunning && CompanionIpc.IsHeartbeatFresh(),
                ["readOnly"] = true,
                ["unityVersion"] = Application.unityVersion,
                ["projectName"] = Application.productName,
                ["activeScene"] = scene.IsValid() ? scene.name : string.Empty,
                ["isPlaying"] = EditorApplication.isPlaying,
                ["isPaused"] = EditorApplication.isPaused,
                ["isCompiling"] = EditorApplication.isCompiling,
                ["isUpdating"] = EditorApplication.isUpdating,
                ["selectedObject"] = Selection.activeObject == null ? null : Selection.activeObject.name,
                ["companionState"] = CompanionManager.Status.State,
                ["companionVersion"] = CompanionManager.Status.CompanionVersion,
                ["companionProcessId"] = CompanionManager.Status.ProcessId,
                ["unityIpcConnected"] = CompanionIpc.IsHeartbeatFresh(),
                ["ngrokState"] = CompanionManager.Status.NgrokState,
                ["publicUrlAvailable"] = !string.IsNullOrWhiteSpace(CompanionManager.Status.PublicUrl),
                ["scriptChangeProposalsEnabled"] = BridgePreferences.EnableScriptChangeProposals,
                ["pendingScriptChangeCount"] = ScriptChangeManager.PendingCount,
                ["lastRequestUtc"] = BridgeRequestHistory.LastRequestUtc?.ToString("O") ?? string.Empty,
                ["serverUtc"] = DateTime.UtcNow.ToString("O")
            };
        }

        private static JObject GetProjectInfo()
        {
            Scene scene = SceneManager.GetActiveScene();
            return new JObject
            {
                ["projectName"] = Application.productName,
                ["projectRoot"] = ProjectSecurity.ProjectRoot.Replace('\\', '/'),
                ["assetsPath"] = Application.dataPath.Replace('\\', '/'),
                ["unityVersion"] = Application.unityVersion,
                ["platform"] = Application.platform.ToString(),
                ["activeBuildTarget"] = EditorUserBuildSettings.activeBuildTarget.ToString(),
                ["activeScene"] = new JObject
                {
                    ["name"] = scene.IsValid() ? scene.name : string.Empty,
                    ["path"] = scene.IsValid() ? scene.path : string.Empty,
                    ["isLoaded"] = scene.IsValid() && scene.isLoaded,
                    ["isDirty"] = scene.IsValid() && scene.isDirty,
                    ["rootCount"] = scene.IsValid() && scene.isLoaded ? scene.rootCount : 0
                }
            };
        }

        private static JArray ReadConsole(JObject args)
        {
            int maximum = ReadInt(args, "maximumEntries", 50, 1, 200);
            bool logs = ReadBool(args, "includeLogs", false);
            bool warnings = ReadBool(args, "includeWarnings", true);
            bool errors = ReadBool(args, "includeErrors", true);
            return UnityConsoleReader.Read(maximum, logs, warnings, errors);
        }

        private static JObject ReadEditorLog(JObject args)
        {
            int tailLines = ReadInt(args, "tailLines", 200, 10, 1000);
            string path = Application.consoleLogPath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new FileNotFoundException("Unity Editor log was not found.", path);

            const int maximumBytes = 2 * 1024 * 1024;
            byte[] bytes;
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                long readable = Math.Min(stream.Length, maximumBytes);
                stream.Seek(-readable, SeekOrigin.End);
                bytes = new byte[(int)readable];
                int offset = 0;
                while (offset < bytes.Length)
                {
                    int read = stream.Read(bytes, offset, bytes.Length - offset);
                    if (read <= 0) break;
                    offset += read;
                }
            }

            string text = Encoding.UTF8.GetString(bytes);
            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            int start = Math.Max(0, lines.Length - tailLines);
            return new JObject
            {
                ["path"] = path.Replace('\\', '/'),
                ["tailLines"] = new JArray(lines.Skip(start)),
                ["truncatedToLastBytes"] = bytes.Length == maximumBytes ? maximumBytes : 0
            };
        }

        private static JObject SearchScripts(JObject args)
        {
            string query = ReadString(args, "query", string.Empty).Trim();
            if (query.Length == 0) throw new ArgumentException("Search query cannot be empty.");
            int maximum = ReadInt(args, "maximumResults", 30, 1, 100);

            var roots = new List<string> { Path.Combine(ProjectSecurity.ProjectRoot, "Assets") };
            if (BridgePreferences.AllowPackageScripts) roots.Add(Path.Combine(ProjectSecurity.ProjectRoot, "Packages"));

            var matches = new JArray();
            int scannedFiles = 0;
            bool stopped = false;
            foreach (string root in roots)
            {
                if (!Directory.Exists(root)) continue;
                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories); }
                catch { continue; }

                foreach (string file in files)
                {
                    if (matches.Count >= maximum) { stopped = true; break; }
                    scannedFiles++;
                    try
                    {
                        FileInfo info = new FileInfo(file);
                        if (info.Length > ProjectSecurity.MaxScriptBytes) continue;
                        int lineNumber = 0;
                        foreach (string line in File.ReadLines(file))
                        {
                            lineNumber++;
                            if (line.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
                            matches.Add(new JObject
                            {
                                ["path"] = ProjectSecurity.ToProjectRelative(file),
                                ["line"] = lineNumber,
                                ["text"] = Truncate(line.Trim(), 500)
                            });
                            if (matches.Count >= maximum) break;
                        }
                    }
                    catch
                    {
                        // Skip unreadable or transient files.
                    }
                }
                if (stopped) break;
            }

            return new JObject
            {
                ["query"] = query,
                ["matches"] = matches,
                ["scannedFiles"] = scannedFiles,
                ["resultLimitReached"] = matches.Count >= maximum,
                ["packageScriptsEnabled"] = BridgePreferences.AllowPackageScripts
            };
        }

        private static JObject ReadScript(JObject args)
        {
            string path = ReadString(args, "path", string.Empty);
            int startLine = ReadInt(args, "startLine", 1, 1, 1000000);
            int requestedEnd = ReadInt(args, "endLine", 0, 0, 1000000);
            if (!ProjectSecurity.TryResolveReadableScript(path, out string fullPath, out string error))
                throw new InvalidOperationException(error);

            string[] lines = File.ReadAllLines(fullPath);
            if (lines.Length == 0)
            {
                return new JObject
                {
                    ["path"] = ProjectSecurity.ToProjectRelative(fullPath),
                    ["lineCount"] = 0,
                    ["sha256"] = ScriptChangeManager.ComputeFileSha256(ProjectSecurity.ToProjectRelative(fullPath)),
                    ["content"] = string.Empty
                };
            }

            startLine = Math.Min(startLine, lines.Length);
            int endLine = requestedEnd <= 0 ? Math.Min(lines.Length, startLine + 399) : Math.Min(lines.Length, requestedEnd);
            if (endLine < startLine) throw new ArgumentException("endLine must not be before startLine.");

            var numbered = new StringBuilder();
            for (int i = startLine; i <= endLine; i++)
                numbered.Append(i).Append(": ").AppendLine(lines[i - 1]);

            return new JObject
            {
                ["path"] = ProjectSecurity.ToProjectRelative(fullPath),
                ["lineCount"] = lines.Length,
                ["startLine"] = startLine,
                ["endLine"] = endLine,
                ["truncated"] = endLine < lines.Length,
                ["sha256"] = ScriptChangeManager.ComputeFileSha256(ProjectSecurity.ToProjectRelative(fullPath)),
                ["content"] = numbered.ToString()
            };
        }

        private static JObject GetSelectedScript()
        {
            MonoScript script = Selection.activeObject as MonoScript;
            if (script == null) throw new InvalidOperationException("No C# script is selected in the Unity Project window.");
            string path = AssetDatabase.GetAssetPath(script);
            return ReadScript(new JObject { ["path"] = path, ["startLine"] = 1, ["endLine"] = 0 });
        }

        private static JObject GetActiveScene(JObject args)
        {
            int maximumObjects = ReadInt(args, "maximumObjects", 300, 1, 1000);
            int maximumDepth = ReadInt(args, "maximumDepth", 8, 0, 20);
            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded) throw new InvalidOperationException("There is no loaded active scene.");

            int count = 0;
            bool truncated = false;
            JArray roots = new JArray();
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                if (count >= maximumObjects) { truncated = true; break; }
                roots.Add(BuildHierarchyNode(root.transform, 0, maximumDepth, maximumObjects, ref count, ref truncated));
            }

            return new JObject
            {
                ["name"] = scene.name,
                ["path"] = scene.path,
                ["isDirty"] = scene.isDirty,
                ["rootCount"] = scene.rootCount,
                ["returnedObjectCount"] = count,
                ["truncated"] = truncated,
                ["hierarchy"] = roots
            };
        }

        private static JObject BuildHierarchyNode(Transform transform, int depth, int maximumDepth, int maximumObjects, ref int count, ref bool truncated)
        {
            count++;
            GameObject gameObject = transform.gameObject;
            var node = new JObject
            {
                ["name"] = gameObject.name,
                ["instanceId"] = gameObject.GetInstanceID(),
                ["activeSelf"] = gameObject.activeSelf,
                ["activeInHierarchy"] = gameObject.activeInHierarchy,
                ["tag"] = gameObject.tag,
                ["layer"] = LayerMask.LayerToName(gameObject.layer),
                ["components"] = new JArray(gameObject.GetComponents<Component>().Select(c => c == null ? "Missing Script" : c.GetType().FullName))
            };

            JArray children = new JArray();
            if (depth < maximumDepth)
            {
                for (int i = 0; i < transform.childCount; i++)
                {
                    if (count >= maximumObjects) { truncated = true; break; }
                    children.Add(BuildHierarchyNode(transform.GetChild(i), depth + 1, maximumDepth, maximumObjects, ref count, ref truncated));
                }
            }
            else if (transform.childCount > 0)
            {
                truncated = true;
            }
            node["children"] = children;
            return node;
        }

        private static JObject InspectSelectedObject(JObject args)
        {
            int maximumProperties = ReadInt(args, "maximumPropertiesPerComponent", 50, 1, 200);
            GameObject selected = Selection.activeGameObject;
            if (selected == null) throw new InvalidOperationException("No GameObject is selected in the Unity Hierarchy.");

            var components = new JArray();
            foreach (Component component in selected.GetComponents<Component>())
            {
                if (component == null)
                {
                    components.Add(new JObject { ["type"] = "Missing Script", ["missing"] = true });
                    continue;
                }

                var properties = new JArray();
                try
                {
                    var serializedObject = new SerializedObject(component);
                    SerializedProperty iterator = serializedObject.GetIterator();
                    bool enterChildren = true;
                    while (properties.Count < maximumProperties && iterator.NextVisible(enterChildren))
                    {
                        enterChildren = false;
                        properties.Add(new JObject
                        {
                            ["path"] = iterator.propertyPath,
                            ["displayName"] = iterator.displayName,
                            ["type"] = iterator.propertyType.ToString(),
                            ["value"] = SerializedValue(iterator)
                        });
                    }
                }
                catch (Exception ex)
                {
                    properties.Add(new JObject { ["error"] = ex.Message });
                }

                components.Add(new JObject
                {
                    ["type"] = component.GetType().FullName,
                    ["enabled"] = GetEnabledState(component),
                    ["properties"] = properties,
                    ["propertiesTruncated"] = properties.Count >= maximumProperties
                });
            }

            string prefabAssetPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(selected);
            return new JObject
            {
                ["name"] = selected.name,
                ["hierarchyPath"] = GetHierarchyPath(selected.transform),
                ["instanceId"] = selected.GetInstanceID(),
                ["activeSelf"] = selected.activeSelf,
                ["activeInHierarchy"] = selected.activeInHierarchy,
                ["tag"] = selected.tag,
                ["layer"] = LayerMask.LayerToName(selected.layer),
                ["isStatic"] = selected.isStatic,
                ["scene"] = selected.scene.IsValid() ? selected.scene.name : string.Empty,
                ["prefabAssetPath"] = prefabAssetPath ?? string.Empty,
                ["prefabInstanceStatus"] = PrefabUtility.GetPrefabInstanceStatus(selected).ToString(),
                ["components"] = components
            };
        }

        private static JObject InspectSelectedPrefab(JObject args)
        {
            int maximumObjects = ReadInt(args, "maximumObjects", 300, 1, 1000);
            int maximumDepth = ReadInt(args, "maximumDepth", 10, 0, 20);
            int maximumProperties = ReadInt(args, "maximumPropertiesPerComponent", 40, 1, 200);
            int maximumOverrides = ReadInt(args, "maximumOverrides", 100, 0, 500);

            GameObject selected = Selection.activeObject as GameObject;
            if (selected == null) selected = Selection.activeGameObject;
            if (selected == null)
                throw new InvalidOperationException("No prefab asset or prefab instance is selected.");

            bool isPrefabAsset = PrefabUtility.IsPartOfPrefabAsset(selected);
            bool isPrefabInstance = PrefabUtility.IsPartOfPrefabInstance(selected);
            if (!isPrefabAsset && !isPrefabInstance)
                throw new InvalidOperationException("The selected GameObject is not a prefab asset or prefab instance.");

            GameObject inspectionRoot = selected;
            if (isPrefabInstance)
            {
                GameObject preferredRoot = GetPreferredPrefabInstanceRoot(selected);
                if (preferredRoot != null) inspectionRoot = preferredRoot;
            }

            string selectedAssetPath = AssetDatabase.GetAssetPath(selected);
            string prefabAssetPath = isPrefabAsset
                ? selectedAssetPath
                : PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(inspectionRoot);

            int objectCount = 0;
            int missingScriptCount = 0;
            int missingReferenceCount = 0;
            bool truncated = false;
            JObject hierarchy = BuildDetailedPrefabNode(
                inspectionRoot.transform,
                0,
                maximumDepth,
                maximumObjects,
                maximumProperties,
                ref objectCount,
                ref missingScriptCount,
                ref missingReferenceCount,
                ref truncated);

            JArray overrides = new JArray();
            bool overridesTruncated = false;
            if (isPrefabInstance && maximumOverrides > 0)
            {
                try
                {
                    PropertyModification[] modifications = PrefabUtility.GetPropertyModifications(inspectionRoot);
                    if (modifications != null)
                    {
                        foreach (PropertyModification modification in modifications)
                        {
                            if (overrides.Count >= maximumOverrides)
                            {
                                overridesTruncated = true;
                                break;
                            }

                            UnityEngine.Object target = modification.target;
                            overrides.Add(new JObject
                            {
                                ["target"] = target == null ? "null" : target.name,
                                ["targetType"] = target == null ? string.Empty : target.GetType().FullName,
                                ["propertyPath"] = modification.propertyPath ?? string.Empty,
                                ["value"] = modification.value ?? string.Empty,
                                ["objectReference"] = modification.objectReference == null
                                    ? "null"
                                    : modification.objectReference.name + " (" + modification.objectReference.GetType().Name + ")"
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    overrides.Add(new JObject { ["error"] = ex.Message });
                }
            }

            return new JObject
            {
                ["selectedName"] = selected.name,
                ["inspectionRootName"] = inspectionRoot.name,
                ["inspectionRootHierarchyPath"] = GetHierarchyPath(inspectionRoot.transform),
                ["selectionType"] = isPrefabAsset ? "PrefabAsset" : "PrefabInstance",
                ["prefabAssetPath"] = prefabAssetPath ?? string.Empty,
                ["prefabAssetType"] = PrefabUtility.GetPrefabAssetType(inspectionRoot).ToString(),
                ["prefabInstanceStatus"] = PrefabUtility.GetPrefabInstanceStatus(inspectionRoot).ToString(),
                ["scene"] = inspectionRoot.scene.IsValid() ? inspectionRoot.scene.name : string.Empty,
                ["returnedObjectCount"] = objectCount,
                ["missingScriptCount"] = missingScriptCount,
                ["missingObjectReferenceCount"] = missingReferenceCount,
                ["truncated"] = truncated,
                ["hierarchy"] = hierarchy,
                ["propertyOverrides"] = overrides,
                ["overridesTruncated"] = overridesTruncated
            };
        }

        private static JObject BuildDetailedPrefabNode(
            Transform transform,
            int depth,
            int maximumDepth,
            int maximumObjects,
            int maximumProperties,
            ref int objectCount,
            ref int missingScriptCount,
            ref int missingReferenceCount,
            ref bool truncated)
        {
            objectCount++;
            GameObject gameObject = transform.gameObject;
            JArray components = ReadDetailedComponents(
                gameObject,
                maximumProperties,
                ref missingScriptCount,
                ref missingReferenceCount);

            var node = new JObject
            {
                ["name"] = gameObject.name,
                ["hierarchyPath"] = GetHierarchyPath(transform),
                ["instanceId"] = gameObject.GetInstanceID(),
                ["activeSelf"] = gameObject.activeSelf,
                ["activeInHierarchy"] = gameObject.activeInHierarchy,
                ["tag"] = gameObject.tag,
                ["layer"] = LayerMask.LayerToName(gameObject.layer),
                ["isStatic"] = gameObject.isStatic,
                ["localPosition"] = transform.localPosition.ToString(),
                ["localRotationEuler"] = transform.localEulerAngles.ToString(),
                ["localScale"] = transform.localScale.ToString(),
                ["components"] = components
            };

            JArray children = new JArray();
            if (depth < maximumDepth)
            {
                for (int i = 0; i < transform.childCount; i++)
                {
                    if (objectCount >= maximumObjects)
                    {
                        truncated = true;
                        break;
                    }

                    children.Add(BuildDetailedPrefabNode(
                        transform.GetChild(i),
                        depth + 1,
                        maximumDepth,
                        maximumObjects,
                        maximumProperties,
                        ref objectCount,
                        ref missingScriptCount,
                        ref missingReferenceCount,
                        ref truncated));
                }
            }
            else if (transform.childCount > 0)
            {
                truncated = true;
            }

            node["children"] = children;
            return node;
        }

        private static JArray ReadDetailedComponents(
            GameObject gameObject,
            int maximumProperties,
            ref int missingScriptCount,
            ref int missingReferenceCount)
        {
            var components = new JArray();
            foreach (Component component in gameObject.GetComponents<Component>())
            {
                if (component == null)
                {
                    missingScriptCount++;
                    components.Add(new JObject { ["type"] = "Missing Script", ["missing"] = true });
                    continue;
                }

                var properties = new JArray();
                int componentMissingReferences = 0;
                try
                {
                    var serializedObject = new SerializedObject(component);
                    SerializedProperty iterator = serializedObject.GetIterator();
                    bool enterChildren = true;
                    while (properties.Count < maximumProperties && iterator.NextVisible(enterChildren))
                    {
                        enterChildren = false;
                        bool missingReference = HasMissingObjectReference(iterator);
                        if (missingReference)
                        {
                            componentMissingReferences++;
                            missingReferenceCount++;
                        }

                        string referenceAssetPath = string.Empty;
                        if (iterator.propertyType == SerializedPropertyType.ObjectReference && iterator.objectReferenceValue != null)
                            referenceAssetPath = AssetDatabase.GetAssetPath(iterator.objectReferenceValue) ?? string.Empty;

                        properties.Add(new JObject
                        {
                            ["path"] = iterator.propertyPath,
                            ["displayName"] = iterator.displayName,
                            ["type"] = iterator.propertyType.ToString(),
                            ["value"] = SerializedValue(iterator),
                            ["missingReference"] = missingReference,
                            ["referenceAssetPath"] = referenceAssetPath
                        });
                    }
                }
                catch (Exception ex)
                {
                    properties.Add(new JObject { ["error"] = ex.Message });
                }

                components.Add(new JObject
                {
                    ["type"] = component.GetType().FullName,
                    ["enabled"] = GetEnabledState(component),
                    ["missingReferenceCount"] = componentMissingReferences,
                    ["properties"] = properties,
                    ["propertiesTruncated"] = properties.Count >= maximumProperties
                });
            }
            return components;
        }


        private static JObject FindScriptReferences(JObject args)
        {
            string symbol = ReadString(args, "symbol", string.Empty).Trim();
            if (symbol.Length == 0) throw new ArgumentException("A symbol is required.");
            bool caseSensitive = ReadBool(args, "caseSensitive", true);
            int maximum = ReadInt(args, "maximumResults", 80, 1, 200);
            StringComparison comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

            var roots = new List<string> { Path.Combine(ProjectSecurity.ProjectRoot, "Assets") };
            if (BridgePreferences.AllowPackageScripts) roots.Add(Path.Combine(ProjectSecurity.ProjectRoot, "Packages"));
            var matches = new JArray();
            int scannedFiles = 0;

            foreach (string root in roots)
            {
                if (!Directory.Exists(root)) continue;
                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories); }
                catch { continue; }

                foreach (string file in files)
                {
                    if (matches.Count >= maximum) break;
                    try
                    {
                        FileInfo info = new FileInfo(file);
                        if (info.Length > ProjectSecurity.MaxScriptBytes) continue;
                        scannedFiles++;
                        int lineNumber = 0;
                        foreach (string line in File.ReadLines(file))
                        {
                            lineNumber++;
                            int index = line.IndexOf(symbol, comparison);
                            if (index < 0) continue;
                            string trimmed = line.Trim();
                            string kind = trimmed.IndexOf("class " + symbol, comparison) >= 0
                                || trimmed.IndexOf("struct " + symbol, comparison) >= 0
                                || trimmed.IndexOf("interface " + symbol, comparison) >= 0
                                || trimmed.IndexOf("enum " + symbol, comparison) >= 0
                                ? "declaration"
                                : "reference";
                            matches.Add(new JObject
                            {
                                ["path"] = ProjectSecurity.ToProjectRelative(file),
                                ["line"] = lineNumber,
                                ["column"] = index + 1,
                                ["kind"] = kind,
                                ["text"] = Truncate(trimmed, 700)
                            });
                            if (matches.Count >= maximum) break;
                        }
                    }
                    catch { }
                }
                if (matches.Count >= maximum) break;
            }

            return new JObject
            {
                ["symbol"] = symbol,
                ["caseSensitive"] = caseSensitive,
                ["matches"] = matches,
                ["scannedFiles"] = scannedFiles,
                ["resultLimitReached"] = matches.Count >= maximum
            };
        }

        private static JObject InspectSelectedHierarchy(JObject args)
        {
            int maximumObjects = ReadInt(args, "maximumObjects", 300, 1, 1000);
            int maximumDepth = ReadInt(args, "maximumDepth", 10, 0, 20);
            int maximumProperties = ReadInt(args, "maximumPropertiesPerComponent", 40, 1, 200);
            GameObject selected = Selection.activeGameObject;
            if (selected == null) throw new InvalidOperationException("No GameObject is selected in the Unity Hierarchy.");

            int objectCount = 0;
            int missingScriptCount = 0;
            int missingReferenceCount = 0;
            bool truncated = false;
            JObject hierarchy = BuildDetailedPrefabNode(
                selected.transform, 0, maximumDepth, maximumObjects, maximumProperties,
                ref objectCount, ref missingScriptCount, ref missingReferenceCount, ref truncated);

            return new JObject
            {
                ["selectedName"] = selected.name,
                ["selectedHierarchyPath"] = GetHierarchyPath(selected.transform),
                ["scene"] = selected.scene.IsValid() ? selected.scene.name : string.Empty,
                ["returnedObjectCount"] = objectCount,
                ["missingScriptCount"] = missingScriptCount,
                ["missingObjectReferenceCount"] = missingReferenceCount,
                ["truncated"] = truncated,
                ["hierarchy"] = hierarchy
            };
        }

        private static JObject GetSelectedAssetInfo(JObject args)
        {
            UnityEngine.Object selected = Selection.activeObject;
            if (selected == null) throw new InvalidOperationException("No asset is selected in the Unity Project window.");
            string path = AssetDatabase.GetAssetPath(selected);
            if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("The selected object is not a project asset.");

            bool recursive = ReadBool(args, "recursiveDependencies", false);
            int maximum = ReadInt(args, "maximumDependencies", 100, 1, 500);
            string[] dependencies = AssetDatabase.GetDependencies(path, recursive)
                .Where(value => !string.Equals(value, path, StringComparison.OrdinalIgnoreCase))
                .Take(maximum + 1)
                .ToArray();
            bool truncated = dependencies.Length > maximum;
            if (truncated) dependencies = dependencies.Take(maximum).ToArray();

            AssetImporter importer = AssetImporter.GetAtPath(path);
            return new JObject
            {
                ["name"] = selected.name,
                ["path"] = path,
                ["guid"] = AssetDatabase.AssetPathToGUID(path),
                ["type"] = selected.GetType().FullName,
                ["isFolder"] = AssetDatabase.IsValidFolder(path),
                ["labels"] = new JArray(AssetDatabase.GetLabels(selected)),
                ["importerType"] = importer == null ? string.Empty : importer.GetType().FullName,
                ["assetBundleName"] = importer == null ? string.Empty : importer.assetBundleName,
                ["dependenciesRecursive"] = recursive,
                ["dependencies"] = new JArray(dependencies),
                ["dependenciesTruncated"] = truncated
            };
        }

        private static JObject GetAnimatorInfo(JObject args)
        {
            bool searchChildren = ReadBool(args, "searchChildren", true);
            int maximumStates = ReadInt(args, "maximumStates", 250, 1, 1000);
            int maximumTransitions = ReadInt(args, "maximumTransitions", 600, 1, 2000);
            int maximumMotions = ReadInt(args, "maximumMotions", 500, 1, 2000);

            UnityEngine.Object selected = Selection.activeObject;
            if (selected == null) throw new InvalidOperationException("Nothing is selected in Unity.");

            Animator animator = null;
            RuntimeAnimatorController runtimeController = null;
            string selectionKind;

            GameObject selectedGameObject = Selection.activeGameObject;
            if (selectedGameObject != null)
            {
                animator = selectedGameObject.GetComponent<Animator>();
                if (animator == null && searchChildren)
                    animator = selectedGameObject.GetComponentInChildren<Animator>(true);
                if (animator == null)
                    throw new InvalidOperationException("No Animator was found on the selected GameObject" + (searchChildren ? " or its children." : "."));
                runtimeController = animator.runtimeAnimatorController;
                selectionKind = "AnimatorComponent";
            }
            else if (selected is RuntimeAnimatorController selectedController)
            {
                runtimeController = selectedController;
                selectionKind = selected.GetType().Name;
            }
            else
            {
                throw new InvalidOperationException("Select a GameObject with an Animator, an Animator Controller asset, or an Animator Override Controller asset.");
            }

            AnimatorOverrideController overrideController = runtimeController as AnimatorOverrideController;
            AnimatorController controller = ResolveAnimatorController(runtimeController);
            if (controller == null)
                throw new InvalidOperationException("The selected Animator does not resolve to an editable AnimatorController graph. Runtime-only controller type: " + (runtimeController == null ? "null" : runtimeController.GetType().FullName));

            var parameters = new JArray();
            foreach (AnimatorControllerParameter parameter in controller.parameters)
            {
                parameters.Add(new JObject
                {
                    ["name"] = parameter.name,
                    ["nameHash"] = parameter.nameHash,
                    ["type"] = parameter.type.ToString(),
                    ["defaultBool"] = parameter.defaultBool,
                    ["defaultFloat"] = parameter.defaultFloat,
                    ["defaultInt"] = parameter.defaultInt
                });
            }

            int stateCount = 0;
            int transitionCount = 0;
            int motionCount = 0;
            bool truncated = false;
            var clipPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var layers = new JArray();

            foreach (AnimatorControllerLayer layer in controller.layers)
            {
                if (stateCount >= maximumStates || transitionCount >= maximumTransitions || motionCount >= maximumMotions)
                {
                    truncated = true;
                    break;
                }

                JObject stateMachine = ReadAnimatorStateMachineGraph(
                    layer.stateMachine,
                    layer.name,
                    maximumStates,
                    maximumTransitions,
                    maximumMotions,
                    ref stateCount,
                    ref transitionCount,
                    ref motionCount,
                    ref truncated,
                    clipPaths);

                layers.Add(new JObject
                {
                    ["name"] = layer.name,
                    ["defaultWeight"] = layer.defaultWeight,
                    ["blendingMode"] = layer.blendingMode.ToString(),
                    ["avatarMask"] = layer.avatarMask == null ? "null" : layer.avatarMask.name,
                    ["avatarMaskAssetPath"] = layer.avatarMask == null ? string.Empty : AssetDatabase.GetAssetPath(layer.avatarMask),
                    ["iKPass"] = layer.iKPass,
                    ["syncedLayerIndex"] = layer.syncedLayerIndex,
                    ["syncedLayerAffectsTiming"] = layer.syncedLayerAffectsTiming,
                    ["stateMachine"] = stateMachine
                });
            }

            var overrides = new JArray();
            if (overrideController != null)
            {
                var mappings = new List<KeyValuePair<AnimationClip, AnimationClip>>();
                overrideController.GetOverrides(mappings);
                foreach (KeyValuePair<AnimationClip, AnimationClip> mapping in mappings)
                {
                    overrides.Add(new JObject
                    {
                        ["originalClip"] = mapping.Key == null ? "null" : mapping.Key.name,
                        ["originalClipAssetPath"] = mapping.Key == null ? string.Empty : AssetDatabase.GetAssetPath(mapping.Key),
                        ["overrideClip"] = mapping.Value == null ? "null" : mapping.Value.name,
                        ["overrideClipAssetPath"] = mapping.Value == null ? string.Empty : AssetDatabase.GetAssetPath(mapping.Value)
                    });
                    AddClipPath(mapping.Key, clipPaths);
                    AddClipPath(mapping.Value, clipPaths);
                }
            }

            var result = new JObject
            {
                ["bridgeVersion"] = BridgeVersion.PackageVersion,
                ["selectionKind"] = selectionKind,
                ["selectedObject"] = selected.name,
                ["runtimeController"] = runtimeController == null ? "null" : runtimeController.name,
                ["runtimeControllerType"] = runtimeController == null ? string.Empty : runtimeController.GetType().FullName,
                ["runtimeControllerAssetPath"] = runtimeController == null ? string.Empty : AssetDatabase.GetAssetPath(runtimeController),
                ["animatorController"] = controller.name,
                ["animatorControllerAssetPath"] = AssetDatabase.GetAssetPath(controller),
                ["overrideControllerAssetPath"] = overrideController == null ? string.Empty : AssetDatabase.GetAssetPath(overrideController),
                ["parameters"] = parameters,
                ["layers"] = layers,
                ["overrideMappings"] = overrides,
                ["animationClipAssetPaths"] = new JArray(clipPaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)),
                ["returnedStateCount"] = stateCount,
                ["returnedTransitionCount"] = transitionCount,
                ["returnedMotionNodeCount"] = motionCount,
                ["truncated"] = truncated
            };

            if (animator != null)
            {
                result["animator"] = new JObject
                {
                    ["gameObject"] = animator.gameObject.name,
                    ["hierarchyPath"] = GetHierarchyPath(animator.transform),
                    ["enabled"] = animator.enabled,
                    ["applyRootMotion"] = animator.applyRootMotion,
                    ["updateMode"] = animator.updateMode.ToString(),
                    ["cullingMode"] = animator.cullingMode.ToString(),
                    ["avatar"] = animator.avatar == null ? "null" : animator.avatar.name,
                    ["avatarAssetPath"] = animator.avatar == null ? string.Empty : AssetDatabase.GetAssetPath(animator.avatar)
                };
            }

            return result;
        }

        private static AnimatorController ResolveAnimatorController(RuntimeAnimatorController runtimeController)
        {
            RuntimeAnimatorController current = runtimeController;
            for (int depth = 0; depth < 8 && current != null; depth++)
            {
                if (current is AnimatorController controller) return controller;
                if (current is AnimatorOverrideController overrideController)
                {
                    current = overrideController.runtimeAnimatorController;
                    continue;
                }
                break;
            }
            return null;
        }

        private static JObject ReadAnimatorStateMachineGraph(
            AnimatorStateMachine stateMachine,
            string path,
            int maximumStates,
            int maximumTransitions,
            int maximumMotions,
            ref int stateCount,
            ref int transitionCount,
            ref int motionCount,
            ref bool truncated,
            HashSet<string> clipPaths)
        {
            if (stateMachine == null) return null;

            var states = new JArray();
            foreach (ChildAnimatorState child in stateMachine.states)
            {
                if (stateCount >= maximumStates)
                {
                    truncated = true;
                    break;
                }

                AnimatorState state = child.state;
                var transitions = new JArray();
                foreach (AnimatorStateTransition transition in state.transitions)
                {
                    if (transitionCount >= maximumTransitions)
                    {
                        truncated = true;
                        break;
                    }
                    transitions.Add(ReadStateTransition(transition));
                    transitionCount++;
                }

                var behaviours = new JArray();
                foreach (StateMachineBehaviour behaviour in state.behaviours)
                    behaviours.Add(behaviour == null ? "MissingBehaviour" : behaviour.GetType().FullName);

                states.Add(new JObject
                {
                    ["name"] = state.name,
                    ["path"] = path + "/" + state.name,
                    ["position"] = child.position.ToString(),
                    ["tag"] = state.tag,
                    ["speed"] = state.speed,
                    ["cycleOffset"] = state.cycleOffset,
                    ["mirror"] = state.mirror,
                    ["iKOnFeet"] = state.iKOnFeet,
                    ["writeDefaultValues"] = state.writeDefaultValues,
                    ["motion"] = ReadAnimatorMotion(state.motion, maximumMotions, ref motionCount, ref truncated, clipPaths),
                    ["behaviours"] = behaviours,
                    ["transitions"] = transitions
                });
                stateCount++;
                if (truncated) break;
            }

            var anyStateTransitions = new JArray();
            foreach (AnimatorStateTransition transition in stateMachine.anyStateTransitions)
            {
                if (transitionCount >= maximumTransitions) { truncated = true; break; }
                anyStateTransitions.Add(ReadStateTransition(transition));
                transitionCount++;
            }

            var entryTransitions = new JArray();
            foreach (AnimatorTransition transition in stateMachine.entryTransitions)
            {
                if (transitionCount >= maximumTransitions) { truncated = true; break; }
                entryTransitions.Add(ReadMachineTransition(transition));
                transitionCount++;
            }

            var childMachines = new JArray();
            foreach (ChildAnimatorStateMachine child in stateMachine.stateMachines)
            {
                var machineTransitions = new JArray();
                foreach (AnimatorTransition transition in stateMachine.GetStateMachineTransitions(child.stateMachine))
                {
                    if (transitionCount >= maximumTransitions) { truncated = true; break; }
                    machineTransitions.Add(ReadMachineTransition(transition));
                    transitionCount++;
                }

                JObject childGraph = ReadAnimatorStateMachineGraph(
                    child.stateMachine,
                    path + "/" + child.stateMachine.name,
                    maximumStates,
                    maximumTransitions,
                    maximumMotions,
                    ref stateCount,
                    ref transitionCount,
                    ref motionCount,
                    ref truncated,
                    clipPaths);
                childMachines.Add(new JObject
                {
                    ["name"] = child.stateMachine.name,
                    ["position"] = child.position.ToString(),
                    ["transitions"] = machineTransitions,
                    ["graph"] = childGraph
                });
                if (truncated) break;
            }

            var behavioursForMachine = new JArray();
            foreach (StateMachineBehaviour behaviour in stateMachine.behaviours)
                behavioursForMachine.Add(behaviour == null ? "MissingBehaviour" : behaviour.GetType().FullName);

            return new JObject
            {
                ["name"] = stateMachine.name,
                ["path"] = path,
                ["defaultState"] = stateMachine.defaultState == null ? string.Empty : stateMachine.defaultState.name,
                ["behaviours"] = behavioursForMachine,
                ["states"] = states,
                ["anyStateTransitions"] = anyStateTransitions,
                ["entryTransitions"] = entryTransitions,
                ["childStateMachines"] = childMachines
            };
        }

        private static JObject ReadStateTransition(AnimatorStateTransition transition)
        {
            return new JObject
            {
                ["name"] = transition.name,
                ["destinationState"] = transition.destinationState == null ? string.Empty : transition.destinationState.name,
                ["destinationStateMachine"] = transition.destinationStateMachine == null ? string.Empty : transition.destinationStateMachine.name,
                ["isExit"] = transition.isExit,
                ["mute"] = transition.mute,
                ["solo"] = transition.solo,
                ["hasExitTime"] = transition.hasExitTime,
                ["exitTime"] = transition.exitTime,
                ["duration"] = transition.duration,
                ["offset"] = transition.offset,
                ["hasFixedDuration"] = transition.hasFixedDuration,
                ["interruptionSource"] = transition.interruptionSource.ToString(),
                ["orderedInterruption"] = transition.orderedInterruption,
                ["canTransitionToSelf"] = transition.canTransitionToSelf,
                ["conditions"] = ReadAnimatorConditions(transition.conditions)
            };
        }

        private static JObject ReadMachineTransition(AnimatorTransition transition)
        {
            return new JObject
            {
                ["name"] = transition.name,
                ["destinationState"] = transition.destinationState == null ? string.Empty : transition.destinationState.name,
                ["destinationStateMachine"] = transition.destinationStateMachine == null ? string.Empty : transition.destinationStateMachine.name,
                ["isExit"] = transition.isExit,
                ["mute"] = transition.mute,
                ["solo"] = transition.solo,
                ["conditions"] = ReadAnimatorConditions(transition.conditions)
            };
        }

        private static JArray ReadAnimatorConditions(AnimatorCondition[] conditions)
        {
            var result = new JArray();
            foreach (AnimatorCondition condition in conditions)
            {
                result.Add(new JObject
                {
                    ["parameter"] = condition.parameter,
                    ["mode"] = condition.mode.ToString(),
                    ["threshold"] = condition.threshold
                });
            }
            return result;
        }

        private static JToken ReadAnimatorMotion(
            Motion motion,
            int maximumMotions,
            ref int motionCount,
            ref bool truncated,
            HashSet<string> clipPaths)
        {
            if (motion == null) return JValue.CreateNull();
            if (motionCount >= maximumMotions)
            {
                truncated = true;
                return new JObject { ["name"] = motion.name, ["truncated"] = true };
            }
            motionCount++;

            if (motion is AnimationClip clip)
            {
                AddClipPath(clip, clipPaths);
                return new JObject
                {
                    ["type"] = "AnimationClip",
                    ["name"] = clip.name,
                    ["assetPath"] = AssetDatabase.GetAssetPath(clip),
                    ["length"] = clip.length,
                    ["frameRate"] = clip.frameRate,
                    ["isLooping"] = clip.isLooping,
                    ["isHumanMotion"] = clip.isHumanMotion
                };
            }

            if (motion is BlendTree tree)
            {
                var children = new JArray();
                foreach (ChildMotion child in tree.children)
                {
                    children.Add(new JObject
                    {
                        ["threshold"] = child.threshold,
                        ["position"] = child.position.ToString(),
                        ["timeScale"] = child.timeScale,
                        ["cycleOffset"] = child.cycleOffset,
                        ["directBlendParameter"] = child.directBlendParameter,
                        ["mirror"] = child.mirror,
                        ["motion"] = ReadAnimatorMotion(child.motion, maximumMotions, ref motionCount, ref truncated, clipPaths)
                    });
                    if (truncated) break;
                }

                return new JObject
                {
                    ["type"] = "BlendTree",
                    ["name"] = tree.name,
                    ["assetPath"] = AssetDatabase.GetAssetPath(tree),
                    ["blendType"] = tree.blendType.ToString(),
                    ["blendParameter"] = tree.blendParameter,
                    ["blendParameterY"] = tree.blendParameterY,
                    ["minThreshold"] = tree.minThreshold,
                    ["maxThreshold"] = tree.maxThreshold,
                    ["useAutomaticThresholds"] = tree.useAutomaticThresholds,
                    ["children"] = children
                };
            }

            return new JObject
            {
                ["type"] = motion.GetType().FullName,
                ["name"] = motion.name,
                ["assetPath"] = AssetDatabase.GetAssetPath(motion)
            };
        }

        private static void AddClipPath(AnimationClip clip, HashSet<string> clipPaths)
        {
            if (clip == null) return;
            string path = AssetDatabase.GetAssetPath(clip);
            if (!string.IsNullOrEmpty(path)) clipPaths.Add(path);
        }

        private static JObject GetPackages()
        {
            string path = Path.Combine(ProjectSecurity.ProjectRoot, "Packages", "manifest.json");
            if (!File.Exists(path)) throw new FileNotFoundException("Packages/manifest.json was not found.", path);
            JObject manifest = JObject.Parse(File.ReadAllText(path));
            return new JObject
            {
                ["path"] = "Packages/manifest.json",
                ["dependencies"] = manifest["dependencies"] ?? new JObject()
            };
        }

        private static string SerializedValue(SerializedProperty property)
        {
            switch (property.propertyType)
            {
                case SerializedPropertyType.Integer: return property.longValue.ToString();
                case SerializedPropertyType.Boolean: return property.boolValue.ToString();
                case SerializedPropertyType.Float: return property.doubleValue.ToString("R");
                case SerializedPropertyType.String: return Truncate(property.stringValue, 1000);
                case SerializedPropertyType.Color: return property.colorValue.ToString();
                case SerializedPropertyType.ObjectReference:
                    return property.objectReferenceValue == null ? "null" : property.objectReferenceValue.name + " (" + property.objectReferenceValue.GetType().Name + ")";
                case SerializedPropertyType.LayerMask: return property.intValue.ToString();
                case SerializedPropertyType.Enum: return property.enumDisplayNames.Length > property.enumValueIndex && property.enumValueIndex >= 0 ? property.enumDisplayNames[property.enumValueIndex] : property.enumValueIndex.ToString();
                case SerializedPropertyType.Vector2: return property.vector2Value.ToString();
                case SerializedPropertyType.Vector3: return property.vector3Value.ToString();
                case SerializedPropertyType.Vector4: return property.vector4Value.ToString();
                case SerializedPropertyType.Rect: return property.rectValue.ToString();
                case SerializedPropertyType.ArraySize: return property.intValue.ToString();
                case SerializedPropertyType.Character: return Convert.ToChar(property.intValue).ToString();
                case SerializedPropertyType.Bounds: return property.boundsValue.ToString();
                case SerializedPropertyType.Quaternion: return property.quaternionValue.ToString();
                case SerializedPropertyType.Vector2Int: return property.vector2IntValue.ToString();
                case SerializedPropertyType.Vector3Int: return property.vector3IntValue.ToString();
                case SerializedPropertyType.RectInt: return property.rectIntValue.ToString();
                case SerializedPropertyType.BoundsInt: return property.boundsIntValue.ToString();
                case SerializedPropertyType.ManagedReference: return property.managedReferenceFullTypename ?? "null";
                default: return "(" + property.propertyType + ")";
            }
        }

        private static GameObject GetPreferredPrefabInstanceRoot(GameObject selected)
        {
            try
            {
                if (GetNearestPrefabInstanceRootMethod != null)
                {
                    GameObject nearest = GetNearestPrefabInstanceRootMethod.Invoke(null, new object[] { selected }) as GameObject;
                    if (nearest != null) return nearest;
                }
            }
            catch { }

            return PrefabUtility.GetOutermostPrefabInstanceRoot(selected);
        }

        private static bool HasMissingObjectReference(SerializedProperty property)
        {
            if (property == null || property.propertyType != SerializedPropertyType.ObjectReference
                || property.objectReferenceValue != null)
                return false;

            try
            {
                if (ObjectReferenceInstanceIdProperty == null) return false;
                object value = ObjectReferenceInstanceIdProperty.GetValue(property, null);
                return value is int instanceId && instanceId != 0;
            }
            catch
            {
                return false;
            }
        }

        private static JToken GetEnabledState(Component component)
        {
            if (component is Behaviour behaviour) return behaviour.enabled;
            if (component is Renderer renderer) return renderer.enabled;
            if (component is Collider collider) return collider.enabled;
            return JValue.CreateNull();
        }

        private static string GetHierarchyPath(Transform transform)
        {
            var names = new Stack<string>();
            for (Transform current = transform; current != null; current = current.parent) names.Push(current.name);
            return string.Join("/", names.ToArray());
        }

        private static int ReadInt(JObject args, string name, int defaultValue, int minimum, int maximum)
        {
            int value = args[name]?.Value<int?>() ?? defaultValue;
            return Mathf.Clamp(value, minimum, maximum);
        }

        private static bool ReadBool(JObject args, string name, bool defaultValue) => args[name]?.Value<bool?>() ?? defaultValue;
        private static string ReadString(JObject args, string name, string defaultValue) => args[name]?.Value<string>() ?? defaultValue;
        private static string Truncate(string value, int maximum) => string.IsNullOrEmpty(value) || value.Length <= maximum ? value ?? string.Empty : value.Substring(0, maximum) + "…";
    }
}
