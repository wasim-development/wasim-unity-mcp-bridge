using System;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace WasimDevelopment.UnityMcpBridge
{
    internal static class RuntimeInspection
    {
        private static readonly string[] PickupMembers = { "IsHeld", "currentPlayer", "currentHand", "CurrentHand",
            "pickupable", "DisallowTheft", "AutoHold", "proximity", "orientation" };
        private static readonly string[] SyncMembers = { "AllowCollisionOwnershipTransfer", "ForceKinematicOnRemote" };

        public static JObject Inspect(JObject args)
        {
            GameObject root = ObjectAccess.SelectedOrId(args);
            int max = ObjectAccess.Limit(args, "maximumObjects", 30, 1, 200);
            int properties = ObjectAccess.Limit(args, "maximumProperties", 80, 1, 300);
            var objects = new JArray();
            foreach (GameObject go in ObjectAccess.Walk(root, max))
            {
                var components = new JArray();
                foreach (Component component in go.GetComponents<Component>())
                {
                    if (component == null) { components.Add(new JObject { ["missingScript"] = true }); continue; }
                    var item = ObjectAccess.Reference(component);
                    try { item["runtime"] = ComponentValues(component, args["udonVariables"] as JArray); }
                    catch (Exception ex) { item["runtimeError"] = ex.GetBaseException().Message; }
                    if (args["includeSerialized"]?.Value<bool>() != false) item["serialized"] = ObjectAccess.Properties(component, properties);
                    components.Add(item);
                }
                JObject entry = ObjectAccess.Reference(go);
                entry["activeSelf"] = go.activeSelf;
                entry["activeInHierarchy"] = go.activeInHierarchy;
                entry["layer"] = go.layer;
                entry["components"] = components;
                if (args["includeSerialized"]?.Value<bool>() != false) entry["serialized"] = ObjectAccess.Properties(go, properties);
                if (EditorApplication.isPlaying)
                {
                    try { entry["owner"] = ObjectAccess.Value(ObjectAccess.Owner(go)); }
                    catch (Exception ex) { entry["ownerError"] = ex.GetBaseException().Message; }
                }
                objects.Add(entry);
            }
            return new JObject { ["isPlaying"] = EditorApplication.isPlaying, ["timestampUtc"] = DateTime.UtcNow.ToString("O"),
                ["scope"] = "Unity Editor / Play Mode / ClientSim; not a remote VRChat client",
                ["limitReached"] = objects.Count >= max, ["objects"] = objects };
        }

        public static JObject ComponentValues(Component component, JArray variables = null)
        {
            var result = new JObject();
            if (component is Transform t)
            {
                result["position"] = ObjectAccess.Value(t.position);
                result["rotation"] = ObjectAccess.Value(t.rotation);
                result["localPosition"] = ObjectAccess.Value(t.localPosition);
                result["localRotation"] = ObjectAccess.Value(t.localRotation);
                result["localScale"] = ObjectAccess.Value(t.localScale);
            }
            if (component is Rigidbody rb)
            {
                result["isKinematic"] = rb.isKinematic; result["useGravity"] = rb.useGravity;
                result["mass"] = rb.mass; result["velocity"] = ObjectAccess.Value(rb.velocity);
                result["angularVelocity"] = ObjectAccess.Value(rb.angularVelocity);
                result["isSleeping"] = rb.IsSleeping(); result["constraints"] = rb.constraints.ToString();
                result["collisionDetectionMode"] = rb.collisionDetectionMode.ToString();
            }
            if (component is Joint joint)
            {
                result["connectedBody"] = ObjectAccess.Value(joint.connectedBody);
                result["anchor"] = ObjectAccess.Value(joint.anchor);
                result["connectedAnchor"] = ObjectAccess.Value(joint.connectedAnchor);
                result["autoConfigureConnectedAnchor"] = joint.autoConfigureConnectedAnchor;
                result["breakForce"] = float.IsInfinity(joint.breakForce) ? (JToken)"Infinity" : joint.breakForce;
                result["enableCollision"] = joint.enableCollision;
            }
            if (component is Collider collider)
            {
                result["enabled"] = collider.enabled; result["isTrigger"] = collider.isTrigger;
                result["attachedRigidbody"] = ObjectAccess.Value(collider.attachedRigidbody);
                result["boundsCenter"] = ObjectAccess.Value(collider.bounds.center);
                result["boundsSize"] = ObjectAccess.Value(collider.bounds.size);
            }
            if (component is Animator animator)
            {
                result["enabled"] = animator.enabled;
                result["controller"] = ObjectAccess.Value(animator.runtimeAnimatorController);
                result["applyRootMotion"] = animator.applyRootMotion;
                if (EditorApplication.isPlaying && animator.isInitialized)
                {
                    var layers = new JArray();
                    for (int i = 0; i < Math.Min(animator.layerCount, 16); i++)
                    {
                        AnimatorStateInfo state = animator.GetCurrentAnimatorStateInfo(i);
                        layers.Add(new JObject { ["layer"] = i, ["stateHash"] = state.fullPathHash,
                            ["normalizedTime"] = state.normalizedTime, ["inTransition"] = animator.IsInTransition(i) });
                    }
                    result["layers"] = layers;
                }
            }
            string type = component.GetType().FullName ?? "";
            if (type.StartsWith("VRC.", StringComparison.Ordinal))
            {
                string[] members = type.EndsWith("Pickup", StringComparison.Ordinal) || type.EndsWith("VRC_Pickup", StringComparison.Ordinal)
                    ? PickupMembers : type.EndsWith("VRCObjectSync", StringComparison.Ordinal) ? SyncMembers : Array.Empty<string>();
                foreach (string name in members)
                {
                    try { result[name] = ObjectAccess.Value(ObjectAccess.Member(component, name)); }
                    catch (Exception ex) { result[name] = new JObject { ["error"] = ex.GetBaseException().Message }; }
                }
            }
            if (EditorApplication.isPlaying && type == "VRC.Udon.UdonBehaviour" && variables != null)
            {
                MethodInfo method = component.GetType().GetMethod("GetProgramVariable", new[] { typeof(string) });
                var values = new JObject();
                foreach (JToken variable in variables.Take(32))
                {
                    string name = variable.Value<string>();
                    if (string.IsNullOrWhiteSpace(name) || name.Length > 120) throw new ArgumentException("Invalid Udon variable name.");
                    values[name] = ObjectAccess.Value(method?.Invoke(component, new object[] { name }));
                }
                result["udonVariables"] = values;
            }
            return result;
        }
    }
}
