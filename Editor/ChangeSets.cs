using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace WasimDevelopment.UnityMcpBridge
{
    internal static class ChangeSets
    {
        private static string Root => Path.Combine(ProjectSecurity.ProjectRoot, "Library", "WasimUnityMcpBridge", "ChangeSets");
        private static string Index => Path.Combine(Root, "index.json");
        private static JArray _records;
        private static readonly HashSet<string> Kinds = new HashSet<string> {
            "set_property", "add_component", "create_gameobject", "create_script", "replace_script", "save_prefab" };

        private static JArray Records
        {
            get
            {
                if (_records != null) return _records;
                _records = File.Exists(Index) ? JArray.Parse(AtomicFile.ReadText(Index)) : new JArray();
                foreach (JObject record in _records)
                    if (record["status"]?.Value<string>() == "Applying")
                    {
                        record["status"] = "RecoveryRequired";
                        record["message"] = "Editor reloaded during apply. Inspect the recorded backups before recovery.";
                    }
                return _records;
            }
        }
        private static void Save() => AtomicFile.WriteText(Index, Records.ToString(Newtonsoft.Json.Formatting.Indented));
        public static string HashFile(string path) => File.Exists(path) ? HashBytes(File.ReadAllBytes(path)) : null;
        public static string HashText(string text) => HashBytes(new UTF8Encoding(false).GetBytes(text ?? ""));
        private static string HashBytes(byte[] bytes)
        { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }

        private static void Gate()
        {
            if (!BridgePreferences.EnableChangeSets) throw new InvalidOperationException("Enable reviewed change sets in the Unity bridge window.");
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating)
                throw new InvalidOperationException("Change sets require stable Edit Mode.");
        }

        private static Scene LoadedScene(string path)
        {
            Scene scene = SceneManager.GetSceneByPath(path);
            if (!scene.IsValid() || !scene.isLoaded || string.IsNullOrEmpty(scene.path))
                throw new InvalidOperationException("Save and load the target scene first: " + path);
            if (scene.isDirty) throw new InvalidOperationException("Save scene edits before proposing/applying: " + path);
            return scene;
        }

        private static void AddScene(JObject scenes, Scene scene)
        {
            LoadedScene(scene.path);
            scenes[scene.path] = HashFile(ProjectSecurity.ResolveAssetFile(scene.path, true));
        }

        private static Object ExistingTarget(string id, HashSet<string> aliases)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentException("targetId is required.");
            if (aliases.Contains(id)) return null;
            if (id.StartsWith("instance:", StringComparison.Ordinal)) throw new ArgumentException("Use a saved GlobalObjectId for change sets.");
            Object target = ObjectAccess.Resolve(id);
            GameObject go = ObjectAccess.GameObjectOf(target);
            if (go == null || EditorUtility.IsPersistent(target)) throw new ArgumentException("Target must be a loaded scene object or component.");
            if ((target.hideFlags & HideFlags.NotEditable) != 0) throw new ArgumentException("Target is not editable.");
            return target;
        }

        public static JObject Propose(JObject args)
        {
            Gate();
            string summary = args["summary"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(summary) || summary.Length > 2000) throw new ArgumentException("Provide a summary of 1-2000 characters.");
            JArray operations = args["operations"] as JArray;
            if (operations == null || operations.Count < 1 || operations.Count > 50) throw new ArgumentException("Provide 1-50 operations.");
            if (Encoding.UTF8.GetByteCount(operations.ToString()) > 800 * 1024) throw new ArgumentException("Change set exceeds 800 KB.");
            if (Records.OfType<JObject>().Count(r => (string)r["status"] == "Pending") >= 20)
                throw new InvalidOperationException("Review pending change sets first (maximum 20).");
            var sceneGuards = new JObject();
            var aliases = new HashSet<string>(StringComparer.Ordinal);
            var aliasScenes = new Dictionary<string, string>();
            var changedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var prepared = (JArray)operations.DeepClone();
            foreach (JObject op in prepared)
            {
                string kind = (string)op["kind"];
                if (!Kinds.Contains(kind ?? "")) throw new ArgumentException("Unsupported operation: " + kind);
                string alias = (string)op["alias"];
                if (alias != null && (kind != "create_gameobject" && kind != "add_component"))
                    throw new ArgumentException("Only object/component creation can define an alias.");
                if (alias != null && (!System.Text.RegularExpressions.Regex.IsMatch(alias, @"^\$[A-Za-z][A-Za-z0-9_]{0,39}$") || aliases.Contains(alias)))
                    throw new ArgumentException("Invalid or duplicate alias: " + alias);

                if (kind == "create_script" || kind == "replace_script" || kind == "save_prefab")
                {
                    string path = (string)op["path"];
                    string full = ProjectSecurity.ResolveAssetFile(path, true);
                    string extension = kind == "save_prefab" ? ".prefab" : ".cs";
                    if (!string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException("Expected extension " + extension);
                    if (!Directory.Exists(Path.GetDirectoryName(full))) throw new ArgumentException("Destination folder must already exist.");
                    if (!changedFiles.Add(path)) throw new ArgumentException("A file may be changed only once per set.");
                    if (kind == "replace_script")
                    {
                        if (!ProjectSecurity.TryResolveWritableAssetScript(path, out _, out string error)) throw new ArgumentException(error);
                        if ((string)op["expectedSha256"] != ScriptChangeManager.ComputeFileSha256(path)) throw new InvalidOperationException("Stale script hash: " + path);
                        op["beforeFileSha256"] = HashFile(full);
                    }
                    else if (File.Exists(full) || File.Exists(full + ".meta") || Directory.Exists(full))
                        throw new InvalidOperationException("Creation cannot overwrite an existing asset or its metadata: " + path);
                    if (kind != "save_prefab")
                    {
                        string content = (string)op["newContent"];
                        if (string.IsNullOrWhiteSpace(content) || Encoding.UTF8.GetByteCount(content) > ProjectSecurity.MaxScriptBytes)
                            throw new ArgumentException("Script content must contain 1-256 KB of UTF-8 text.");
                    }
                }
                if (kind == "create_gameobject")
                {
                    string name = (string)op["name"];
                    if (string.IsNullOrWhiteSpace(name) || name.Length > 120 || name.IndexOf('/') >= 0) throw new ArgumentException("Invalid GameObject name.");
                    string parent = (string)op["parentId"];
                    Object parentObject = string.IsNullOrEmpty(parent) ? null : ExistingTarget(parent, aliases);
                    string parentScene = parentObject != null ? ObjectAccess.GameObjectOf(parentObject).scene.path
                        : parent != null && aliasScenes.TryGetValue(parent, out string aliasScene) ? aliasScene : null;
                    string scenePath = parentScene ?? (string)op["scenePath"] ?? SceneManager.GetActiveScene().path;
                    if (parentScene != null && op["scenePath"] != null && (string)op["scenePath"] != parentScene)
                        throw new ArgumentException("Child and parent must belong to the same scene.");
                    op["scenePath"] = scenePath;
                    AddScene(sceneGuards, LoadedScene(scenePath));
                    string primitive = (string)op["primitive"];
                    if (primitive != null && (!Enum.TryParse(primitive, out PrimitiveType parsed) || !Enum.IsDefined(typeof(PrimitiveType), parsed)))
                        throw new ArgumentException("Invalid primitive type.");
                }
                if (kind == "set_property" || kind == "add_component" || kind == "save_prefab")
                {
                    string targetId = (string)op["targetId"];
                    Object target = ExistingTarget(targetId, aliases);
                    if (target != null) AddScene(sceneGuards, ObjectAccess.GameObjectOf(target).scene);
                    if (kind == "add_component")
                    {
                        Type type = ComponentType((string)op["componentType"]);
                        if (IsProxyType(type)) ProxyAddMethod(type);
                    }
                    if (kind == "set_property")
                    {
                        string propertyPath = (string)op["propertyPath"];
                        if (string.IsNullOrWhiteSpace(propertyPath) || propertyPath.Length > 300 || propertyPath == "m_Script")
                            throw new ArgumentException("Invalid or protected property path.");
                        if (op.Property("value") == null) throw new ArgumentException("set_property requires value, including explicit null.");
                        if (target != null)
                        {
                            using (var serialized = new SerializedObject(target))
                            {
                                SerializedProperty property = serialized.FindProperty(propertyPath);
                                if (property == null || !property.editable) throw new ArgumentException("Property is not editable: " + propertyPath);
                                ValidatePropertyType(property);
                                JToken actual = ObjectAccess.SerializedValue(property);
                                if (op.Property("expectedValue") != null && !JToken.DeepEquals(op["expectedValue"], actual))
                                    throw new InvalidOperationException("Property changed since inspection: " + propertyPath);
                                op["expectedValue"] = actual;
                            }
                            EnsureProxyAdapter(target);
                        }
                    }
                }
                if (alias != null)
                {
                    aliases.Add(alias);
                    if (kind == "create_gameobject") aliasScenes[alias] = (string)op["scenePath"];
                    else
                    {
                        string targetId = (string)op["targetId"];
                        aliasScenes[alias] = aliasScenes.TryGetValue(targetId, out string targetScene) ? targetScene
                            : ObjectAccess.GameObjectOf(ObjectAccess.Resolve(targetId)).scene.path;
                    }
                }
            }
            var record = new JObject { ["id"] = Guid.NewGuid().ToString("N").Substring(0, 12), ["summary"] = summary,
                ["createdUtc"] = DateTime.UtcNow.ToString("O"), ["status"] = "Pending", ["operations"] = prepared,
                ["sceneGuards"] = sceneGuards, ["kind"] = "Changes", ["message"] = "" };
            Records.Add(record); Save();
            return Summary(record, true);
        }

        private static Type ComponentType(string name)
        {
            Type type = ObjectAccess.FindType(name);
            if (!typeof(Component).IsAssignableFrom(type) || type.IsAbstract || type.ContainsGenericParameters
                || typeof(Transform).IsAssignableFrom(type) || (type.Namespace ?? "").StartsWith("UnityEditor", StringComparison.Ordinal))
                throw new ArgumentException("Type cannot be added as a runtime component: " + name);
            return type;
        }

        public static JArray List(bool completed, bool operations = false) => new JArray(Records.OfType<JObject>()
            .Where(r => completed || (string)r["status"] == "Pending" || (string)r["status"] == "RecoveryRequired")
            .Reverse().Take(100).Select(r => Summary(r, operations)));

        private static JObject Summary(JObject record, bool operations)
        {
            var result = new JObject { ["id"] = record["id"], ["kind"] = record["kind"], ["summary"] = record["summary"],
                ["createdUtc"] = record["createdUtc"], ["status"] = record["status"], ["message"] = record["message"],
                ["operationCount"] = (record["operations"] as JArray)?.Count ?? 0,
                ["requiresUnityApproval"] = (string)record["status"] == "Pending",
                ["rollbackOf"] = record["rollbackOf"], ["createdObjects"] = record["createdObjects"]?.DeepClone(), ["scenes"] = new JArray((record["sceneGuards"] as JObject)?.Properties().Select(p => p.Name) ?? Enumerable.Empty<string>()) };
            if (operations) result["operations"] = record["operations"]?.DeepClone();
            return result;
        }
        private static JObject Find(string id) => Records.OfType<JObject>().FirstOrDefault(r => (string)r["id"] == id)
            ?? throw new ArgumentException("Unknown change set.");

        public static void Reject(string id)
        {
            JObject record = Find(id);
            if ((string)record["status"] != "Pending") throw new InvalidOperationException("Only pending sets can be rejected.");
            record["status"] = "Rejected"; Save();
        }

        private static void CheckGuards(JObject record)
        {
            foreach (JProperty guard in ((JObject)record["sceneGuards"]).Properties())
            {
                LoadedScene(guard.Name);
                if (HashFile(ProjectSecurity.ResolveAssetFile(guard.Name, true)) != (string)guard.Value)
                    throw new InvalidOperationException("Scene changed since proposal: " + guard.Name);
            }
            var aliases = new HashSet<string>();
            foreach (JObject op in (JArray)record["operations"])
            {
                string kind = (string)op["kind"];
                if (kind == "replace_script")
                {
                    string full = ProjectSecurity.ResolveAssetFile((string)op["path"], true);
                    if (HashFile(full) != (string)op["beforeFileSha256"]) throw new InvalidOperationException("Script changed since proposal.");
                }
                else if (kind == "create_script" || kind == "save_prefab")
                {
                    string full = ProjectSecurity.ResolveAssetFile((string)op["path"], true);
                    if (File.Exists(full) || File.Exists(full + ".meta")) throw new InvalidOperationException("Creation destination now exists.");
                }
                if (kind == "set_property" && !aliases.Contains((string)op["targetId"]))
                {
                    Object target = ExistingTarget((string)op["targetId"], aliases);
                    using (var serialized = new SerializedObject(target))
                    {
                        SerializedProperty property = serialized.FindProperty((string)op["propertyPath"]);
                        if (property == null || !JToken.DeepEquals(op["expectedValue"], ObjectAccess.SerializedValue(property)))
                            throw new InvalidOperationException("Target property changed since proposal.");
                    }
                }
                string alias = (string)op["alias"];
                if (alias != null) aliases.Add(alias);
            }
        }

        public static void Approve(string id)
        {
            Gate();
            JObject record = Find(id);
            if ((string)record["status"] != "Pending") throw new InvalidOperationException("Set is not pending.");
            if ((string)record["kind"] == "Rollback") { ApplyRollback(record); return; }
            CheckGuards(record);
            string backupRoot = Path.Combine(Root, id);
            var files = new JArray();
            var filePaths = new HashSet<string>();
            foreach (JProperty scene in ((JObject)record["sceneGuards"]).Properties()) filePaths.Add(scene.Name);
            foreach (JObject op in (JArray)record["operations"])
                if (op["path"] != null)
                { filePaths.Add((string)op["path"]); filePaths.Add((string)op["path"] + ".meta"); }
            foreach (string path in filePaths)
            {
                string full = ProjectSecurity.ResolveAssetFile(path, true);
                string backup = Path.Combine(backupRoot, path.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(full))
                { Directory.CreateDirectory(Path.GetDirectoryName(backup)); File.Copy(full, backup, false); }
                files.Add(new JObject { ["path"] = path, ["beforeSha256"] = HashFile(full), ["backup"] = backup });
            }
            record["files"] = files; record["status"] = "Applying"; Save();
            int group;
            Undo.IncrementCurrentGroup(); group = Undo.GetCurrentGroup(); Undo.SetCurrentGroupName("Wasim MCP: " + (string)record["summary"]);
            var aliases = new Dictionary<string, Object>();
            AssetDatabase.DisallowAutoRefresh();
            try
            {
                foreach (JObject op in (JArray)record["operations"]) ApplyOperation(op, aliases);
                foreach (JProperty scene in ((JObject)record["sceneGuards"]).Properties())
                    if (!EditorSceneManager.SaveScene(SceneManager.GetSceneByPath(scene.Name)))
                        throw new IOException("Could not save scene: " + scene.Name);
                foreach (JObject file in files) file["afterSha256"] = HashFile(ProjectSecurity.ResolveAssetFile((string)file["path"], true));
                record["status"] = "Applied";
                record["message"] = "Applied through Unity approval. Backups: " + backupRoot;
                record["appliedUtc"] = DateTime.UtcNow.ToString("O");
                record["createdObjects"] = new JObject(aliases.Select(p => new JProperty(p.Key, ObjectAccess.Reference(p.Value))));
                Save();
                Undo.CollapseUndoOperations(group);
            }
            catch (Exception ex)
            {
                Undo.RevertAllDownToGroup(group);
                try
                {
                    RestoreFiles(files);
                    ReloadScenes((JObject)record["sceneGuards"]);
                    record["status"] = "FailedRolledBack";
                    record["message"] = ex.GetBaseException().Message;
                }
                catch (Exception rollbackError)
                {
                    record["status"] = "RecoveryRequired";
                    record["message"] = ex.Message + "; rollback error: " + rollbackError.GetBaseException().Message;
                }
                Save();
                throw new InvalidOperationException((string)record["message"], ex);
            }
            finally { AssetDatabase.AllowAutoRefresh(); AssetDatabase.Refresh(); }
        }

        private static void ApplyOperation(JObject op, Dictionary<string, Object> aliases)
        {
            string kind = (string)op["kind"];
            Object created = null;
            if (kind == "create_script" || kind == "replace_script")
            {
                string full = ProjectSecurity.ResolveAssetFile((string)op["path"], true);
                AtomicFile.WriteText(full, (string)op["newContent"]);
                if (kind == "create_script")
                    AtomicFile.WriteText(full + ".meta", "fileFormatVersion: 2\nguid: " + Guid.NewGuid().ToString("N")
                        + "\nMonoImporter:\n  externalObjects: {}\n  serializedVersion: 2\n  defaultReferences: []\n  executionOrder: 0\n  icon: {instanceID: 0}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n");
            }
            else if (kind == "create_gameobject")
            {
                string primitive = (string)op["primitive"];
                GameObject go = primitive == null ? new GameObject((string)op["name"]) : GameObject.CreatePrimitive((PrimitiveType)Enum.Parse(typeof(PrimitiveType), primitive));
                go.name = (string)op["name"];
                Undo.RegisterCreatedObjectUndo(go, "Create " + go.name);
                SceneManager.MoveGameObjectToScene(go, SceneManager.GetSceneByPath((string)op["scenePath"]));
                string parent = (string)op["parentId"];
                if (parent != null) Undo.SetTransformParent(go.transform, ObjectAccess.GameObjectOf(ObjectAccess.Resolve(parent, aliases)).transform, "Set parent");
                created = go;
            }
            else
            {
                Object target = ObjectAccess.Resolve((string)op["targetId"], aliases);
                GameObject go = ObjectAccess.GameObjectOf(target);
                if (go == null || EditorUtility.IsPersistent(target)) throw new InvalidOperationException("Target must be in a scene.");
                if (kind == "add_component")
                {
                    Type type = ComponentType((string)op["componentType"]);
                    if (IsProxyType(type))
                    {
                        Component[] before = go.GetComponents<Component>();
                        var method = ProxyAddMethod(type);
                        object[] parameters = method.GetParameters().Select(p => p.DefaultValue).ToArray();
                        parameters[0] = go;
                        if (parameters.Length > 1 && method.GetParameters()[1].ParameterType == typeof(Type)) parameters[1] = type;
                        created = method.Invoke(null, parameters) as Component;
                        foreach (Component component in go.GetComponents<Component>().Except(before))
                            if (component != null) Undo.RegisterCreatedObjectUndo(component, "Add UdonSharp component");
                    }
                    else created = Undo.AddComponent(go, type);
                    if (created == null) throw new InvalidOperationException("Unity refused to add the component.");
                    EnsureProxyAdapter(created); CopyProxy(created);
                }
                else if (kind == "save_prefab")
                {
                    var prefab = PrefabUtility.SaveAsPrefabAsset(go, (string)op["path"], out bool success);
                    if (!success || prefab == null) throw new IOException("Could not create prefab.");
                }
                else if (kind == "set_property")
                {
                    using (var serialized = new SerializedObject(target))
                    {
                        SerializedProperty property = serialized.FindProperty((string)op["propertyPath"]);
                        if (property == null || !property.editable) throw new ArgumentException("Property unavailable: " + (string)op["propertyPath"]);
                        ValidatePropertyType(property);
                        Undo.RecordObject(target, "Set " + property.propertyPath);
                        WriteProperty(property, op["value"], aliases);
                        serialized.ApplyModifiedProperties();
                    }
                    CopyProxy(target);
                    PrefabUtility.RecordPrefabInstancePropertyModifications(target);
                }
                EditorSceneManager.MarkSceneDirty(go.scene);
            }
            string alias = (string)op["alias"];
            if (alias != null && created != null) aliases.Add(alias, created);
        }

        private static void ValidatePropertyType(SerializedProperty property)
        {
            if (property.propertyPath == "m_Script" || property.propertyType == SerializedPropertyType.Generic
                || property.propertyType == SerializedPropertyType.ManagedReference || property.isArray && property.propertyType != SerializedPropertyType.String)
                throw new ArgumentException("Use an individual supported property or array element; whole arrays/managed references/script identity are protected.");
            switch (property.propertyType)
            {
                case SerializedPropertyType.Boolean: case SerializedPropertyType.Integer: case SerializedPropertyType.Float:
                case SerializedPropertyType.String: case SerializedPropertyType.Enum: case SerializedPropertyType.ObjectReference:
                case SerializedPropertyType.Vector2: case SerializedPropertyType.Vector3: case SerializedPropertyType.Vector4:
                case SerializedPropertyType.Quaternion: case SerializedPropertyType.Color: return;
                default: throw new ArgumentException("Property type is not supported for edits: " + property.propertyType);
            }
        }
        private static float Number(JToken value, string key)
        {
            if (value?[key] == null) throw new ArgumentException("Missing numeric coordinate " + key);
            float result = value[key].Value<float>();
            if (float.IsNaN(result) || float.IsInfinity(result)) throw new ArgumentException("Coordinate must be finite.");
            return result;
        }
        private static void WriteProperty(SerializedProperty p, JToken value, IDictionary<string, Object> aliases)
        {
            switch (p.propertyType)
            {
                case SerializedPropertyType.Boolean:
                    if (value.Type != JTokenType.Boolean) throw new ArgumentException("Expected boolean.");
                    p.boolValue = value.Value<bool>(); break;
                case SerializedPropertyType.Integer:
                    if (value.Type != JTokenType.Integer) throw new ArgumentException("Expected integer.");
                    p.longValue = value.Value<long>(); break;
                case SerializedPropertyType.Float:
                    if (value.Type != JTokenType.Float && value.Type != JTokenType.Integer) throw new ArgumentException("Expected number.");
                    double number = value.Value<double>();
                    if (double.IsNaN(number) || double.IsInfinity(number)) throw new ArgumentException("Expected finite number.");
                    p.doubleValue = number; break;
                case SerializedPropertyType.String:
                    if (value.Type != JTokenType.String) throw new ArgumentException("Expected string.");
                    p.stringValue = value.Value<string>(); break;
                case SerializedPropertyType.Enum:
                    int index = value.Type == JTokenType.String ? Array.IndexOf(p.enumNames, value.Value<string>()) : value.Value<int>();
                    if (index < 0 || index >= p.enumNames.Length) throw new ArgumentException("Invalid enum value.");
                    p.enumValueIndex = index; break;
                case SerializedPropertyType.ObjectReference:
                    Object reference = value.Type == JTokenType.Null ? null : ObjectAccess.Resolve(value["objectId"]?.Value<string>(), aliases);
                    p.objectReferenceValue = reference;
                    if (p.objectReferenceValue != reference) throw new ArgumentException("Reference type is incompatible with the property.");
                    break;
                case SerializedPropertyType.Vector2: p.vector2Value = new Vector2(Number(value, "x"), Number(value, "y")); break;
                case SerializedPropertyType.Vector3: p.vector3Value = new Vector3(Number(value, "x"), Number(value, "y"), Number(value, "z")); break;
                case SerializedPropertyType.Vector4: p.vector4Value = new Vector4(Number(value, "x"), Number(value, "y"), Number(value, "z"), Number(value, "w")); break;
                case SerializedPropertyType.Quaternion: p.quaternionValue = new Quaternion(Number(value, "x"), Number(value, "y"), Number(value, "z"), Number(value, "w")); break;
                case SerializedPropertyType.Color: p.colorValue = new Color(Number(value, "r"), Number(value, "g"), Number(value, "b"), Number(value, "a")); break;
            }
        }

        private static bool IsProxy(Object target) => IsProxyType(target.GetType());
        private static bool IsProxyType(Type type)
        {
            for (Type t = type; t != null; t = t.BaseType)
                if (t.FullName == "UdonSharp.UdonSharpBehaviour") return true;
            return false;
        }
        private static System.Reflection.MethodInfo ProxyAddMethod(Type type)
        {
            Type utility = ObjectAccess.FindType("UdonSharpEditor.UdonSharpEditorUtility");
            foreach (var method in utility.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
            {
                if (method.Name != "AddUdonSharpComponent") continue;
                var parameters = method.GetParameters();
                if (parameters.Length == 0 || parameters[0].ParameterType != typeof(GameObject)) continue;
                if (method.IsGenericMethodDefinition && method.GetGenericArguments().Length == 1 && parameters.Skip(1).All(p => p.IsOptional))
                    return method.MakeGenericMethod(type);
                if (!method.IsGenericMethod && parameters.Length >= 2 && parameters[1].ParameterType == typeof(Type)
                    && parameters.Skip(2).All(p => p.IsOptional)) return method;
            }
            throw new InvalidOperationException("Installed UdonSharp lacks a supported AddUdonSharpComponent adapter.");
        }

        private static System.Reflection.MethodInfo ProxyMethod(Object target)
        {
            if (!IsProxy(target)) return null;
            Type utility = ObjectAccess.FindType("UdonSharpEditor.UdonSharpEditorUtility");
            var method = utility.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "CopyProxyToUdon" && m.GetParameters().Length >= 1
                    && m.GetParameters()[0].ParameterType.IsInstanceOfType(target) && m.GetParameters().Skip(1).All(p => p.IsOptional));
            if (method == null) throw new InvalidOperationException("Installed UdonSharp lacks the supported CopyProxyToUdon adapter.");
            return method;
        }
        private static void EnsureProxyAdapter(Object target) { ProxyMethod(target); }
        private static void CopyProxy(Object target)
        {
            var method = ProxyMethod(target);
            if (method == null) return;
            object[] values = method.GetParameters().Select(p => p.DefaultValue).ToArray(); values[0] = target;
            method.Invoke(null, values);
        }

        public static JObject ProposeRollback(string id, string summary)
        {
            Gate();
            JObject source = Find(id);
            if ((string)source["status"] != "Applied" || (string)source["kind"] != "Changes")
                throw new InvalidOperationException("Only an applied change set can be rolled back.");
            if (Records.OfType<JObject>().Any(r => (string)r["rollbackOf"] == id && (string)r["status"] == "Pending"))
                throw new InvalidOperationException("A rollback proposal already exists.");
            CheckPostFiles(source);
            var record = new JObject { ["id"] = Guid.NewGuid().ToString("N").Substring(0, 12), ["kind"] = "Rollback",
                ["rollbackOf"] = id, ["summary"] = string.IsNullOrWhiteSpace(summary) ? "Restore before " + id : summary,
                ["status"] = "Pending", ["createdUtc"] = DateTime.UtcNow.ToString("O"),
                ["sceneGuards"] = source["sceneGuards"].DeepClone(), ["operations"] = new JArray(), ["message"] = "" };
            Records.Add(record); Save(); return Summary(record, true);
        }
        private static void CheckPostFiles(JObject source)
        {
            foreach (JObject file in (JArray)source["files"])
                if (HashFile(ProjectSecurity.ResolveAssetFile((string)file["path"], true)) != (string)file["afterSha256"])
                    throw new InvalidOperationException("Rollback blocked: asset changed since apply: " + (string)file["path"]);
            if (((JObject)source["sceneGuards"]).HasValues)
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    Scene scene = SceneManager.GetSceneAt(i);
                    if (scene.isDirty || string.IsNullOrEmpty(scene.path)) throw new InvalidOperationException("Save all scenes before rollback.");
                }
        }
        private static void ApplyRollback(JObject rollback)
        {
            JObject source = Find((string)rollback["rollbackOf"]); CheckPostFiles(source);
            // Validate every backup before touching any asset.
            foreach (JObject file in (JArray)source["files"])
                if (file["beforeSha256"].Type != JTokenType.Null && HashFile((string)file["backup"]) != (string)file["beforeSha256"])
                    throw new IOException("Missing or corrupted backup: " + (string)file["path"]);
            rollback["status"] = "Applying"; Save();
            AssetDatabase.DisallowAutoRefresh();
            try
            {
                RestoreFiles((JArray)source["files"]);
                ReloadScenes((JObject)source["sceneGuards"]);
                source["status"] = "RolledBack"; rollback["status"] = "Applied";
                rollback["message"] = "Restored the recorded pre-change files."; Save();
            }
            catch (Exception ex) { rollback["status"] = "RecoveryRequired"; rollback["message"] = ex.Message; Save(); throw; }
            finally { AssetDatabase.AllowAutoRefresh(); AssetDatabase.Refresh(); }
        }
        private static void RestoreFiles(JArray files)
        {
            foreach (JObject file in files)
            {
                string full = ProjectSecurity.ResolveAssetFile((string)file["path"], true);
                if (file["beforeSha256"].Type == JTokenType.Null) { if (File.Exists(full)) File.Delete(full); }
                else AtomicFile.WriteBytes(full, File.ReadAllBytes((string)file["backup"]));
            }
        }
        private static void ReloadScenes(JObject affected)
        {
            if (!affected.HasValues) return;
            SceneSetup[] setup = EditorSceneManager.GetSceneManagerSetup();
            // Never discard unrelated unsaved scenes while refreshing restored scene bytes.
            foreach (SceneSetup item in setup)
            {
                Scene scene = SceneManager.GetSceneByPath(item.path);
                if (scene.IsValid() && scene.isDirty && affected.Property(item.path) == null)
                    throw new InvalidOperationException("Unrelated scene became dirty; reopen restored scenes manually.");
            }
            string active = setup.FirstOrDefault(s => s.isActive)?.path;
            bool first = true;
            foreach (SceneSetup item in setup.Where(s => s.isLoaded))
            {
                if (string.IsNullOrEmpty(item.path)) throw new InvalidOperationException("Cannot reload an unsaved scene.");
                EditorSceneManager.OpenScene(item.path, first ? OpenSceneMode.Single : OpenSceneMode.Additive);
                first = false;
            }
            foreach (SceneSetup item in setup.Where(s => !s.isLoaded))
                EditorSceneManager.OpenScene(item.path, OpenSceneMode.AdditiveWithoutLoading);
            if (!string.IsNullOrEmpty(active)) SceneManager.SetActiveScene(SceneManager.GetSceneByPath(active));
        }
    }
}
