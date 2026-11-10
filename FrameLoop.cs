using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewModdingAPI.Utilities;

namespace FpsUnlocker;

/// <summary>
/// Replacement for MonoGame's <c>Game.Tick</c>. Vanilla runs Update and Draw in lockstep at
/// <c>TargetElapsedTime</c> (1/60 s). Here Update still runs on that fixed step, so game speed,
/// SMAPI's UpdateTicked and every tick-counted timer are unchanged, but Draw runs once per loop
/// iteration, paced only by vsync and <see cref="ModConfig.MaxFps"/>.
/// Uses the Game's own timer fields, so switching between this and the original Tick is seamless.
/// </summary>
internal static class FrameLoop
{
    /// <summary>Hard ceiling when nothing else limits the frame rate (vsync off and no cap).</summary>
    private const int SafetyFpsCap = 500;

    private static readonly AccessTools.FieldRef<Game, Stopwatch> GameTimer = AccessTools.FieldRefAccess<Game, Stopwatch>("_gameTimer");
    private static readonly AccessTools.FieldRef<Game, long> PreviousTicks = AccessTools.FieldRefAccess<Game, long>("_previousTicks");
    private static readonly AccessTools.FieldRef<Game, TimeSpan> Accumulated = AccessTools.FieldRefAccess<Game, TimeSpan>("_accumulatedElapsedTime");
    private static readonly AccessTools.FieldRef<Game, TimeSpan> MaxElapsed = AccessTools.FieldRefAccess<Game, TimeSpan>("_maxElapsedTime");
    private static readonly AccessTools.FieldRef<Game, GameTime> GameTimeRef = AccessTools.FieldRefAccess<Game, GameTime>("_gameTime");
    private static readonly AccessTools.FieldRef<Game, bool> SuppressDraw = AccessTools.FieldRefAccess<Game, bool>("_suppressDraw");
    private static readonly AccessTools.FieldRef<Game, bool> ShouldExit = AccessTools.FieldRefAccess<Game, bool>("_shouldExit");
    private static readonly FieldInfo PlatformField = AccessTools.Field(typeof(Game), "Platform");
    private static readonly Action<Game, GameTime> DoUpdate = AccessTools.MethodDelegate<Action<Game, GameTime>>(AccessTools.Method(typeof(Game), "DoUpdate"));
    private static readonly Action<Game, GameTime> DoDraw = AccessTools.MethodDelegate<Action<Game, GameTime>>(AccessTools.Method(typeof(Game), "DoDraw"));

    /// <summary>
    /// SMAPI raises <c>Display.Rendering</c> only once per update tick (<c>SCore.LastRenderEventTick</c>), which in
    /// vanilla equals once per frame. Mods rely on that: UI Info Suite 2 resets its HUD icon slots there, so without
    /// it the icons shift left on every extra frame. Clearing this before each draw restores "once per frame".
    /// </summary>
    private static readonly PerScreen<uint> SmapiLastRenderTick = GetSmapiLastRenderTick();
    public static bool HooksSmapiRendering => SmapiLastRenderTick != null;

    private static long nextFrameAt;
    private static long sleepPrecision = TimeSpan.TicksPerMillisecond;
    private static int updateFrameLag;

    // Stats for the FPS counter, refreshed once per second.
    private static long statsWindowStart;
    private static int drawsInWindow;
    private static int updatesInWindow;
    public static int DrawsPerSecond { get; private set; }
    public static int UpdatesPerSecond { get; private set; }

    /// <summary>Harmony prefix for <c>Game.Tick</c>.</summary>
    public static bool TickPrefix(Game __instance)
    {
        if (!ModEntry.Config.Enabled)
            return true;
        Tick(__instance);
        return false;
    }

