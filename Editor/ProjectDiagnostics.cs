using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace WasimDevelopment.UnityMcpBridge
{
    internal static class ProjectDiagnostics
    {
        public static JObject Validate(JObject args)
        {
            GameObject root = ObjectAccess.SelectedOrId(args);
            int maximum = ObjectAccess.Limit(args, "maximumObjects", 200, 1, 1000);
            var issues = new JArray(); var layers = new HashSet<int>(); int count = 0;
            Action<string, string, UnityEngine.Object> issue = (severity, message, target) =>
            { if (issues.Count < 500) issues.Add(new JObject { ["severity"] = severity, ["message"] = message, ["object"] = ObjectAccess.Reference(target) }); };
            foreach (GameObject go in ObjectAccess.Walk(root, maximum))
            {
                count++; layers.Add(go.layer);
                Component[] components = go.GetComponents<Component>();
                foreach (Component c in components)
                {
                    if (c == null) { issue("Error", "Missing script.", go); continue; }
                    string type = c.GetType().FullName ?? "";
                    using (var serialized = new SerializedObject(c))
                    {
                        var p = serialized.GetIterator(); int scanned = 0;
                        while (scanned++ < 2000 && p.NextVisible(true))
                            if (p.propertyType == SerializedPropertyType.ObjectReference && p.objectReferenceValue == null
                                && p.objectReferenceInstanceIDValue != 0) issue("Error", "Missing object reference: " + p.propertyPath, c);
                    }
                    if (type.StartsWith("VRC.", StringComparison.Ordinal) && (type.EndsWith("Pickup", StringComparison.Ordinal) || type.EndsWith("VRC_Pickup", StringComparison.Ordinal)))
                    {
                        if (go.GetComponent<Rigidbody>() == null) issue("Error", "Pickup has no Rigidbody on its GameObject.", c);
                        if (go.GetComponentsInChildren<Collider>(true).Length == 0) issue("Error", "Pickup has no collider in its hierarchy.", c);
                        if (go.isStatic) issue("Warning", "Pickup is marked Static.", c);
                    }
                    if (c is Joint joint)
                    {
                        if (joint.connectedBody == null) issue("Info", "Joint is connected to the world, not another Rigidbody.", c);
                        Rigidbody own = go.GetComponent<Rigidbody>();
                        if (own != null && own.isKinematic && joint.connectedBody != null && joint.connectedBody.isKinematic)
                            issue("Warning", "Both joint bodies are kinematic; physics will not reposition them.", c);
                    }
                    if (c is Animator animator && animator.runtimeAnimatorController == null)
                        issue("Warning", "Animator has no controller; verify whether this object needs animation.", c);
                    if (type.EndsWith("VRCObjectSync", StringComparison.Ordinal) && go.GetComponent<Rigidbody>() == null)
                        issue("Warning", "ObjectSync has no Rigidbody; check the intended physics setup.", c);
                }
            }
            var collision = new JArray();
            foreach (int a in layers.OrderBy(x => x)) foreach (int b in layers.Where(x => x >= a).OrderBy(x => x))
                collision.Add(new JObject { ["layerA"] = a, ["nameA"] = LayerMask.LayerToName(a),
                    ["layerB"] = b, ["nameB"] = LayerMask.LayerToName(b), ["collides"] = !Physics.GetIgnoreLayerCollision(a, b) });
            return new JObject { ["objectsScanned"] = count, ["limitReached"] = count >= maximum,
                ["issues"] = issues, ["collisionMatrixForUsedLayers"] = collision,
                ["compilation"] = CompilationMonitor.GetStatus(),
                ["limitations"] = new JArray("Static configuration checks do not prove multiplayer ownership correctness.",
                    "VRChat Network ID allocation and full SDK build validation remain in the SDK build/Network ID utility.") };
        }

        public static JObject References(JObject args)
        {
            string path = args["path"]?.Value<string>() ?? AssetDatabase.GetAssetPath(Selection.activeObject);
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(path))) throw new ArgumentException("Select an asset or supply its asset path.");
            ProjectSecurity.ResolveAssetFile(path);
            int maximum = ObjectAccess.Limit(args, "maximumAssets", 200, 1, 1000);
            int start = ObjectAccess.Limit(args, "startIndex", 0, 0, int.MaxValue);
            string[] paths = AssetDatabase.FindAssets("", new[] { "Assets" }).Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => !AssetDatabase.IsValidFolder(p)).Distinct().OrderBy(p => p, StringComparer.Ordinal).ToArray();
            var refs = new JArray(); int index = Math.Min(start, paths.Length);
            int end = Math.Min(paths.Length, index + maximum);
            for (; index < end; index++)
            {
                string candidate = paths[index];
                if (candidate != path && AssetDatabase.GetDependencies(candidate, false).Contains(path))
                    refs.Add(new JObject { ["path"] = candidate, ["guid"] = AssetDatabase.AssetPathToGUID(candidate) });
            }
            return new JObject { ["path"] = path, ["guid"] = AssetDatabase.AssetPathToGUID(path),
                ["directDependencies"] = new JArray(AssetDatabase.GetDependencies(path, false).Where(p => p != path)),
                ["referencedBy"] = refs, ["scannedThisPage"] = end - Math.Min(start, paths.Length),
                ["totalCandidateAssets"] = paths.Length, ["nextStartIndex"] = index < paths.Length ? (JToken)index : JValue.CreateNull(),
                ["scope"] = "Direct asset dependencies under Assets; save scenes first. Source-code symbol references use unity_find_script_references." };
        }

        public static JObject RuntimeChecks(JObject args)
        {
            if (!EditorApplication.isPlaying) throw new InvalidOperationException("Runtime assertions require Play Mode.");
            JArray checks = args["checks"] as JArray;
            if (checks == null || checks.Count == 0 || checks.Count > 100) throw new ArgumentException("Provide 1-100 checks.");
            var results = new JArray();
            foreach (JObject check in checks)
            {
                bool passed = false; JToken actual = null; string error = null;
                try
                {
                    var component = ObjectAccess.Resolve((string)check["objectId"]) as Component;
                    if (component == null) throw new ArgumentException("Runtime checks require a component objectId.");
                    JObject snapshot = RuntimeInspection.ComponentValues(component, args["udonVariables"] as JArray);
                    string property = (string)check["property"];
                    actual = snapshot[property];
                    if (actual == null) throw new ArgumentException("Property is not exposed by the runtime inspector.");
                    if (check.Property("equals") == null) throw new ArgumentException("Provide an equals value.");
                    passed = JToken.DeepEquals(actual, check["equals"]);
                }
                catch (Exception ex) { error = ex.GetBaseException().Message; }
                results.Add(new JObject { ["objectId"] = check["objectId"], ["property"] = check["property"],
                    ["expected"] = check["equals"], ["actual"] = actual, ["passed"] = passed, ["error"] = error });
            }
            return new JObject { ["timestampUtc"] = DateTime.UtcNow.ToString("O"),
                ["passed"] = results.All(r => r["passed"].Value<bool>()), ["checks"] = results,
                ["note"] = "Assertions sample current state; they do not simulate VRChat players or pickup input." };
        }
    }
}
