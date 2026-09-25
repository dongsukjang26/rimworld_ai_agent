using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using UnityEngine;
using Verse;

namespace AIAdvisor
{
    /// <summary>
    /// 백그라운드 스레드(에이전트 루프)에서 게임 객체를 읽거나 Unity API를 쓸 때
    /// 메인 스레드로 작업을 넘기는 디스패처. 매 프레임 AdvisorRunner 가 큐를 비운다.
    /// </summary>
    public static class MainThread
    {
        static readonly ConcurrentQueue<Action> queue = new ConcurrentQueue<Action>();

        public static Task<T> Invoke<T>(Func<T> func)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            queue.Enqueue(() =>
            {
                try { tcs.SetResult(func()); }
                catch (Exception e) { tcs.SetException(e); }
            });
            return tcs.Task;
        }

        public static void Post(Action action)
        {
            queue.Enqueue(action);
        }

        internal static void Drain()
        {
            // 한 프레임에 너무 오래 붙잡지 않도록 개수 제한
            for (int n = 0; n < 64 && queue.TryDequeue(out var a); n++)
            {
                try { a(); }
                catch (Exception e) { Log.Error("[AI Advisor] main thread task failed: " + e); }
            }
        }
    }

    /// <summary>
    /// 매 프레임 메인 스레드 작업을 처리하는 Unity 컴포넌트.
    /// GameComponent 와 달리 세이브 파일에 저장되지 않아서, 모드를 빼도 세이브에 흔적이 남지 않는다.
    /// 메인 메뉴, 게임 중, 일시정지 중 모두 동작한다.
    /// </summary>
    public class AdvisorRunner : MonoBehaviour
    {
        static AdvisorRunner instance;

        /// <summary>메인 스레드에서 한 번 호출.</summary>
        public static void Ensure()
        {
            if (instance != null) return;
            var go = new GameObject("AIAdvisorRunner");
            DontDestroyOnLoad(go);
            instance = go.AddComponent<AdvisorRunner>();
        }

        void Update()
        {
            MainThread.Drain();
            // 게임 옵션을 바꾸면 원래 값이 다시 적용되므로 매 프레임 확인
            McpServer.KeepRunningInBackground();
#if SELFTEST
            if (PromoCapture.Enabled) PromoCapture.Tick();
            else SelfTest.TryRun();
#endif
        }

        void OnApplicationQuit()
        {
            McpServer.Stop();
        }
    }
}