    private static void Tick(Game game)
    {
        if (!game.IsActive && game.InactiveSleepTime.TotalMilliseconds >= 1.0)
            Thread.Sleep((int)game.InactiveSleepTime.TotalMilliseconds);

        Stopwatch timer = GameTimer(game);
        WaitForNextFrame(timer);

        long now = timer.Elapsed.Ticks;
        Accumulated(game) += TimeSpan.FromTicks(now - PreviousTicks(game));
        PreviousTicks(game) = now;
        if (Accumulated(game) > MaxElapsed(game))
            Accumulated(game) = MaxElapsed(game);

        GameTime gameTime = GameTimeRef(game);
        TimeSpan step = game.TargetElapsedTime;
        gameTime.ElapsedGameTime = step;
        int updates = 0;
        while (Accumulated(game) >= step && !ShouldExit(game))
        {
            gameTime.TotalGameTime += step;
            Accumulated(game) -= step;
            updates++;
            Interpolator.BeforeUpdate();
            DoUpdate(game, gameTime);
        }
        TrackRunningSlowly(gameTime, updates);
        gameTime.ElapsedGameTime = TimeSpan.FromTicks(step.Ticks * updates);

        if (SuppressDraw(game))
            SuppressDraw(game) = false;
        else
        {
            float alpha = (float)((double)Accumulated(game).Ticks / step.Ticks);
            Interpolator.BeginDraw(alpha, updates > 0);
            if (SmapiLastRenderTick != null)
                SmapiLastRenderTick.Value = uint.MaxValue;
            try
            {
                DoDraw(game, gameTime);
            }
            finally
            {
                Interpolator.EndDraw();
            }
            drawsInWindow++;
        }
        updatesInWindow += updates;
        UpdateStats(now);

        if (ShouldExit(game))
        {
            object platform = PlatformField.GetValue(game);
            AccessTools.Method(platform.GetType(), "Exit").Invoke(platform, null);
            ShouldExit(game) = false;
        }
    }

    /// <summary>Paces frames to the configured cap. Sleeps while far from the deadline and spins the last stretch, like MonoGame's own loop.</summary>
    private static void WaitForNextFrame(Stopwatch timer)
    {
        int cap = ModEntry.Config.MaxFps;
        if (cap <= 0 && !VsyncOn())
            cap = SafetyFpsCap;
        if (cap <= 0)
            return;

        long frameTicks = TimeSpan.TicksPerSecond / cap;
        long now = timer.Elapsed.Ticks;
        // After a stall (loading, alt-tab) don't try to "catch up" with a burst of frames.
        if (nextFrameAt < now - frameTicks)
            nextFrameAt = now;

        while (true)
        {
            long remaining = nextFrameAt - timer.Elapsed.Ticks;
            if (remaining <= 0)
                break;
            if (remaining > sleepPrecision + TimeSpan.TicksPerMillisecond)
            {
                long before = timer.Elapsed.Ticks;
                Thread.Sleep(1);
                long slept = timer.Elapsed.Ticks - before;
                // Track the worst recent oversleep (max 4 ms), decaying slowly so one spike doesn't stick.
                sleepPrecision = Math.Min(4 * TimeSpan.TicksPerMillisecond, Math.Max(slept, sleepPrecision - 100));
            }
            else
                Thread.SpinWait(20);
        }
        nextFrameAt += frameTicks;
    }

    private static PerScreen<uint> GetSmapiLastRenderTick()
    {
        Type core = AccessTools.TypeByName("StardewModdingAPI.Framework.SCore");
        object instance = core == null ? null : AccessTools.Property(core, "Instance")?.GetValue(null);
        return instance == null ? null : AccessTools.Field(core, "LastRenderEventTick")?.GetValue(instance) as PerScreen<uint>;
    }

    private static bool VsyncOn()
    {
        return StardewValley.Game1.graphics?.SynchronizeWithVerticalRetrace ?? true;
    }

    /// <summary>Same heuristic as MonoGame: flag slow running after 5 frames that needed catch-up updates.</summary>
    private static void TrackRunningSlowly(GameTime gameTime, int updates)
    {
        updateFrameLag += Math.Max(0, updates - 1);
        if (gameTime.IsRunningSlowly)
        {
            if (updateFrameLag == 0)
                gameTime.IsRunningSlowly = false;
        }
        else if (updateFrameLag >= 5)
            gameTime.IsRunningSlowly = true;
        if (updates == 1 && updateFrameLag > 0)
            updateFrameLag--;
    }

    private static void UpdateStats(long now)
    {
        if (now - statsWindowStart < TimeSpan.TicksPerSecond)
            return;
        double seconds = (double)(now - statsWindowStart) / TimeSpan.TicksPerSecond;
        DrawsPerSecond = (int)Math.Round(drawsInWindow / seconds);
        UpdatesPerSecond = (int)Math.Round(updatesInWindow / seconds);
        drawsInWindow = updatesInWindow = 0;
        statsWindowStart = now;
    }
}
