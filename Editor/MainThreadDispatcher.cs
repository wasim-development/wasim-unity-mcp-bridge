using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace WasimDevelopment.UnityMcpBridge
{
    [InitializeOnLoad]
    internal static class MainThreadDispatcher
    {
        private sealed class WorkItem
        {
            public Func<JToken> Work;
            public TaskCompletionSource<JToken> Completion;
        }

        private static readonly ConcurrentQueue<WorkItem> WorkQueue = new ConcurrentQueue<WorkItem>();
        private static readonly ConcurrentQueue<Action> ActionQueue = new ConcurrentQueue<Action>();

        static MainThreadDispatcher()
        {
            EditorApplication.update -= Drain;
            EditorApplication.update += Drain;
        }

        public static Task<JToken> InvokeAsync(Func<JToken> work)
        {
            if (work == null) throw new ArgumentNullException(nameof(work));
            var completion = new TaskCompletionSource<JToken>();
            WorkQueue.Enqueue(new WorkItem { Work = work, Completion = completion });
            return completion.Task;
        }

        public static void Post(Action action)
        {
            if (action != null) ActionQueue.Enqueue(action);
        }

        private static void Drain()
        {
            int actionsProcessed = 0;
            while (actionsProcessed++ < 50 && ActionQueue.TryDequeue(out Action action))
            {
                try { action(); }
                catch { }
            }

            int workProcessed = 0;
            while (workProcessed++ < 25 && WorkQueue.TryDequeue(out WorkItem item))
            {
                try { item.Completion.TrySetResult(item.Work()); }
                catch (Exception ex) { item.Completion.TrySetException(ex); }
            }
        }
    }
}
