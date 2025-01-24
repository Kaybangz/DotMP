﻿using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace DotMP
{
    /// <summary>
    /// Represents the various scheduling strategies for parallel for loops.
    /// Detailed explanations of each scheduling strategy are provided alongside each enumeration value.
    /// If no schedule is specified, the default is <see cref="Schedule.Static"/>.
    /// </summary>
    public enum Schedule
    {
        /// <summary>
        /// The static scheduling strategy.
        /// Iterations are divided amongst threads in round-robin fashion.
        /// Each thread gets a 'chunk' of iterations, determined by the chunk size.
        /// If no chunk size is specified, it's computed as total iterations divided by number of threads.
        /// 
        /// Pros:
        /// - Reduced overhead.
        /// 
        /// Cons:
        /// - Potential for load imbalance.
        /// 
        /// Note: This is the default strategy if none is chosen.
        /// </summary>
        Static,

        /// <summary>
        /// The dynamic scheduling strategy.
        /// Iterations are managed in a central queue.
        /// Threads fetch chunks of iterations from this queue when they have no assigned work.
        /// If no chunk size is defined, a basic heuristic is used to determine a chunk size.
        /// 
        /// Pros:
        /// - Better load balancing.
        /// 
        /// Cons:
        /// - Increased overhead.
        /// </summary>
        Dynamic,

        /// <summary>
        /// The guided scheduling strategy.
        /// Similar to dynamic, but the chunk size starts larger and shrinks as iterations are consumed.
        /// The shrinking formula is based on the remaining iterations divided by the number of threads.
        /// The chunk size parameter sets a minimum chunk size.
        /// 
        /// Pros:
        /// - Adaptable to workloads.
        /// 
        /// Cons:
        /// - Might not handle loops with early heavy load imbalance efficiently.
        /// </summary>
        Guided,

        /// <summary>
        /// Runtime-defined scheduling strategy.
        /// Schedule is determined by the 'OMP_SCHEDULE' environment variable.
        /// Expected format: "schedule[,chunk_size]", e.g., "static,128", "guided", or "dynamic,3".
        /// </summary>
        Runtime
    }

    /// <summary>
    /// Contains all of the scheduling code for parallel for loops.
    /// </summary>
    internal static class Iter
    {
        /// <summary>
        /// Starts and controls a parallel for loop with static scheduling.
        /// </summary>
        /// <typeparam name="T">The type of the local variable for reductions.</typeparam>
        /// <param name="ws">The WorkShare object for state.</param>
        /// <param name="thread_id">The thread ID.</param>
        /// <param name="forAction">The function to be executed.</param>
        internal static void StaticLoop<T>(WorkShare ws, int thread_id, ForAction<T> forAction)
        {
            int tid = thread_id;
            Thr thr = ws.thread;

            thr.curr_iter = (int)(ws.start + tid * ws.chunk_size);
            int end = ws.end;

            T local = default;
            ws.SetLocal(ref local);

            while (thr.curr_iter < end)
                StaticNext(ws, thr, ws.chunk_size, forAction, ref local);

            ws.AddReductionValue(local);
        }

        /// <summary>
        /// Calculates the next chunk of iterations for a static scheduling parallel for loop and executes the appropriate function.
        /// Each time this function is called, the calling thread receives a chunk of iterations to work on, as specified in the Iter.StaticLoop&lt;T&gt; documentation.
        /// </summary>
        /// <typeparam name="T">The type of the local variable for reductions.</typeparam>
        /// <param name="ws">The WorkShare object for state.</param>
        /// <param name="thr">The Thr object for the current thread.</param>
        /// <param name="chunk_size">The chunk size.</param>
        /// <param name="forAction">The function to be executed.</param>
        /// <param name="local">The local variable for reductions.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void StaticNext<T>(WorkShare ws, Thr thr, uint chunk_size, ForAction<T> forAction, ref T local)
        {
            int start = thr.curr_iter;
            int end = (int)Math.Min(thr.curr_iter + chunk_size, ws.end);

            forAction.PerformLoop(ref thr.working_iter, start, end, ref local);

            thr.curr_iter += (int)(ws.num_threads * chunk_size);
        }

        /// <summary>
        /// Specifies a load balancing loop, like dynamic or guided.
        /// </summary>
        /// <typeparam name="T">The type of the local variable for reductions.</typeparam>
        /// <param name="ws">The WorkShare object for state.</param>
        /// <param name="forAction">The function to be executed.</param>
        /// <param name="schedule">The schedule to use.</param>
        internal static void LoadBalancingLoop<T>(WorkShare ws, ForAction<T> forAction, Schedule schedule)
        {
            Thr thr = ws.thread;
            int end = ws.end;

            T local = default;
            if (forAction.IsReduction)
                ws.SetLocal(ref local);

            if (schedule == Schedule.Guided) while (ws.start < end)
                {
                    GuidedNext(ws, thr, forAction, ref local);
                }
            else if (schedule == Schedule.Dynamic) while (ws.start < end)
                {
                    DynamicNext(ws, thr, forAction, ref local);
                }

            ws.AddReductionValue(local);
        }

        /// <summary>
        /// Calculates the next chunk of iterations for a dynamic scheduling parallel for loop and executes the appropriate function.
        /// Each time this function is called, the calling thread receives a chunk of iterations to work on, as specified in the Iter.DynamicLoop&lt;T&gt; documentation.
        /// </summary>
        /// <typeparam name="T">The type of the local variable for reductions.</typeparam>
        /// <param name="ws">The WorkShare object for state.</param>
        /// <param name="thr">The Thr object for the current thread.</param>
        /// <param name="forAction">The function to be executed.</param>
        /// <param name="local">The local variable for reductions.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void DynamicNext<T>(WorkShare ws, Thr thr, ForAction<T> forAction, ref T local)
        {
            int chunk_start = ws.Advance((int)ws.chunk_size);
            int chunk_end = (int)Math.Min(chunk_start + ws.chunk_size, ws.end);

            forAction.PerformLoop(ref thr.working_iter, chunk_start, chunk_end, ref local);
        }

        /// <summary>
        /// Calculates the next chunk of iterations for a guided scheduling parallel for loop and executes the appropriate function.
        /// Each time this function is called, the calling thread receives a chunk of iterations to work on, as specified in the Iter.GuidedLoop&lt;T&gt; documentation.
        /// </summary>
        /// <typeparam name="T">The type of the local variable for reductions.</typeparam>
        /// <param name="ws">The WorkShare object for state.</param>
        /// <param name="thr">The Thr object for the current thread.</param>
        /// <param name="forAction">The function to be executed.</param>
        /// <param name="local">The local variable for reductions.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void GuidedNext<T>(WorkShare ws, Thr thr, ForAction<T> forAction, ref T local)
        {
            int chunk_start, chunk_size;

            lock (ws.ws_lock)
            {
                chunk_start = ws.start;
                chunk_size = (int)Math.Max(ws.chunk_size, (ws.end - chunk_start) / (ws.num_threads * 2));

                ws.Advance(chunk_size);
            }

            int chunk_end = Math.Min(chunk_start + chunk_size, ws.end);

            forAction.PerformLoop(ref thr.working_iter, chunk_start, chunk_end, ref local);
        }
    }
}
