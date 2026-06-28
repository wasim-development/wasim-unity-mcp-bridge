using UnityEditor;
using UnityEditor.Compilation;

namespace WasimDevelopment.UnityMcpBridge
{
    [InitializeOnLoad]
    internal static class BridgeBootstrap
    {
        static BridgeBootstrap()
        {
            CompanionIpc.Initialize();
            AssemblyReloadEvents.beforeAssemblyReload -= BeforeAssemblyReload;
            AssemblyReloadEvents.beforeAssemblyReload += BeforeAssemblyReload;
            EditorApplication.quitting -= OnQuitting;
            EditorApplication.quitting += OnQuitting;
            CompilationPipeline.compilationStarted -= OnCompilationStarted;
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.compilationFinished -= OnCompilationFinished;
            CompilationPipeline.compilationFinished += OnCompilationFinished;

            if (BridgePreferences.AutoStart)
                EditorApplication.delayCall += TryAutoStart;
        }

        public static void StartRequested()
        {
            CompanionManager.Start();
        }

        public static void StopRequested()
        {
            CompanionManager.Stop();
        }

        public static void RestartRequested()
        {
            CompanionManager.Restart();
        }

        private static void TryAutoStart()
        {
            try { CompanionManager.Start(); }
            catch (System.Exception ex) { UnityEngine.Debug.LogError("WDMCP companion auto-start failed: " + ex.Message); }
        }

        private static void OnCompilationStarted(object context)
        {
            CompanionIpc.WriteUnityStatus("compiling");
        }

        private static void OnCompilationFinished(object context)
        {
            CompanionIpc.WriteUnityStatus("compilation-finished");
        }

        private static void BeforeAssemblyReload()
        {
            // Deliberately do not stop the companion. It owns the MCP socket and ngrok
            // outside Unity's reloadable AppDomain and remains alive through this reload.
            CompanionIpc.WriteUnityStatus("reloading");
        }

        private static void OnQuitting()
        {
            CompanionIpc.WriteUnityStatus("quitting");
            if (BridgePreferences.StopCompanionWithUnity)
                CompanionManager.Stop();
        }
    }
}
