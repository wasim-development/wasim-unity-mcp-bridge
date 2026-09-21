using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace WasimDevelopment.UnityMcpBridge
{
    internal static class ObjectAccess
    {
        public static int Limit(JObject args, string key, int fallback, int min, int max)
            => Mathf.Clamp(args[key]?.Value<int>() ?? fallback, min, max);

        public static Object Resolve(string id, IDictionary<string, Object> aliases = null)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentException("objectId is required.");
            if (id.StartsWith("$", StringComparison.Ordinal) && aliases != null && aliases.TryGetValue(id, out Object alias)) return alias;
            if (id.StartsWith("instance:", StringComparison.Ordinal) && int.TryParse(id.Substring(9), out int instance))
            {
                Object result = EditorUtility.InstanceIDToObject(instance);
                if (result != null) return result;
            }
            if (GlobalObjectId.TryParse(id, out GlobalObjectId global))
            {
                Object result = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(global);
                if (result != null) return result;
            }
            throw new ArgumentException("Object not found or scene is not loaded: " + id);
        }

        public static GameObject GameObjectOf(Object value)
            => value is GameObject go ? go : value is Component component ? component.gameObject : null;

        public static string Id(Object value)
        {
            if (value == null) return null;
            var id = GlobalObjectId.GetGlobalObjectIdSlow(value);
            // Dynamic Play Mode / unsaved objects have no useful persistent identifier.
            if (id.targetObjectId == 0 || id.identifierType == 0) return "instance:" + value.GetInstanceID();
            return id.ToString();
        }

        public static JObject Reference(Object value)
        {
            if (value == null) return null;
            return new JObject { ["objectId"] = Id(value), ["instanceId"] = value.GetInstanceID(),
                ["name"] = value.name, ["type"] = value.GetType().FullName, ["assetPath"] = AssetDatabase.GetAssetPath(value) };
        }

        public static GameObject SelectedOrId(JObject args)
        {
            string id = args["objectId"]?.Value<string>();
            GameObject go = string.IsNullOrEmpty(id) ? Selection.activeGameObject : GameObjectOf(Resolve(id));
            if (go == null) throw new ArgumentException("Select a GameObject or supply its objectId.");
            return go;
        }

        public static IEnumerable<GameObject> Walk(GameObject root, int max)
        {
            var queue = new Queue<Transform>();
            queue.Enqueue(root.transform);
            int count = 0;
            while (queue.Count > 0 && count++ < max)
            {
                Transform item = queue.Dequeue();
                yield return item.gameObject;
                for (int i = 0; i < item.childCount; i++) queue.Enqueue(item.GetChild(i));
            }
        }

        public static Type FindType(string name)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("A fully qualified type name is required.");
            Type[] matches = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name, false)).Where(t => t != null).ToArray();
            if (matches.Length != 1) throw new ArgumentException("Type is unavailable or ambiguous: " + name);
            return matches[0];
        }

        public static object Member(object value, string name)
        {
            if (value == null) return null;
            Type type = value.GetType();
            var flags = BindingFlags.Public | BindingFlags.Instance;
            FieldInfo field = type.GetField(name, flags);
            if (field != null) return field.GetValue(value);
            PropertyInfo property = type.GetProperty(name, flags);
            return property != null && property.GetIndexParameters().Length == 0 && property.CanRead
                ? property.GetValue(value) : null;
        }

        public static JToken Value(object value)
        {
            if (value == null) return JValue.CreateNull();
            if (value is Object obj) return (JToken)Reference(obj) ?? JValue.CreateNull();
            if (value is Vector2 v2) return new JObject { ["x"] = v2.x, ["y"] = v2.y };
            if (value is Vector3 v) return new JObject { ["x"] = v.x, ["y"] = v.y, ["z"] = v.z };
            if (value is Vector4 v4) return new JObject { ["x"] = v4.x, ["y"] = v4.y, ["z"] = v4.z, ["w"] = v4.w };
            if (value is Quaternion q) return new JObject { ["x"] = q.x, ["y"] = q.y, ["z"] = q.z, ["w"] = q.w };
            if (value is Color c) return new JObject { ["r"] = c.r, ["g"] = c.g, ["b"] = c.b, ["a"] = c.a };
            if (value is Enum) return new JValue(value.ToString());
            if (value is string || value is bool || value.GetType().IsPrimitive || value is decimal) return new JValue(value);
            // Explicitly bounded player metadata; never recursively reflect arbitrary SDK objects.
            if (value.GetType().FullName == "VRC.SDKBase.VRCPlayerApi")
                return new JObject { ["playerId"] = Convert.ToInt32(Member(value, "playerId") ?? -1),
                    ["displayName"] = Convert.ToString(Member(value, "displayName")),
                    ["isLocal"] = Convert.ToBoolean(Member(value, "isLocal") ?? false) };
            return new JObject { ["type"] = value.GetType().FullName, ["note"] = "Complex runtime value omitted." };
        }

        public static object Owner(GameObject go)
        {
            Type type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("VRC.SDKBase.Networking", false)).FirstOrDefault(t => t != null);
            return type?.GetMethod("GetOwner", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(GameObject) }, null)?.Invoke(null, new object[] { go });
        }

        public static JToken SerializedValue(SerializedProperty property)
        {
            switch (property.propertyType)
            {
                case SerializedPropertyType.Boolean: return property.boolValue;
                case SerializedPropertyType.Integer: return property.longValue;
                case SerializedPropertyType.Float: return property.doubleValue;
                case SerializedPropertyType.String: return property.stringValue;
                case SerializedPropertyType.Enum: return property.enumValueIndex;
                case SerializedPropertyType.ObjectReference: return (JToken)Reference(property.objectReferenceValue) ?? JValue.CreateNull();
                case SerializedPropertyType.Vector2: return Value(property.vector2Value);
                case SerializedPropertyType.Vector3: return Value(property.vector3Value);
                case SerializedPropertyType.Vector4: return Value(property.vector4Value);
                case SerializedPropertyType.Quaternion: return Value(property.quaternionValue);
                case SerializedPropertyType.Color: return Value(property.colorValue);
                default: return new JObject { ["propertyType"] = property.propertyType.ToString(),
                    ["arraySize"] = property.isArray ? property.arraySize : 0, ["editable"] = false };
            }
        }

        public static JArray Properties(Object target, int maximum)
        {
            var result = new JArray();
            using (var serialized = new SerializedObject(target))
            {
                SerializedProperty it = serialized.GetIterator();
                while (result.Count < maximum && it.NextVisible(true))
                    result.Add(new JObject { ["path"] = it.propertyPath, ["type"] = it.propertyType.ToString(),
                        ["value"] = SerializedValue(it), ["editable"] = it.editable });
            }
            return result;
        }
    }
}
