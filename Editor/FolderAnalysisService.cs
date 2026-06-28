using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace WasimDevelopment.UnityMcpBridge
{
    internal static class FolderAnalysisService
    {
        private const int MaximumSubfolders = 200;
        private const int MaximumTypesPerScript = 30;
        private const int MaximumNamespacesPerScript = 20;
        private const int MaximumObjectsPerPrefab = 500;
        private const int MaximumSerializedPropertiesPerPrefab = 5000;
        private const int MaximumAnimatorParameters = 100;
        private const int MaximumAnimatorClips = 150;
        private const int MaximumAnimatorStates = 2000;
        private const int MaximumAnimatorTransitions = 5000;
        private const int MaximumAnimatorMotions = 3000;

        private static readonly Regex NamespaceRegex = new Regex(
            @"\bnamespace\s+([A-Za-z_][A-Za-z0-9_\.]*)",
            RegexOptions.CultureInvariant);

        private static readonly Regex TypeRegex = new Regex(
            @"\b(class|struct|interface|enum)\s+([A-Za-z_][A-Za-z0-9_]*)(?:\s*:\s*([^\{\r\n]+))?",
            RegexOptions.CultureInvariant);

        private static readonly PropertyInfo ObjectReferenceInstanceIdProperty = typeof(SerializedProperty).GetProperty(
            "objectReferenceInstanceIDValue",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        public static JObject Analyze(JObject args)
        {
            args = args ?? new JObject();

            bool includeSubfolders = ReadBool(args, "includeSubfolders", true);
            bool analyzeScripts = ReadBool(args, "analyzeScripts", true);
            bool analyzePrefabs = ReadBool(args, "analyzePrefabs", true);
            bool analyzeAnimatorControllers = ReadBool(args, "analyzeAnimatorControllers", true);
            bool includeExternalDependencies = ReadBool(args, "includeExternalDependencies", true);
            int maximumAssets = ReadInt(args, "maximumAssets", 200, 1, 2000);
            int maximumDetailedItemsPerType = ReadInt(args, "maximumDetailedItemsPerType", 30, 1, 200);
            int maximumIssues = ReadInt(args, "maximumIssues", 150, 1, 1000);
            int maximumDependencies = ReadInt(args, "maximumDependencies", 100, 0, 1000);

            UnityEngine.Object selected = Selection.activeObject;
            if (selected == null)
                throw new InvalidOperationException("No folder is selected in the Unity Project window.");

            string folderPath = NormalizeAssetPath(AssetDatabase.GetAssetPath(selected));
            if (string.IsNullOrWhiteSpace(folderPath) || !AssetDatabase.IsValidFolder(folderPath))
                throw new InvalidOperationException("The selected Project item is not a folder.");

            if (!string.Equals(folderPath, "Assets", StringComparison.OrdinalIgnoreCase)
                && !folderPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Selected-folder analysis is limited to the Assets folder in this release.");
            }

            List<string> discoveredPaths = FindAssetPaths(folderPath, includeSubfolders);
            List<string> scannedPaths = discoveredPaths.Take(maximumAssets).ToList();
            bool assetLimitReached = discoveredPaths.Count > scannedPaths.Count;

            var issues = new JArray();
            int totalIssueCount = 0;
            var assets = new JArray();
            var scripts = new JArray();
            var prefabs = new JArray();
            var animatorControllers = new JArray();
            var compileMessages = new JArray();
            var extensionCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var assetTypeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var categoryCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var dependencyMap = new Dictionary<string, DependencySummary>(StringComparer.OrdinalIgnoreCase);
            var typeDeclarationPaths = new Dictionary<string, List<string>>(StringComparer.Ordinal);

            int detailedScriptCount = 0;
            int detailedPrefabCount = 0;
            int detailedAnimatorCount = 0;

            foreach (string path in scannedPaths)
            {
                string extension = Path.GetExtension(path).ToLowerInvariant();
                string category = ClassifyAsset(path, extension);
                Increment(extensionCounts, string.IsNullOrEmpty(extension) ? "(none)" : extension);
                Increment(categoryCounts, category);

                Type mainType = null;
                try { mainType = AssetDatabase.GetMainAssetTypeAtPath(path); }
                catch { }
                string typeName = mainType == null ? string.Empty : mainType.FullName;
                Increment(assetTypeCounts, string.IsNullOrEmpty(typeName) ? "Unknown" : typeName);

                long sizeBytes = TryGetFileSize(path);
                assets.Add(new JObject
                {
                    ["path"] = path,
                    ["category"] = category,
                    ["extension"] = extension,
                    ["mainAssetType"] = typeName,
                    ["sizeBytes"] = sizeBytes
                });

                if (includeExternalDependencies && maximumDependencies > 0)
                    CollectExternalDependencies(path, folderPath, dependencyMap);

                if (analyzeScripts && string.Equals(category, "Script", StringComparison.Ordinal)
                    && detailedScriptCount < maximumDetailedItemsPerType)
                {
                    JObject scriptSummary = AnalyzeScript(path, issues, maximumIssues, ref totalIssueCount, typeDeclarationPaths);
                    scripts.Add(scriptSummary);
                    detailedScriptCount++;
                }
                else if (analyzePrefabs && string.Equals(category, "Prefab", StringComparison.Ordinal)
                    && detailedPrefabCount < maximumDetailedItemsPerType)
                {
                    JObject prefabSummary = AnalyzePrefab(path, issues, maximumIssues, ref totalIssueCount);
                    prefabs.Add(prefabSummary);
                    detailedPrefabCount++;
                }
                else if (analyzeAnimatorControllers && string.Equals(category, "AnimatorController", StringComparison.Ordinal)
                    && detailedAnimatorCount < maximumDetailedItemsPerType)
                {
                    JObject animatorSummary = AnalyzeAnimatorController(path, issues, maximumIssues, ref totalIssueCount);
                    animatorControllers.Add(animatorSummary);
                    detailedAnimatorCount++;
                }
            }

            AddDuplicateTypeIssues(typeDeclarationPaths, issues, maximumIssues, ref totalIssueCount);
            CollectFolderCompilationMessages(folderPath, compileMessages, issues, maximumIssues, ref totalIssueCount);

            JArray dependencies = BuildDependencyResults(dependencyMap, maximumDependencies, out bool dependencyLimitReached);
            JArray subfolders = CollectSubfolders(folderPath, includeSubfolders, out bool subfolderLimitReached);

            int scannedScriptCount = GetCount(categoryCounts, "Script");
            int scannedPrefabCount = GetCount(categoryCounts, "Prefab");
            int scannedAnimatorCount = GetCount(categoryCounts, "AnimatorController");

            return new JObject
            {
                ["folderName"] = selected.name,
                ["folderPath"] = folderPath,
                ["includeSubfolders"] = includeSubfolders,
                ["totalDiscoveredAssets"] = discoveredPaths.Count,
                ["scannedAssetCount"] = scannedPaths.Count,
                ["assetLimitReached"] = assetLimitReached,
                ["maximumAssets"] = maximumAssets,
                ["subfolders"] = subfolders,
                ["subfoldersTruncated"] = subfolderLimitReached,
                ["categoryCountsForScannedAssets"] = ToCountObject(categoryCounts),
                ["extensionCountsForScannedAssets"] = ToCountObject(extensionCounts),
                ["mainAssetTypeCountsForScannedAssets"] = ToCountObject(assetTypeCounts),
                ["assets"] = assets,
                ["scripts"] = scripts,
                ["scriptsDetailedCount"] = scripts.Count,
                ["scriptsDetailTruncated"] = analyzeScripts && scannedScriptCount > scripts.Count,
                ["prefabs"] = prefabs,
                ["prefabsDetailedCount"] = prefabs.Count,
                ["prefabsDetailTruncated"] = analyzePrefabs && scannedPrefabCount > prefabs.Count,
                ["animatorControllers"] = animatorControllers,
                ["animatorControllersDetailedCount"] = animatorControllers.Count,
                ["animatorControllersDetailTruncated"] = analyzeAnimatorControllers && scannedAnimatorCount > animatorControllers.Count,
                ["compileMessagesInFolder"] = compileMessages,
                ["externalDependencies"] = dependencies,
                ["externalDependenciesTruncated"] = dependencyLimitReached,
                ["issues"] = issues,
                ["returnedIssueCount"] = issues.Count,
                ["totalIssueCount"] = totalIssueCount,
                ["issuesTruncated"] = totalIssueCount > issues.Count,
                ["analysisOptions"] = new JObject
                {
                    ["analyzeScripts"] = analyzeScripts,
                    ["analyzePrefabs"] = analyzePrefabs,
                    ["analyzeAnimatorControllers"] = analyzeAnimatorControllers,
                    ["includeExternalDependencies"] = includeExternalDependencies,
                    ["maximumDetailedItemsPerType"] = maximumDetailedItemsPerType,
                    ["maximumIssues"] = maximumIssues,
                    ["maximumDependencies"] = maximumDependencies
                }
            };
        }

        private static List<string> FindAssetPaths(string folderPath, bool includeSubfolders)
        {
            string[] guids = AssetDatabase.FindAssets(string.Empty, new[] { folderPath });
            var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string guid in guids)
            {
                string path = NormalizeAssetPath(AssetDatabase.GUIDToAssetPath(guid));
                if (string.IsNullOrWhiteSpace(path) || AssetDatabase.IsValidFolder(path))
                    continue;
                if (!IsPathInsideFolder(path, folderPath))
                    continue;
                if (!includeSubfolders && !string.Equals(GetAssetDirectory(path), folderPath, StringComparison.OrdinalIgnoreCase))
                    continue;
                unique.Add(path);
            }

            return unique.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static JArray CollectSubfolders(string folderPath, bool recursive, out bool truncated)
        {
            truncated = false;
            var result = new JArray();
            var pending = new Queue<string>();
            foreach (string child in AssetDatabase.GetSubFolders(folderPath))
                pending.Enqueue(NormalizeAssetPath(child));

            while (pending.Count > 0)
            {
                string current = pending.Dequeue();
                if (result.Count >= MaximumSubfolders)
                {
                    truncated = true;
                    break;
                }

                result.Add(current);
                if (!recursive) continue;
                foreach (string child in AssetDatabase.GetSubFolders(current))
                    pending.Enqueue(NormalizeAssetPath(child));
            }

            return result;
        }

        private static JObject AnalyzeScript(
            string path,
            JArray issues,
            int maximumIssues,
            ref int totalIssueCount,
            Dictionary<string, List<string>> typeDeclarationPaths)
        {
            string fullPath = Path.Combine(ProjectSecurity.ProjectRoot, path.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                FileInfo info = new FileInfo(fullPath);
                if (!info.Exists)
                    throw new FileNotFoundException("Script file was not found.", fullPath);
                if (info.Length > ProjectSecurity.MaxScriptBytes)
                {
                    AddIssue(issues, maximumIssues, ref totalIssueCount, "Warning", "Script", path,
                        "Script exceeds the bridge read limit and was summarized without reading its contents.", string.Empty);
                    return new JObject
                    {
                        ["path"] = path,
                        ["sizeBytes"] = info.Length,
                        ["contentAnalyzed"] = false,
                        ["reason"] = "Script exceeds the read limit."
                    };
                }

                string text = File.ReadAllText(fullPath);
                int lineCount = CountLines(text);
                var namespaces = new JArray();
                var seenNamespaces = new HashSet<string>(StringComparer.Ordinal);
                foreach (Match match in NamespaceRegex.Matches(text))
                {
                    string value = match.Groups[1].Value;
                    if (seenNamespaces.Add(value) && namespaces.Count < MaximumNamespacesPerScript)
                        namespaces.Add(value);
                }

                var declarations = new JArray();
                foreach (Match match in TypeRegex.Matches(text))
                {
                    if (declarations.Count >= MaximumTypesPerScript) break;
                    string kind = match.Groups[1].Value;
                    string name = match.Groups[2].Value;
                    string bases = match.Groups[3].Success ? match.Groups[3].Value.Trim() : string.Empty;
                    declarations.Add(new JObject
                    {
                        ["kind"] = kind,
                        ["name"] = name,
                        ["baseTypes"] = bases
                    });

                    if (!typeDeclarationPaths.TryGetValue(name, out List<string> paths))
                    {
                        paths = new List<string>();
                        typeDeclarationPaths[name] = paths;
                    }
                    if (!paths.Contains(path)) paths.Add(path);
                }

                int todoCount = Regex.Matches(text, @"\bTODO\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Count;
                int fixmeCount = Regex.Matches(text, @"\bFIXME\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Count;
                int regionCount = Regex.Matches(text, @"^\s*#region\b", RegexOptions.Multiline | RegexOptions.CultureInvariant).Count;

                return new JObject
                {
                    ["path"] = path,
                    ["sizeBytes"] = info.Length,
                    ["lineCount"] = lineCount,
                    ["sha256"] = ScriptChangeManager.ComputeFileSha256(path),
                    ["namespaces"] = namespaces,
                    ["typeDeclarations"] = declarations,
                    ["typeDeclarationsTruncated"] = TypeRegex.Matches(text).Count > declarations.Count,
                    ["todoCount"] = todoCount,
                    ["fixmeCount"] = fixmeCount,
                    ["regionCount"] = regionCount,
                    ["contentAnalyzed"] = true
                };
            }
            catch (Exception ex)
            {
                AddIssue(issues, maximumIssues, ref totalIssueCount, "Error", "Script", path,
                    "Failed to analyze script: " + ex.Message, string.Empty);
                return new JObject
                {
                    ["path"] = path,
                    ["contentAnalyzed"] = false,
                    ["error"] = ex.Message
                };
            }
        }

        private static JObject AnalyzePrefab(
            string path,
            JArray issues,
            int maximumIssues,
            ref int totalIssueCount)
        {
            GameObject root = null;
            try
            {
                root = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (root == null)
                    throw new InvalidOperationException("Prefab root could not be loaded.");

                int objectCount = 0;
                int componentCount = 0;
                int missingScriptCount = 0;
                int missingReferenceCount = 0;
                int animatorCount = 0;
                int serializedPropertyCount = 0;
                bool objectLimitReached = false;
                bool propertyLimitReached = false;
                var componentTypeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
                var pending = new Stack<Transform>();
                pending.Push(root.transform);

                while (pending.Count > 0)
                {
                    if (objectCount >= MaximumObjectsPerPrefab)
                    {
                        objectLimitReached = true;
                        break;
                    }

                    Transform current = pending.Pop();
                    objectCount++;
                    string objectPath = GetRelativeHierarchyPath(root.transform, current);
                    Component[] components = current.gameObject.GetComponents<Component>();
                    foreach (Component component in components)
                    {
                        componentCount++;
                        if (component == null)
                        {
                            missingScriptCount++;
                            AddIssue(issues, maximumIssues, ref totalIssueCount, "Error", "Prefab", path,
                                "Missing script component.", objectPath);
                            continue;
                        }

                        string componentType = component.GetType().FullName;
                        Increment(componentTypeCounts, componentType);
                        if (component is Animator) animatorCount++;

                        if (serializedPropertyCount >= MaximumSerializedPropertiesPerPrefab)
                        {
                            propertyLimitReached = true;
                            continue;
                        }

                        try
                        {
                            var serializedObject = new SerializedObject(component);
                            SerializedProperty iterator = serializedObject.GetIterator();
                            bool enterChildren = true;
                            while (serializedPropertyCount < MaximumSerializedPropertiesPerPrefab
                                   && iterator.NextVisible(enterChildren))
                            {
                                enterChildren = false;
                                serializedPropertyCount++;
                                if (!IsMissingObjectReference(iterator)) continue;
                                missingReferenceCount++;
                                AddIssue(issues, maximumIssues, ref totalIssueCount, "Warning", "Prefab", path,
                                    "Missing object reference on " + component.GetType().Name + "." + iterator.propertyPath,
                                    objectPath);
                            }
                            if (serializedPropertyCount >= MaximumSerializedPropertiesPerPrefab)
                                propertyLimitReached = true;
                        }
                        catch (Exception ex)
                        {
                            AddIssue(issues, maximumIssues, ref totalIssueCount, "Warning", "Prefab", path,
                                "Could not inspect serialized properties on " + component.GetType().Name + ": " + ex.Message,
                                objectPath);
                        }
                    }

                    for (int i = current.childCount - 1; i >= 0; i--)
                        pending.Push(current.GetChild(i));
                }

                return new JObject
                {
                    ["path"] = path,
                    ["rootName"] = root.name,
                    ["objectCount"] = objectCount,
                    ["componentCount"] = componentCount,
                    ["missingScriptCount"] = missingScriptCount,
                    ["missingObjectReferenceCount"] = missingReferenceCount,
                    ["animatorComponentCount"] = animatorCount,
                    ["serializedPropertiesInspected"] = serializedPropertyCount,
                    ["objectLimitReached"] = objectLimitReached,
                    ["serializedPropertyLimitReached"] = propertyLimitReached,
                    ["topComponentTypes"] = ToTopCountArray(componentTypeCounts, 30)
                };
            }
            catch (Exception ex)
            {
                AddIssue(issues, maximumIssues, ref totalIssueCount, "Error", "Prefab", path,
                    "Failed to analyze prefab: " + ex.Message, string.Empty);
                return new JObject
                {
                    ["path"] = path,
                    ["error"] = ex.Message
                };
            }
        }

        private static JObject AnalyzeAnimatorController(
            string path,
            JArray issues,
            int maximumIssues,
            ref int totalIssueCount)
        {
            try
            {
                RuntimeAnimatorController runtimeController = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(path);
                if (runtimeController == null)
                    throw new InvalidOperationException("Animator Controller asset could not be loaded.");

                AnimatorOverrideController overrideController = runtimeController as AnimatorOverrideController;
                AnimatorController controller = ResolveAnimatorController(runtimeController);
                if (controller == null)
                    throw new InvalidOperationException("The asset does not resolve to an AnimatorController graph.");

                var parameters = new JArray();
                foreach (AnimatorControllerParameter parameter in controller.parameters)
                {
                    if (parameters.Count >= MaximumAnimatorParameters) break;
                    parameters.Add(new JObject
                    {
                        ["name"] = parameter.name,
                        ["type"] = parameter.type.ToString()
                    });
                }

                int stateCount = 0;
                int transitionCount = 0;
                int blendTreeCount = 0;
                int motionCount = 0;
                int emptyStateCount = 0;
                bool truncated = false;
                var clipPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var layers = new JArray();

                foreach (AnimatorControllerLayer layer in controller.layers)
                {
                    int statesBefore = stateCount;
                    int transitionsBefore = transitionCount;
                    int motionsBefore = motionCount;
                    CountStateMachine(
                        layer.stateMachine,
                        path,
                        issues,
                        maximumIssues,
                        ref totalIssueCount,
                        ref stateCount,
                        ref transitionCount,
                        ref blendTreeCount,
                        ref motionCount,
                        ref emptyStateCount,
                        ref truncated,
                        clipPaths);

                    layers.Add(new JObject
                    {
                        ["name"] = layer.name,
                        ["defaultWeight"] = layer.defaultWeight,
                        ["blendingMode"] = layer.blendingMode.ToString(),
                        ["avatarMask"] = layer.avatarMask == null ? string.Empty : AssetDatabase.GetAssetPath(layer.avatarMask),
                        ["syncedLayerIndex"] = layer.syncedLayerIndex,
                        ["stateCount"] = stateCount - statesBefore,
                        ["transitionCount"] = transitionCount - transitionsBefore,
                        ["motionNodeCount"] = motionCount - motionsBefore,
                        ["defaultState"] = layer.stateMachine == null || layer.stateMachine.defaultState == null
                            ? string.Empty
                            : layer.stateMachine.defaultState.name
                    });
                    if (truncated) break;
                }

                int overrideCount = 0;
                int missingOverrideCount = 0;
                if (overrideController != null)
                {
                    var overrides = new List<KeyValuePair<AnimationClip, AnimationClip>>();
                    overrideController.GetOverrides(overrides);
                    overrideCount = overrides.Count;
                    foreach (KeyValuePair<AnimationClip, AnimationClip> pair in overrides)
                    {
                        if (pair.Key != null && pair.Value == null)
                        {
                            missingOverrideCount++;
                            AddIssue(issues, maximumIssues, ref totalIssueCount, "Warning", "AnimatorController", path,
                                "Override Controller has no replacement clip for " + pair.Key.name + ".", string.Empty);
                        }
                    }
                }

                JArray clips = new JArray();
                foreach (string clipPath in clipPaths.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).Take(MaximumAnimatorClips))
                    clips.Add(clipPath);

                return new JObject
                {
                    ["path"] = path,
                    ["assetType"] = runtimeController.GetType().FullName,
                    ["baseControllerPath"] = AssetDatabase.GetAssetPath(controller),
                    ["parameterCount"] = controller.parameters.Length,
                    ["parameters"] = parameters,
                    ["parametersTruncated"] = controller.parameters.Length > parameters.Count,
                    ["layerCount"] = controller.layers.Length,
                    ["layers"] = layers,
                    ["stateCount"] = stateCount,
                    ["transitionCount"] = transitionCount,
                    ["blendTreeCount"] = blendTreeCount,
                    ["motionNodeCount"] = motionCount,
                    ["emptyStateCount"] = emptyStateCount,
                    ["animationClipCount"] = clipPaths.Count,
                    ["animationClips"] = clips,
                    ["animationClipsTruncated"] = clipPaths.Count > clips.Count,
                    ["overrideCount"] = overrideCount,
                    ["missingOverrideCount"] = missingOverrideCount,
                    ["truncated"] = truncated
                };
            }
            catch (Exception ex)
            {
                AddIssue(issues, maximumIssues, ref totalIssueCount, "Error", "AnimatorController", path,
                    "Failed to analyze Animator Controller: " + ex.Message, string.Empty);
                return new JObject
                {
                    ["path"] = path,
                    ["error"] = ex.Message
                };
            }
        }

        private static void CountStateMachine(
            AnimatorStateMachine stateMachine,
            string assetPath,
            JArray issues,
            int maximumIssues,
            ref int totalIssueCount,
            ref int stateCount,
            ref int transitionCount,
            ref int blendTreeCount,
            ref int motionCount,
            ref int emptyStateCount,
            ref bool truncated,
            HashSet<string> clipPaths)
        {
            if (stateMachine == null || truncated) return;

            foreach (ChildAnimatorState child in stateMachine.states)
            {
                if (stateCount >= MaximumAnimatorStates)
                {
                    truncated = true;
                    return;
                }

                AnimatorState state = child.state;
                stateCount++;
                if (state == null) continue;

                transitionCount += state.transitions == null ? 0 : state.transitions.Length;
                if (transitionCount > MaximumAnimatorTransitions)
                {
                    transitionCount = MaximumAnimatorTransitions;
                    truncated = true;
                    return;
                }

                if (state.motion == null)
                {
                    emptyStateCount++;
                }
                else
                {
                    CountMotion(state.motion, ref blendTreeCount, ref motionCount, ref truncated, clipPaths);
                }
                if (truncated) return;
            }

            transitionCount += stateMachine.anyStateTransitions == null ? 0 : stateMachine.anyStateTransitions.Length;
            transitionCount += stateMachine.entryTransitions == null ? 0 : stateMachine.entryTransitions.Length;
            if (transitionCount > MaximumAnimatorTransitions)
            {
                transitionCount = MaximumAnimatorTransitions;
                truncated = true;
                return;
            }

            foreach (ChildAnimatorStateMachine child in stateMachine.stateMachines)
            {
                AnimatorTransition[] transitions = stateMachine.GetStateMachineTransitions(child.stateMachine);
                transitionCount += transitions == null ? 0 : transitions.Length;
                if (transitionCount > MaximumAnimatorTransitions)
                {
                    transitionCount = MaximumAnimatorTransitions;
                    truncated = true;
                    return;
                }
                CountStateMachine(
                    child.stateMachine,
                    assetPath,
                    issues,
                    maximumIssues,
                    ref totalIssueCount,
                    ref stateCount,
                    ref transitionCount,
                    ref blendTreeCount,
                    ref motionCount,
                    ref emptyStateCount,
                    ref truncated,
                    clipPaths);
                if (truncated) return;
            }
        }

        private static void CountMotion(
            Motion motion,
            ref int blendTreeCount,
            ref int motionCount,
            ref bool truncated,
            HashSet<string> clipPaths)
        {
            if (motion == null || truncated) return;
            if (motionCount >= MaximumAnimatorMotions)
            {
                truncated = true;
                return;
            }
            motionCount++;

            AnimationClip clip = motion as AnimationClip;
            if (clip != null)
            {
                string path = AssetDatabase.GetAssetPath(clip);
                if (!string.IsNullOrWhiteSpace(path)) clipPaths.Add(path);
                return;
            }

            BlendTree blendTree = motion as BlendTree;
            if (blendTree == null) return;
            blendTreeCount++;
            foreach (ChildMotion child in blendTree.children)
            {
                CountMotion(child.motion, ref blendTreeCount, ref motionCount, ref truncated, clipPaths);
                if (truncated) return;
            }
        }

        private static AnimatorController ResolveAnimatorController(RuntimeAnimatorController runtimeController)
        {
            RuntimeAnimatorController current = runtimeController;
            var visited = new HashSet<int>();
            while (current is AnimatorOverrideController)
            {
                if (!visited.Add(current.GetInstanceID())) return null;
                current = ((AnimatorOverrideController)current).runtimeAnimatorController;
            }
            return current as AnimatorController;
        }

        private static void CollectFolderCompilationMessages(
            string folderPath,
            JArray compileMessages,
            JArray issues,
            int maximumIssues,
            ref int totalIssueCount)
        {
            JObject status = CompilationMonitor.GetStatus();
            JArray messages = status["messages"] as JArray;
            if (messages == null) return;

            foreach (JToken token in messages)
            {
                JObject message = token as JObject;
                if (message == null) continue;
                string file = NormalizeCompilerPath(message.Value<string>("file"));
                if (!IsPathInsideFolder(file, folderPath)) continue;

                var copy = (JObject)message.DeepClone();
                copy["file"] = file;
                compileMessages.Add(copy);
                AddIssue(
                    issues,
                    maximumIssues,
                    ref totalIssueCount,
                    string.Equals(message.Value<string>("type"), "Error", StringComparison.OrdinalIgnoreCase) ? "Error" : "Warning",
                    "Compiler",
                    file,
                    message.Value<string>("message") ?? string.Empty,
                    "Line " + message.Value<int?>("line").GetValueOrDefault() + ", Column " + message.Value<int?>("column").GetValueOrDefault());
            }
        }

        private static void AddDuplicateTypeIssues(
            Dictionary<string, List<string>> typeDeclarationPaths,
            JArray issues,
            int maximumIssues,
            ref int totalIssueCount)
        {
            foreach (KeyValuePair<string, List<string>> pair in typeDeclarationPaths.OrderBy(value => value.Key, StringComparer.Ordinal))
            {
                if (pair.Value.Count <= 1) continue;
                AddIssue(
                    issues,
                    maximumIssues,
                    ref totalIssueCount,
                    "Info",
                    "Script",
                    pair.Value[0],
                    "Type name is declared in multiple analyzed scripts (this may be intentional for partial types): " + pair.Key,
                    string.Join(", ", pair.Value.ToArray()));
            }
        }

        private static void CollectExternalDependencies(
            string assetPath,
            string folderPath,
            Dictionary<string, DependencySummary> map)
        {
            try
            {
                string[] dependencies = AssetDatabase.GetDependencies(assetPath, false);
                foreach (string dependency in dependencies)
                {
                    string normalized = NormalizeAssetPath(dependency);
                    if (string.IsNullOrWhiteSpace(normalized)
                        || string.Equals(normalized, assetPath, StringComparison.OrdinalIgnoreCase)
                        || IsPathInsideFolder(normalized, folderPath))
                    {
                        continue;
                    }

                    if (!map.TryGetValue(normalized, out DependencySummary summary))
                    {
                        summary = new DependencySummary();
                        map[normalized] = summary;
                    }
                    summary.ReferenceCount++;
                    if (summary.ReferencedBy.Count < 5 && !summary.ReferencedBy.Contains(assetPath))
                        summary.ReferencedBy.Add(assetPath);
                }
            }
            catch
            {
                // Individual dependency failures should not abort the folder report.
            }
        }

        private static JArray BuildDependencyResults(
            Dictionary<string, DependencySummary> map,
            int maximumDependencies,
            out bool truncated)
        {
            var result = new JArray();
            if (maximumDependencies <= 0)
            {
                truncated = map.Count > 0;
                return result;
            }

            foreach (KeyValuePair<string, DependencySummary> pair in map
                         .OrderByDescending(value => value.Value.ReferenceCount)
                         .ThenBy(value => value.Key, StringComparer.OrdinalIgnoreCase)
                         .Take(maximumDependencies))
            {
                result.Add(new JObject
                {
                    ["path"] = pair.Key,
                    ["referencedByCount"] = pair.Value.ReferenceCount,
                    ["referencedBySamples"] = new JArray(pair.Value.ReferencedBy)
                });
            }

            truncated = map.Count > result.Count;
            return result;
        }

        private static bool IsMissingObjectReference(SerializedProperty property)
        {
            if (property == null || property.propertyType != SerializedPropertyType.ObjectReference)
                return false;
            if (property.objectReferenceValue != null)
                return false;

            try
            {
                if (ObjectReferenceInstanceIdProperty != null)
                {
                    object value = ObjectReferenceInstanceIdProperty.GetValue(property, null);
                    return value != null && Convert.ToInt32(value) != 0;
                }
            }
            catch
            {
                return false;
            }
            return false;
        }

        private static void AddIssue(
            JArray issues,
            int maximumIssues,
            ref int totalIssueCount,
            string severity,
            string category,
            string path,
            string message,
            string context)
        {
            totalIssueCount++;
            if (issues.Count >= maximumIssues) return;
            issues.Add(new JObject
            {
                ["severity"] = severity,
                ["category"] = category,
                ["path"] = path ?? string.Empty,
                ["message"] = message ?? string.Empty,
                ["context"] = context ?? string.Empty
            });
        }

        private static string ClassifyAsset(string path, string extension)
        {
            if (string.Equals(extension, ".cs", StringComparison.OrdinalIgnoreCase)) return "Script";
            if (string.Equals(extension, ".prefab", StringComparison.OrdinalIgnoreCase)) return "Prefab";
            if (string.Equals(extension, ".controller", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".overridecontroller", StringComparison.OrdinalIgnoreCase)) return "AnimatorController";
            if (string.Equals(extension, ".unity", StringComparison.OrdinalIgnoreCase)) return "Scene";
            if (string.Equals(extension, ".anim", StringComparison.OrdinalIgnoreCase)) return "AnimationClip";
            if (string.Equals(extension, ".mat", StringComparison.OrdinalIgnoreCase)) return "Material";
            if (string.Equals(extension, ".asset", StringComparison.OrdinalIgnoreCase)) return "Asset";
            if (string.Equals(extension, ".fbx", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".obj", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".dae", StringComparison.OrdinalIgnoreCase)) return "Model";
            if (string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".tga", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".psd", StringComparison.OrdinalIgnoreCase)) return "Texture";
            if (string.Equals(extension, ".wav", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".mp3", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".ogg", StringComparison.OrdinalIgnoreCase)) return "Audio";
            return "Other";
        }

        private static string NormalizeCompilerPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            try
            {
                if (Path.IsPathRooted(path))
                    return NormalizeAssetPath(ProjectSecurity.ToProjectRelativeOrAbsolute(path));
            }
            catch { }
            return NormalizeAssetPath(path);
        }

        private static bool IsPathInsideFolder(string path, string folderPath)
        {
            string normalizedPath = NormalizeAssetPath(path).TrimEnd('/');
            string normalizedFolder = NormalizeAssetPath(folderPath).TrimEnd('/');
            return string.Equals(normalizedPath, normalizedFolder, StringComparison.OrdinalIgnoreCase)
                   || normalizedPath.StartsWith(normalizedFolder + "/", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetAssetDirectory(string assetPath)
        {
            string directory = Path.GetDirectoryName(assetPath);
            return NormalizeAssetPath(directory);
        }

        private static string NormalizeAssetPath(string path)
        {
            return string.IsNullOrWhiteSpace(path) ? string.Empty : path.Replace('\\', '/').Trim();
        }

        private static long TryGetFileSize(string assetPath)
        {
            try
            {
                string fullPath = Path.Combine(ProjectSecurity.ProjectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar));
                return File.Exists(fullPath) ? new FileInfo(fullPath).Length : 0L;
            }
            catch
            {
                return 0L;
            }
        }

        private static int CountLines(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int count = 1;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n') count++;
            }
            return count;
        }

        private static string GetRelativeHierarchyPath(Transform root, Transform current)
        {
            if (current == root) return root.name;
            var names = new Stack<string>();
            Transform cursor = current;
            while (cursor != null)
            {
                names.Push(cursor.name);
                if (cursor == root) break;
                cursor = cursor.parent;
            }
            return string.Join("/", names.ToArray());
        }

        private static void Increment(Dictionary<string, int> dictionary, string key)
        {
            if (string.IsNullOrWhiteSpace(key)) key = "Unknown";
            dictionary.TryGetValue(key, out int count);
            dictionary[key] = count + 1;
        }

        private static int GetCount(Dictionary<string, int> dictionary, string key)
        {
            return dictionary.TryGetValue(key, out int count) ? count : 0;
        }

        private static JObject ToCountObject(Dictionary<string, int> dictionary)
        {
            var result = new JObject();
            foreach (KeyValuePair<string, int> pair in dictionary.OrderBy(value => value.Key, StringComparer.OrdinalIgnoreCase))
                result[pair.Key] = pair.Value;
            return result;
        }

        private static JArray ToTopCountArray(Dictionary<string, int> dictionary, int maximum)
        {
            var result = new JArray();
            foreach (KeyValuePair<string, int> pair in dictionary
                         .OrderByDescending(value => value.Value)
                         .ThenBy(value => value.Key, StringComparer.Ordinal)
                         .Take(maximum))
            {
                result.Add(new JObject
                {
                    ["type"] = pair.Key,
                    ["count"] = pair.Value
                });
            }
            return result;
        }

        private static bool ReadBool(JObject args, string name, bool fallback)
        {
            JToken token = args[name];
            return token == null || token.Type == JTokenType.Null ? fallback : token.Value<bool>();
        }

        private static int ReadInt(JObject args, string name, int fallback, int minimum, int maximum)
        {
            JToken token = args[name];
            int value = token == null || token.Type == JTokenType.Null ? fallback : token.Value<int>();
            return Mathf.Clamp(value, minimum, maximum);
        }

        private sealed class DependencySummary
        {
            public int ReferenceCount;
            public readonly List<string> ReferencedBy = new List<string>();
        }
    }
}
