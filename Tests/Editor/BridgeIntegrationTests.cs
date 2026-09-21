using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace WasimDevelopment.UnityMcpBridge.Tests
{
    public sealed class BridgeIntegrationTests
    {
        private string _folder, _scenePath;
        private GameObject _root;
        private bool _oldPermission, _restoreEmpty;
        private string _originalActive;
        private readonly List<string> _proposals = new List<string>();

        [SetUp]
        public void SetUp()
        {
            _oldPermission = BridgePreferences.EnableChangeSets;
            _folder = null; _scenePath = null; _restoreEmpty = false;
            _originalActive = SceneManager.GetActiveScene().path;
            if (SceneManager.sceneCount == 1)
            {
                Scene initial = SceneManager.GetSceneAt(0);
                _restoreEmpty = string.IsNullOrEmpty(initial.path) && !initial.isDirty && initial.rootCount == 0;
            }
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (!_restoreEmpty && (scene.isDirty || string.IsNullOrEmpty(scene.path))) Assert.Ignore("Save all open scenes before bridge integration tests.");
            }
            BridgePreferences.EnableChangeSets = true;
            _folder = "Assets/WasimMcpTest_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(_folder));
            Scene test = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, _restoreEmpty ? NewSceneMode.Single : NewSceneMode.Additive);
            _root = new GameObject("BridgeTestRoot");
            SceneManager.MoveGameObjectToScene(_root, test);
            _scenePath = _folder + "/Test.unity";
            Assert.IsTrue(EditorSceneManager.SaveScene(test, _scenePath));
        }

        [TearDown]
        public void TearDown()
        {
            foreach (string id in _proposals)
                try { ChangeSets.Reject(id); } catch (InvalidOperationException) { }
            _proposals.Clear();
            if (!string.IsNullOrEmpty(_scenePath))
            {
                Scene scene = SceneManager.GetSceneByPath(_scenePath);
                if (_restoreEmpty) EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                else if (scene.IsValid()) EditorSceneManager.CloseScene(scene, true);
                if (!string.IsNullOrEmpty(_originalActive))
                {
                    Scene original = SceneManager.GetSceneByPath(_originalActive);
                    if (original.IsValid() && original.isLoaded) SceneManager.SetActiveScene(original);
                }
            }
            if (!string.IsNullOrEmpty(_folder)) AssetDatabase.DeleteAsset(_folder);
            BridgePreferences.EnableChangeSets = _oldPermission;
        }
        private JObject Propose(params JObject[] operations)
        {
            JObject record = ChangeSets.Propose(new JObject { ["summary"] = "Bridge integration test", ["operations"] = new JArray(operations) });
            _proposals.Add((string)record["id"]); return record;
        }

        [Test]
        public void ComponentChangeCanBeAppliedAndRolledBack()
        {
            var body = _root.AddComponent<Rigidbody>(); body.isKinematic = false;
            EditorSceneManager.MarkSceneDirty(_root.scene); EditorSceneManager.SaveScene(_root.scene);
            JObject set = Propose(new JObject { ["kind"] = "set_property", ["targetId"] = ObjectAccess.Id(body),
                ["propertyPath"] = "m_IsKinematic", ["value"] = true });
            Assert.IsFalse(body.isKinematic);
            ChangeSets.Approve((string)set["id"]); Assert.IsTrue(body.isKinematic);
            JObject rollback = ChangeSets.ProposeRollback((string)set["id"], "Test rollback");
            _proposals.Add((string)rollback["id"]); ChangeSets.Approve((string)rollback["id"]);
            GameObject restored = SceneManager.GetSceneByPath(_scenePath).GetRootGameObjects().Single(g => g.name == "BridgeTestRoot");
            Assert.IsFalse(restored.GetComponent<Rigidbody>().isKinematic);
        }

        [Test]
        public void ASceneChangedAfterProposalBlocksApply()
        {
            JObject set = Propose(new JObject { ["kind"] = "set_property", ["targetId"] = ObjectAccess.Id(_root),
                ["propertyPath"] = "m_Layer", ["value"] = 5 });
            _root.name = "EditedByUser"; EditorSceneManager.MarkSceneDirty(_root.scene); EditorSceneManager.SaveScene(_root.scene);
            Assert.Throws<InvalidOperationException>(() => ChangeSets.Approve((string)set["id"]));
            Assert.AreEqual(0, _root.layer);
        }

        [Test]
        public void AliasesCreateObjectAndComponentTogether()
        {
            JObject set = Propose(
                new JObject { ["kind"] = "create_gameobject", ["name"] = "Created", ["parentId"] = ObjectAccess.Id(_root), ["alias"] = "$child" },
                new JObject { ["kind"] = "add_component", ["targetId"] = "$child", ["componentType"] = "UnityEngine.Rigidbody", ["alias"] = "$body" },
                new JObject { ["kind"] = "set_property", ["targetId"] = "$body", ["propertyPath"] = "m_UseGravity", ["value"] = false });
            ChangeSets.Approve((string)set["id"]);
            var child = _root.transform.Find("Created");
            Assert.NotNull(child); Assert.IsFalse(child.GetComponent<Rigidbody>().useGravity);
        }

        [Test]
        public void FailureAfterCreationRestoresScene()
        {
            JObject set = Propose(
                new JObject { ["kind"] = "create_gameobject", ["name"] = "MustDisappear", ["parentId"] = ObjectAccess.Id(_root), ["alias"] = "$child" },
                new JObject { ["kind"] = "set_property", ["targetId"] = "$child", ["propertyPath"] = "NoSuchProperty", ["value"] = true });
            Assert.Throws<InvalidOperationException>(() => ChangeSets.Approve((string)set["id"]));
            GameObject restored = SceneManager.GetSceneByPath(_scenePath).GetRootGameObjects().Single(g => g.name == "BridgeTestRoot");
            Assert.IsNull(restored.transform.Find("MustDisappear"));
        }

        [Test]
        public void JointInspectorReportsConnectedBodyAndKinematicState()
        {
            _root.AddComponent<Rigidbody>();
            var other = new GameObject("OtherBody"); SceneManager.MoveGameObjectToScene(other, _root.scene);
            Rigidbody connected = other.AddComponent<Rigidbody>(); connected.isKinematic = true;
            FixedJoint joint = _root.AddComponent<FixedJoint>(); joint.connectedBody = connected;
            JObject state = RuntimeInspection.ComponentValues(joint);
            Assert.AreEqual(ObjectAccess.Id(connected), (string)state["connectedBody"]["objectId"]);
            Assert.IsTrue(RuntimeInspection.ComponentValues(connected)["isKinematic"].Value<bool>());
        }

        [Test]
        public void PackageScriptReaderResolvesInstalledPackage()
        {
            bool old = BridgePreferences.AllowPackageScripts;
            try
            {
                BridgePreferences.AllowPackageScripts = true;
                Assert.IsTrue(ProjectSecurity.TryResolveReadableScript("Packages/com.wasimdevelopment.unity-mcp-bridge/Editor/BridgeVersion.cs",
                    out string path, out string error), error);
                StringAssert.Contains("0.6.0", File.ReadAllText(path));
                Assert.IsFalse(ProjectSecurity.TryResolveReadableScript("Assets/../Library/hidden.cs", out _, out _));
            }
            finally { BridgePreferences.AllowPackageScripts = old; }
        }
    }
}
